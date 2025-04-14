using NAudio.Vorbis;
using NAudio.Wave.SampleProviders;
using NAudio.Wave;
using SimpleHelpers;
using SonicAudioLib.Archives;
using SonicAudioLib.CriMw;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO.Compression;
using VGAudio.Containers.Hca;
using VGAudio.Containers.Wave;
using VGAudio.Formats.Pcm16;
using VGAudio.Containers.Adx;
using VGAudio.Codecs.CriHca;
using VGAudio.Formats.CriHca;
using NAudio.Utils;
using System.Text.RegularExpressions;
using System.Globalization;

namespace tja2fumen
{
    public class WavData
    {
        public byte[]? Data;
        public int MsDuration;
    }

    public class TJAConvert
    {


        public const int PaddedSongTime = 2 * 1000; // in ms
        public const float TjaOffsetForPaddingSong = -1.0f; // in ms

        public const ulong ns2HcaKey = 52539816150204134;
        private const int TEKA_TEKA_VERSION_NUMBER = 1;
        private const string TEKA_TEKA_VERSION_FIELD = "TekaTekaVersion";

        public enum FileType
        {
            WAV,
            OGG,
            MP3,
            UNK
        }

        private static byte[] BuildCueTable(int msDuration)
        {
            MemoryStream memoryStream = new MemoryStream();
            CriTableWriter writer = CriTableWriter.Create(memoryStream);
            writer.WriteStartTable();
            writer.WriteStartFieldCollection();
            writer.WriteField("CueId", typeof(int));
            writer.WriteField("ReferenceType", typeof(byte));
            writer.WriteField("ReferenceIndex", typeof(UInt16));
            writer.WriteField("UserData", typeof(string));
            writer.WriteField("Worksize", typeof(UInt16));
            writer.WriteField("AisacControlMap", typeof(byte[]));
            writer.WriteField("Length", typeof(UInt32));
            writer.WriteField("NumAisacControlMaps", typeof(byte));
            writer.WriteField("HeaderVisibility", typeof(byte));
            writer.WriteEndFieldCollection();
            writer.WriteStartRow();
            writer.WriteValue("CueId", 0);
            writer.WriteValue("ReferenceType", (byte)3);
            writer.WriteValue("ReferenceIndex", 0);
            writer.WriteValue("UserData", $"{TEKA_TEKA_VERSION_FIELD}:{TEKA_TEKA_VERSION_NUMBER}");
            writer.WriteValue("AisacControlMap", Array.Empty<byte>());
            writer.WriteValue("Length", msDuration);
            writer.WriteValue("NumAisacControlMaps", 0);
            writer.WriteValue("HeaderVisibility", 1);
            writer.WriteEndRow();
            writer.WriteEndTable();
            return memoryStream.GetBuffer();
        }

        private static byte[] BuildCueNameTable(string modName)
        {
            MemoryStream memoryStream = new MemoryStream();
            CriTableWriter writer = CriTableWriter.Create(memoryStream);
            writer.WriteStartTable();
            writer.WriteStartFieldCollection();
            writer.WriteField("CueName", typeof(string));
            writer.WriteField("CueIndex", typeof(int));
            writer.WriteEndFieldCollection();
            writer.WriteStartRow();
            writer.WriteValue("CueName", modName);
            writer.WriteValue("CueIndex", 0);
            writer.WriteEndRow();
            writer.WriteEndTable();
            return memoryStream.GetBuffer();
        }

        private static bool AcbUpdateRequired(string acbPath)
        {
            if (!File.Exists(acbPath)) {
                return true;
            }

            const int bufferSize = 4096;
            CriTable acbFile = new CriTable();
            acbFile.Load(acbPath, bufferSize);
            byte[] cueBytes = acbFile.Rows[0].GetValue<byte[]>("CueTable");
            if (cueBytes == null) {
                return true;
            }

            Regex r = new Regex(@$"^{TEKA_TEKA_VERSION_FIELD}:(?<version>\d+)$", RegexOptions.None, TimeSpan.FromMilliseconds(150));
            using (CriTableReader reader = CriTableReader.Create(cueBytes))
            {
                reader.Read();
                Match m = r.Match(reader.GetValue<string>("UserData"));
                int version = 0;
                if (m.Success) {
                    var versionString = m.Result("${version}");
                    version = Int32.Parse(versionString, CultureInfo.InvariantCulture);
                }
                return version < TEKA_TEKA_VERSION_NUMBER;
            }
        }

        private static void Pack(string acbPath, string songId, string path, int msDuration)
        {
            const int bufferSize = 4096;

            if (!File.Exists(acbPath))
                throw new FileNotFoundException("Unable to locate the corresponding ACB file. Please ensure that it's in the same directory.");

            CriTable acbFile = new CriTable();
            acbFile.Load(acbPath, bufferSize);
            CriAfs2Archive afs2Archive = new CriAfs2Archive();

            using (CriTableReader reader = CriTableReader.Create((byte[])acbFile.Rows[0]["WaveformTable"]))
            {
                while (reader.Read())
                {
                    ushort id = reader.ContainsField("MemoryAwbId") ? reader.GetUInt16("MemoryAwbId") : reader.GetUInt16("Id");

                    string inputName = id.ToString("D5");

                    inputName += ".hca";
                    inputName = Path.Combine(path, inputName);

                    if (!File.Exists(inputName))
                        throw new FileNotFoundException($"Unable to locate {inputName}");

                    CriAfs2Entry entry = new CriAfs2Entry
                    {
                        FilePath = new FileInfo(inputName),
                        Id = id
                    };
                    
                    afs2Archive.Add(entry);
                }
            }

            acbFile.Rows[0]["AwbFile"] = null;
            acbFile.Rows[0]["StreamAwbAfs2Header"] = null;
            acbFile.Rows[0]["CueNameTable"] = BuildCueNameTable(songId);
            acbFile.Rows[0]["CueTable"] = BuildCueTable(msDuration);

            CriCpkArchive cpkArchive = new CriCpkArchive();
            CriCpkArchive extCpkArchive = new CriCpkArchive();
            cpkArchive.Mode = extCpkArchive.Mode = CriCpkMode.Id;

            if (afs2Archive.Count > 0 || cpkArchive.Count > 0)
                acbFile.Rows[0]["AwbFile"] = afs2Archive.Save();

            acbFile.WriterSettings = CriTableWriterSettings.Adx2Settings;
            acbFile.Save(acbPath, bufferSize);
        }

        public static bool ConvertToAcb(string filePath, string songId, FileType fileType, bool isPreview = false, int milisecondsOffset = 0)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath) ?? "";
                string fileName = Path.GetFileNameWithoutExtension(filePath) + ".acb";
                if (isPreview)
                {
                    fileName = "P" + fileName;
                }
                var acbPath = Path.Combine(directory, fileName);

                var keepAcb = true;
                var updateRequired = AcbUpdateRequired(acbPath);
                if (!updateRequired  && keepAcb)
                {
                    return true;
                }
                string hcaPath = $"{directory}/00000.hca";

                WavData? hca = null;

                switch (fileType)
                {
                    case FileType.WAV:
                        hca = WavToHca(filePath, isPreview, milisecondsOffset);
                        break;

                    case FileType.OGG:
                        hca = OggToHca(filePath, isPreview, milisecondsOffset);
                        break;

                    case FileType.MP3:
                        hca = Mp3ToHca(filePath, isPreview, milisecondsOffset);
                        break;

                    case FileType.UNK:
                    default:
                        hca = null;
                        break;
                }
                
                if(hca == null || hca.Data == null)
                {
                    return false;
                }

                File.WriteAllBytes(acbPath, Files.TemplateACBData);
                File.WriteAllBytes(hcaPath, hca.Data);
                Pack(acbPath, songId, directory, hca.MsDuration);
                File.Delete(hcaPath);
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return false;
            }
        }

        public static int WriteWavFileToStream(Stream outStream, IWaveProvider sourceProvider)
        {
            using WaveFileWriter waveFileWriter = new WaveFileWriter(new IgnoreDisposeStream(outStream), sourceProvider.WaveFormat);
            byte[] array = new byte[sourceProvider.WaveFormat.AverageBytesPerSecond * 4];
            while (true) {
                int num = sourceProvider.Read(array, 0, array.Length);
                if (num == 0) {
                    break;
                }

                waveFileWriter.Write(array, 0, num);
            }

            outStream.Flush();
            return (int) waveFileWriter.TotalTime.TotalMilliseconds;
        }

        private static WavData ConvertToHca(SampleToWaveProvider16 wavProvider, bool isPreview, int milisecondsOffset = 0)
        {
            var memoryStream = new MemoryStream();

            TimeSpan delay = TimeSpan.Zero;
            TimeSpan skip = TimeSpan.Zero;
            TimeSpan take = TimeSpan.Zero;

            if (isPreview)
            {
                skip = TimeSpan.FromMilliseconds(milisecondsOffset);
                take = TimeSpan.FromMilliseconds(15000);
            }
            else
            {
                delay = TimeSpan.FromMilliseconds(milisecondsOffset);
            }

            int msDuration = 0;
            if (milisecondsOffset > 0 || isPreview)
            {
                var trimmed = new OffsetSampleProvider(wavProvider.ToSampleProvider())
                {
                    DelayBy = delay,
                    SkipOver = skip,
                    Take = take
                };
                
                msDuration = WriteWavFileToStream(memoryStream, trimmed.ToWaveProvider16());
            }
            else
            {
                msDuration = WriteWavFileToStream(memoryStream, wavProvider);
            }
            var hcaWriter = new HcaWriter();
            hcaWriter.Configuration.EncryptionKey = new CriHcaKey(ns2HcaKey);

            var waveReader = new WaveReader();
            var audioData = waveReader.Read(memoryStream.ToArray());

            var encodingConfig = new CriHcaParameters
            {
                Progress = hcaWriter.Configuration.Progress,
                Bitrate = hcaWriter.Configuration.Bitrate,
                LimitBitrate = hcaWriter.Configuration.LimitBitrate
            };
            if (hcaWriter.Configuration.Quality != CriHcaQuality.NotSet)
            {
                encodingConfig.Quality = hcaWriter.Configuration.Quality;
            }

            var hcaFormat = audioData.GetFormat<CriHcaFormat>(encodingConfig);
            var Hca = hcaFormat.Hca;
            HcaEncryption.CriHcaEncryption.Crypt(Hca, hcaFormat.AudioData, hcaWriter.Configuration.EncryptionKey, false);


            return new WavData {
                Data = hcaWriter.GetFile(audioData, hcaWriter.Configuration),
                MsDuration = msDuration
            };
        }

        private static WavData? WavToHca(string path, bool isPreview = false, int milisecondsOffset = 0)
        {
            
            WaveFileReader reader = new WaveFileReader(path);
            var wavProvider = new SampleToWaveProvider16(reader.ToSampleProvider());
            return ConvertToHca(wavProvider, isPreview, milisecondsOffset);
        }

        private static WavData? OggToHca(string inPath, bool isPreview = false, int milisecondsOffset = 0)
        {
            try
            {
                using FileStream fileIn = new FileStream(inPath, FileMode.Open);
                var vorbis = new VorbisWaveReader(fileIn);
                
                var wavProvider = new SampleToWaveProvider16(vorbis.ToSampleProvider());
                return ConvertToHca(wavProvider, isPreview, milisecondsOffset);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return null;
            }
        }


        private static WavData? Mp3ToHca(string inPath, bool isPreview = false, int milisecondsOffset = 0)
        {
            try
            {
                using FileStream fileIn = new FileStream(inPath, FileMode.Open);
                var mp3 = new Mp3FileReader(fileIn);
                
                
                var wavProvider = new SampleToWaveProvider16(mp3.ToSampleProvider());
                return ConvertToHca(wavProvider, isPreview, milisecondsOffset);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return null;
            }
        }
    }
}
