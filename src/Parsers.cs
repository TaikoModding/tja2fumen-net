using SimpleHelpers;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.SymbolStore;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace tja2fumen
{
    public static class Parsers
    {
        public static TJASong ParseTja(string tjaFileName)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = FileEncoding.DetectFileEncoding(tjaFileName);

            List<string> tjaLines = File.ReadLines(tjaFileName, encoding).ToList()
                                        .Where(line =>
                                               { return line.Trim() != ""; })
                                        .ToList();
            TJASong tja = SplitTjaLinesIntoCourses(tjaLines);
            foreach (string key in tja.courses.Keys)
            {

                (Dictionary<string, List<TJAMeasure>> branches, Dictionary<string, List<string>> balloonData, bool hasBranches) =
                    ParseTjaCourseData(tja.courses[key].data);
                TJACourse course = tja.courses[key];
                course.branches = branches;
                course.balloon = FixBalloonField(course.balloon, balloonData);
                course.hasBranches = hasBranches;
                tja.courses[key] = course;
            }

            return tja;
        }

        private static TJASong SplitTjaLinesIntoCourses(List<string> lines)
        {
            lines = lines
                        .Where(line =>
                               { return line.Split("//") [0].Trim() != ""; })
                        .Select(line =>
                                { return line.Split("//") [0].Trim(); })
                        .ToList();

            Dictionary<string, float> tjaMetadata = new Dictionary<string, float>();
            foreach (string requiredMetadata in new string[] { "BPM", "OFFSET" })
            {
                bool metadataFound = false;
                foreach (string line in lines)
                {
                    if (line.StartsWith(requiredMetadata))
                    {
                        // Potencial problem, if some tja file uses , as decimal separator, this wont work
                        tjaMetadata[requiredMetadata] = float.Parse(line.Split(":")[1], CultureInfo.InvariantCulture);
                        metadataFound = true;
                        break;
                    }
                }
                if (!metadataFound)
                {
                    throw new Exception("TJA does not contain required " + $"'{requiredMetadata}' metadata.");
                }
            }
            TJASong parsedTja = new TJASong { bpm = tjaMetadata["BPM"], offset = tjaMetadata["OFFSET"],
                                              courses = new Dictionary<string, TJACourse>() };

            parsedTja.metadata = ParseMetadata(lines);

            Constants.TJA_COURSE_NAMES.ForEach(
                course =>
                {
                    parsedTja.courses[course] =
                        new TJACourse { bpm = tjaMetadata["BPM"], offset = tjaMetadata["OFFSET"], course = course };
                });

            string currentCourse = "Oni";
            string currentCourseBasename = "Oni";
            bool readingNotes = false;

            foreach (string line in lines)
            {
                var matchMetadata = Regex.Match(line, @"^([a-zA-Z0-9]+):(.*)");
                var matchStart = Regex.Match(line, @"^#START(?:\s+(.+))?");
                var matchEnd = Regex.Match(line, @"^#END(?:\s+(.+))?");

                if (matchMetadata.Success)
                {
                    string nameUpper = matchMetadata.Groups[1].Value.ToUpper();
                    string value = matchMetadata.Groups[2].Value.Trim();

                    TJACourse course;
                    switch (nameUpper)
                    {
                    case "COURSE":
                        value = value.ToLower();
                        value = value[0].ToString().ToUpper() + value.Substring(1).ToLower();

                        if (!Constants.NORMALIZE_COURSE.ContainsKey(value))
                        {
                            throw new Exception($"Invalid COURSE value: '{value}'");
                        }
                        currentCourse = Constants.NORMALIZE_COURSE[value];
                        currentCourseBasename = currentCourse;
                        break;
                    case "LEVEL":
                        if (!int.TryParse(value, out _))
                        {
                            throw new Exception($"Invalid LEVEL value: '{value}'");
                        }
                        var parsedLevel = Math.Min(Math.Max(int.Parse(value), 1), 10);
                        course = parsedTja.courses[currentCourse];
                        course.level = parsedLevel;
                        parsedTja.courses[currentCourse] = course;
                        break;

                    case "SCOREMODE":
                        course = parsedTja.courses[currentCourse];
                        course.scoreMode = int.Parse(value);
                        break;
                    case "SCOREINIT":
                        course = parsedTja.courses[currentCourse];
                            if (value.Contains(","))
                        {
                                Console.WriteLine($"SCOREINIT split: {value.Split(',')[0]} {value.Split(',')[1]}");
                                // This is an explicit initializer of scoreInit and shinuti
                                course.shinutiScore = int.Parse(value.Split(',')[1]);
                                course.scoreInit = int.Parse(value.Split(',')[0]);
                        } else
                        {
                                course.scoreInit = value != "" ? int.Parse(value) : 300;
                        }
                        parsedTja.courses[currentCourse] = course;
                        break;
                    case "SCOREDIFF":
                        course = parsedTja.courses[currentCourse];
                        course.scoreDiff = value != "" ? int.Parse(value.Split(",").Last()) : 120;
                        parsedTja.courses[currentCourse] = course;
                        break;
                    case "BALLOON":
                        course = parsedTja.courses[currentCourse];

                        if (value != "")
                        {
                                List<int> balloons;
                                try
                                {
                                    if (value.EndsWith(','))
                                    {
                                        value = value[..^1];
                                    }
                            balloons = value.Split(",")
                                                     .Select(v =>
                                                             { return int.Parse(v); })
                                                     .ToList();
                                }
                                catch
                                {
                                    balloons = new List<int>();
                                };
                            course.balloon = balloons;
                        }

                        parsedTja.courses[currentCourse] = course;
                        break;
                    case "STYLE":
                        if (value == "Single")
                        {
                            currentCourse = currentCourseBasename;
                        }
                        break;
                    }
                }
                else if (matchStart.Success)
                {
                    readingNotes = true;
                    string value = matchStart.Groups[1].Value != "" ? matchStart.Groups[1].Value : "";
                    if (new string[] { "1P", "2P" }.Contains(value))
                    {
                        value = value[1].ToString() + value[0].ToString();
                    }

                    if (new string[] { "P1", "P2" }.Contains(value))
                    {
                        currentCourse = currentCourseBasename + value;
                        var baseCourse = parsedTja.courses[currentCourseBasename];
                        parsedTja.courses[currentCourse] =
                            new TJACourse { bpm = baseCourse.bpm,
                                            offset = baseCourse.offset,
                                            course = baseCourse.course,
                                            level = baseCourse.level,
                                            balloon = new List<int>(baseCourse.balloon.ToArray()),
                                            scoreInit = baseCourse.scoreInit,
                                            scoreDiff = baseCourse.scoreDiff,
                                            data = new List<string>(baseCourse.data.ToArray()),

                            };
                        parsedTja.courses[currentCourse].data.Clear();
                    }
                    else if (value != "")
                    {
                        throw new Exception($"Invalid value `{value}` for #START.");
                    }
                    parsedTja.courses[currentCourse].data.Add("#START");
                }
                else if (matchEnd.Success)
                {
                    readingNotes = false;
                }
                else
                {
                    if (currentCourse != "" && readingNotes)
                    {
                        parsedTja.courses[currentCourse].data.Add(line);
                    }
                    else
                    {
#if DEBUG
                        Console.WriteLine("Data encountered before first COURSE: " +
                                          $"'{line}' (Check for typos in TJA)");
#endif
                    }
                }
            }

            foreach (string courseName in Constants.COURSE_NAMES)
            {
                var courseSinglePlayer = parsedTja.courses[courseName];
                var coursePlayerOne = parsedTja.courses[courseName + "P1"];
                if (coursePlayerOne.data.Count > 0 && courseSinglePlayer.data.Count == 0)
                {
                    var baseCourse = coursePlayerOne;
                    parsedTja.courses[courseName] =
                        new TJACourse { bpm = baseCourse.bpm,
                                        offset = baseCourse.offset,
                                        course = baseCourse.course,
                                        level = baseCourse.level,
                                        balloon = new List<int>(baseCourse.balloon.ToArray()),
                                        scoreInit = baseCourse.scoreInit,
                                        scoreDiff = baseCourse.scoreDiff,
                                        data = new List<string>(baseCourse.data.ToArray()),

                        };
                }
            }

            foreach (string courseName in parsedTja.courses.Keys)
            {
                if (parsedTja.courses[courseName].data.Count == 0)
                {
                    parsedTja.courses.Remove(courseName);
                }
            }

            // Not sure if dict are ordered on c#, but whatever...
            Dictionary<string, TJACourse> orderedDict = new Dictionary<string, TJACourse>();
            foreach ((string courseName, TJACourse value) in parsedTja.courses.OrderBy(x => x.Key))
            {
                orderedDict.Add(courseName, value);
            }
            parsedTja.courses = orderedDict;

            return parsedTja;
        }

        private static List<int> FixBalloonField(List<int> balloonField, Dictionary<string, List<string>> balloonData)
        {
            if (!balloonData.Values.All(balloons =>
                                        { return balloons.Count > 0; }))
            {
                return balloonField;
            }

            if (balloonData.Values.All(balloons =>
                                       { return balloons.Count == balloonField.Count; }))
            {
                // baloonField * 3
                return balloonField.Concat(balloonField).Concat(balloonField).ToList();
            }

            if (!balloonData.Values.Any(balloons =>
                                        { return balloons.Contains("DUPE"); }))
            {
                return balloonField;
            }

            var totalNumBalloons = balloonData.Values.Sum(balloons => balloons.Count);
            if (!(balloonField.Count < totalNumBalloons))
            {
                return balloonField;
            }

            List<int> duplicatedBalloons = new List<int>();
            List<int> balloonFieldFixed = new List<int>();

            foreach (var balloonNote in balloonData["normal"])
            {
                int balloonHits = balloonField[0];
                balloonField.RemoveAt(0);
                if (balloonNote == "DUPE")
                {
                    duplicatedBalloons.Add(balloonHits);
                }
                balloonFieldFixed.Add(balloonHits);
            }

            foreach (string branchName in new string[] { "professional", "master" })
            {
                List<int> dupesToCopy = new List<int>(duplicatedBalloons.ToArray());
                foreach (var balloonNote in balloonData[branchName])
                {
                    if (balloonNote == "DUPE")
                    {
                        balloonFieldFixed.Add(dupesToCopy[0]);
                        dupesToCopy.RemoveAt(0);
                    }
                    else
                    {
                        balloonFieldFixed.Add(balloonField[0]);
                        balloonField.RemoveAt(0);
                    }
                }
            }

            return balloonFieldFixed;
        }

        private static void CheckBranchLength(Dictionary<string, List<TJAMeasure>> parsedBranches, string branchName,
                                              int expectedLen = 0)
        {
            int branchLen = parsedBranches[branchName].Count;
            string warningMsg;
            if (expectedLen == 0)
            {
                string maxBranchName = branchName;
                expectedLen = branchLen;
                foreach ((string name, List<TJAMeasure> branch) in parsedBranches)
                {
                    if (branch.Count > expectedLen)
                    {
                        expectedLen = branch.Count;
                        maxBranchName = name;
                    }
                }
                warningMsg = $"To fix this, measures will be copied from the " +
                             $"'{maxBranchName}' branch to equalize branch " + $"lengths.";
                for (int idx_m = branchLen; idx_m < expectedLen; idx_m++)
                {
                    parsedBranches[branchName].Add(parsedBranches[maxBranchName][idx_m]);
                }
            }
            else
            {
                warningMsg = "To fix this, empty measures will be added to " + "equalize branch lengths.";

                for (int idx_m = branchLen; idx_m < expectedLen; idx_m++)
                {
                    parsedBranches[branchName].Add(new TJAMeasure());
                }
            }

            if (branchLen < expectedLen)
            {
                Console.WriteLine("While parsing the TJA's branches, tja2fumen expected " +
                                  $"{expectedLen} measure(s) from the '{branchName}' branch, but " +
                                  $"it only had {branchLen} measure(s). {warningMsg} (Hint: Do " +
                                  $"#N, #E, and #M all have the same number of measures?)");
            }
        }


        private static TJASongMetadata ParseMetadata(List<string> lines)
        {
            TJASongMetadata metadata = new TJASongMetadata();

            foreach (string line in lines)
            {
                var matchMetadata = Regex.Match(line, @"^([a-zA-Z0-9]+):(.*)");
                if (matchMetadata.Success)
                {
                    string nameUpper = matchMetadata.Groups[1].Value.ToUpper();
                    string value = matchMetadata.Groups[2].Value.Trim();
                    switch (nameUpper)
                    {
                        case "TITLE":
                        case "TITLEJA":
                        case "TITLEEN":
                        case "TITLECN":
                        case "TITLETW":
                        case "TITLEKO":
                            metadata.SetTitle(nameUpper, value);
                            break;

                        case "SUBTITLE":
                        case "SUBTITLEJA":
                        case "SUBTITLEEN":
                        case "SUBTITLECN":
                        case "SUBTITLETW":
                        case "SUBTITLEKO":
                            metadata.SetSubtitle(nameUpper, value);
                            break;

                        case "MAKER":
                            metadata.maker = value;
                            break;

                        case "DEMOSTART":
                            float.TryParse(value, out metadata.demoStart);
                            break;

                        case "WAVE":
                            metadata.wave = value;
                            break;

                        case "GENRE":
                            metadata.genre = value;
                            break;

                    }
                }
            }

            metadata.NormilizeTitle();
            metadata.NormilizeSubtitle();

            return metadata;
        }

        private static (Dictionary<string, List<TJAMeasure>>, Dictionary<string, List<string>>, bool)
            ParseTjaCourseData(List<string> data)
        {
            Dictionary<string, List<TJAMeasure>> parsedBranches = new Dictionary<string, List<TJAMeasure>>();
            Constants.BRANCH_NAMES.ToList().ForEach(k =>
                                                    { parsedBranches[k] = new List<TJAMeasure> { new TJAMeasure() }; });
            bool hasBranches = data.Any(d =>
                                        { return d.StartsWith("#BRANCH"); });
            string currentBranch = hasBranches ? "all" : "normal";
            string branchCondition = "";

            Dictionary<string, List<string>> balloons = new Dictionary<string, List<string>>();
            Constants.BRANCH_NAMES.ToList().ForEach(k =>
                                                    { balloons[k] = new List<string>(); });

            int idx_m = 0;
            int idx_m_branchstart = 0;

            for (int idx_l = 0; idx_l < data.Count; idx_l++)
            {
                string line = data[idx_l];
                string command = "", name = "", value = "", noteData = "";
                var matchCommand = Regex.Match(line, @"^#([a-zA-Z0-9]+)(?:\s+(.+))?");
                if (matchCommand.Success)
                {
                    command = matchCommand.Groups[1].Value.ToUpper();
                    if (matchCommand.Groups[2].Success)
                    {
                        value = matchCommand.Groups[2].Value;
                    }
                }
                else
                {
                    noteData = line;
                }

                if (noteData != "")
                {
                    string notesToWrite = "";

                    if (noteData.EndsWith(","))
                    {
                        foreach (string branchName in currentBranch == "all" ? Constants.BRANCH_NAMES
                                                                             : new string[] { currentBranch })
                        {
                            CheckBranchLength(parsedBranches, branchName, idx_m + 1);
                            notesToWrite = noteData[..^ 1];
                            if (notesToWrite != "")
                            {
                                parsedBranches[branchName][idx_m].notes.AddRange(
                                    notesToWrite.Select(x => new string(x, 1)));
                            }
                            parsedBranches[branchName].Add(new TJAMeasure());
                        }
                        idx_m++;
                    }
                    else
                    {
                        foreach (string branchName in currentBranch == "all" ? Constants.BRANCH_NAMES
                                                                             : new string[] { currentBranch })
                        {
                            notesToWrite = noteData;
                            if (notesToWrite != "")
                            {
                                parsedBranches[branchName][idx_m].notes.AddRange(
                                    notesToWrite.Select(x => new string(x, 1)));
                            }
                        }
                    }

                    List<string> balloonsNotes = new List<string>();
                    notesToWrite.ToList().ForEach(n =>
                                                  {
                                                      if (n == '7' || n == '9')
                                                      {
                                                          balloonsNotes.Add(n.ToString());
                                                      }
                                                  });

                    balloonsNotes = currentBranch == "all" ? Enumerable.Repeat("DUPE", balloonsNotes.Count).ToList()
                                                           : balloonsNotes;

                    foreach (string branchName in currentBranch == "all" ? Constants.BRANCH_NAMES
                                                                         : new string[] { currentBranch })
                    {
                        balloons[branchName].AddRange(balloonsNotes);
                    }
                }

                else if (new string[] { "GOGOSTART", "GOGOEND", "BARLINEON", "BARLINEOFF", "DELAY", "SCROLL",
                                        "BPMCHANGE", "MEASURE", "LEVELHOLD", "SENOTECHANGE", "SECTION", "BRANCHSTART" }
                             .Contains(command))
                {
                    int pos = 0;
                    foreach (string branchName in currentBranch == "all" ? Constants.BRANCH_NAMES
                                                                         : new string[] { currentBranch })
                    {
                        CheckBranchLength(parsedBranches, branchName, idx_m + 1);
                        pos = parsedBranches[branchName][idx_m].notes.Count;
                    }

                    switch (command)
                    {
                    case "GOGOSTART":
                        name = "gogo";
                        value = "1";
                        break;
                    case "GOGOEND":
                        name = "gogo";
                        value = "0";
                        break;
                    case "BARLINEON":
                        name = "barline";
                        value = "1";
                        break;
                    case "BARLINEOFF":
                        name = "barline";
                        value = "0";
                        break;
                    case "DELAY":
                        name = "delay";
                        break;
                    case "SCROLL":
                        name = "scroll";
                        break;
                    case "BPMCHANGE":
                        name = "bpm";
                        break;
                    case "MEASURE":
                        name = "measure";
                        break;
                    case "LEVELHOLD":
                        name = "levelhold";
                        break;
                    case "SENOTECHANGE":
                        name = "senote";
                        break;
                    case "SECTION":

                        if (data[idx_l + 1].StartsWith("#BRANCHSTART"))
                        {
                            name = "section";
                            currentBranch = "all";
                        }
                        else if (branchCondition == null || branchCondition == "")
                        {
                            name = "section";
                            currentBranch = "all";
                        }
                        else
                        {
                            name = "branch_start";
                            value = branchCondition;
                        }
                        break;

                    case "BRANCHSTART":

                        currentBranch = "all";
                        name = "branch_start";
                        branchCondition = value;
                        foreach (var branchName in Constants.BRANCH_NAMES)
                        {
                            CheckBranchLength(parsedBranches, branchName);
                        }
                        idx_m_branchstart = idx_m;
                        break;
                    }

                    foreach(string branchName in currentBranch == "all" ? Constants.BRANCH_NAMES
                                                                             : new string[] { currentBranch })
                    {
                        CheckBranchLength(parsedBranches, branchName, idx_m + 1);
                        parsedBranches[branchName][idx_m].events.Add(new TJAData { name = name, value = value, pos = pos });
                    }
                }
                else
                {
                    switch (command)
                    {
                    case "START":
                    case "END":
                        currentBranch = hasBranches ? "all" : "normal";
                        break;

                    case "N":
                        currentBranch = "normal";
                        idx_m = idx_m_branchstart;
                        break;
                    case "E":
                        currentBranch = "professional";
                        idx_m = idx_m_branchstart;
                        break;
                    case "M":
                        currentBranch = "master";
                        idx_m = idx_m_branchstart;
                        break;
                    case "BRANCHEND":
                        currentBranch = "all";
                        break;
                    default:
#if DEBUG
                        Console.WriteLine($"Ignoring unsopported command '{command}'");
#endif
                        break;
                    }
                }
            }

            bool deletedBranches = false;
            foreach (var branch in parsedBranches.Values)
            {
                if (branch[^1].notes.Count == 0 && branch[^1].events.Count == 0)
                {
                    branch.RemoveAt(branch.Count - 1);
                    deletedBranches = true;
                }
            }
            if (deletedBranches)
            {
                idx_m -= 1;
            }

            foreach ((string branchName, List<TJAMeasure> branch) in parsedBranches)
            {
                if (branch.Count > 0)
                {
                    CheckBranchLength(parsedBranches, branchName);
                }
            }

            foreach ((string branchName, List<TJAMeasure> branch) in parsedBranches)
            {
                foreach (TJAMeasure measure in branch)
                {
                    List<string> validNotes = new List<string>();
                    foreach (string note in measure.notes)
                    {
                        if (!Constants.TJA_NOTE_TYPES.ContainsKey(note))
                        {
                            Console.WriteLine($"Ignoring invalid note '{note}' in measure " +
                                              $"'{string.Join("", measure.notes)}' (check for " + $"typos in TJA)");
                        }
                        else
                        {
                            validNotes.Add(note);
                        }
                    }

                    List<TJAData> notes = new List<TJAData>();
                    for (int i = 0; i < validNotes.Count; i++)
                    {
                        string note = validNotes[i];
                        if (Constants.TJA_NOTE_TYPES[note] != "Blank")
                        {
                            notes.Add(new TJAData { name = "note", value = Constants.TJA_NOTE_TYPES[note], pos = i });
                        }
                    }
                    List<TJAData> events = measure.events;

                    while (notes.Count > 0 || events.Count > 0)
                    {
                        if (notes.Count > 0 && events.Count > 0)
                        {
                            if (notes[0].pos >= events[0].pos)
                            {
                                measure.combined.Add(events[0]);
                                events.RemoveAt(0);
                            }
                            else
                            {
                                measure.combined.Add(notes[0]);
                                notes.RemoveAt(0);
                            }
                        }
                        else if (events.Count > 0)
                        {
                            measure.combined.Add(events[0]);
                            events.RemoveAt(0);
                        }
                        else if (notes.Count > 0)
                        {
                            measure.combined.Add(notes[0]);
                            notes.RemoveAt(0);
                        }
                    }
                }
            }

            if (hasBranches)
            {
                HashSet<int> ints = new HashSet<int>();
                foreach (var b in parsedBranches.Values)
                {
                    ints.Add(b.Count);
                };
                if (ints.Count != 1)
                {
                    throw new Exception("Branches do not have the same number of measures. (This " +
                                        "check was performed prior to splitting up the measures due " +
                                        "to mid-measure commands. Please check the number of ',' you " +
                                        "have in each branch.)");
                }
            }

            return (parsedBranches, balloons, hasBranches);
        }
    }
}
