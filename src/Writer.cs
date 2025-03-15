using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tja2fumen
{
    public static class Writer
    {
        public static byte[] getFumenBytes(FumenCourse song)
        {
            MemoryStream bytes = new MemoryStream();

            using (BinaryWriter bw = new BinaryWriter(bytes))
            {
                bw.Write(song.header.rawBytes);

                Console.WriteLine($"FIRST OFFSET: {song.measures[0].offsetStart}");
                foreach (FumenMeasure measure in song.measures)
                {

                    bw.Write(measure.bpm);
                    bw.Write(measure.offsetStart);
                    bw.Write(measure.gogo);
                    bw.Write(measure.barline);

                    bw.Write((ushort)measure.padding1);
                    foreach (int branchInfo in measure.branchInfo)
                    {
                        bw.Write(branchInfo);
                    }
                    bw.Write(measure.padding2);

                    foreach (string branchName in Constants.BRANCH_NAMES)
                    {
                        FumenBranch branch = measure.branches[branchName];
                        bw.Write((ushort)branch.length);
                        bw.Write((ushort)branch.padding);
                        bw.Write(branch.speed);

                        foreach (FumenNote note in branch.notes)
                        {
                            bw.Write(Constants.FUMEN_TYPE_NOTES[note.noteType]);

                            bw.Write(note.pos);

                            bw.Write(note.item);
                            bw.Write(note.padding);

                            if (note.hits != 0)
                            {
                                bw.Write((ushort)note.hits);
                                bw.Write((ushort)note.hitsPadding);
                            }
                            else
                            {
                                bw.Write((ushort)Math.Min(65535, note.scoreInit));
                                bw.Write((ushort)Math.Min(65535, note.scoreDiff * 4));
                            }

                            bw.Write(note.duration);

                            if (note.noteType.ToLower() == "drumroll")
                            {
                                bw.Write(note.drumrollBytes);
                            }
                        }
                    }
                }
            }

            bytes.Flush();
            return bytes.ToArray();
        }
    }
}
