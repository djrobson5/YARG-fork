using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YARG.Core;
using YARG.Scores.Sync;
using static YARG.ScoreSyncTests.Fixtures;

namespace YARG.ScoreSyncTests
{
    public class FileFormatTests
    {
        private static readonly Guid Alice = new("aaaaaaaa-0000-0000-0000-000000000001");

        private static ScoreSyncFile SampleFile()
        {
            var data = new ScoreSyncData();
            data.Profiles.Add(Profile(Alice, "Alice"));
            data.Players.Add(Player(Alice, "Alice"));
            var score = Score(Alice, 1000);
            score.Percent = null;
            score.EnginePresetId = Guid.NewGuid();
            data.Games.Add(Game(SongA, T0 + 1234567, 1000, score));
            var harm = Completion(Alice, SongB, 3, 7, T0 + 42);
            harm.Instrument = Instrument.Harmony;
            harm.HarmonyIndex = 2;
            data.SectionCompletions.Add(harm);
            data.SectionProgress.Add(Progress(Alice, SongB, 7, 1, T0 + 42));
            return File(data);
        }

        private static byte[] Gzip(string json)
        {
            using var stream = new MemoryStream();
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                gzip.Write(bytes, 0, bytes.Length);
            }

            return stream.ToArray();
        }

        private static string Json(ScoreSyncFile file)
        {
            var bytes = ScoreSyncFileFormat.WriteToBytes(file);
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return reader.ReadToEnd();
        }

        private static SyncReadResult ReadEdited(Action<JObject> edit)
        {
            var root = JObject.Parse(Json(SampleFile()));
            edit(root);
            return ScoreSyncFileFormat.ReadFromBytes(Gzip(root.ToString()));
        }

        [Test]
        public void RoundTripPreservesEveryField()
        {
            var original = SampleFile();

            var result = ScoreSyncFileFormat.ReadFromBytes(ScoreSyncFileFormat.WriteToBytes(original));

            Assert.That(result.Status, Is.EqualTo(SyncReadStatus.Ok), result.Error);
            var read = result.File;
            Assert.That(read.Format, Is.EqualTo(ScoreSyncFile.CURRENT_FORMAT));
            Assert.That(read.DeviceId, Is.EqualTo(original.DeviceId));
            Assert.That(read.DeviceName, Is.EqualTo(original.DeviceName));
            Assert.That(read.AppVersion, Is.EqualTo(original.AppVersion));
            Assert.That(read.ExportedAt, Is.EqualTo(original.ExportedAt));
            Assert.That(read.ExportedAt.Kind, Is.EqualTo(DateTimeKind.Utc));

            // Field-by-field, via the same serializer: anything lost on the way shows up as a
            // difference between the two JSON documents
            Assert.That(Json(read), Is.EqualTo(Json(original)));

            var game = read.Games.Single();
            Assert.That(game.DateTicks, Is.EqualTo(T0 + 1234567));
            Assert.That(game.SongChecksum, Is.EqualTo(SongA));
            Assert.That(game.PlayerScores.Single().Percent, Is.Null);
            Assert.That(read.SectionCompletions.Single().Instrument, Is.EqualTo(Instrument.Harmony));
            Assert.That(read.SectionCompletions.Single().HarmonyIndex, Is.EqualTo(2));
        }

        [Test]
        public void OutputIsGzip()
        {
            var bytes = ScoreSyncFileFormat.WriteToBytes(SampleFile());

            Assert.That(bytes[0], Is.EqualTo(0x1f));
            Assert.That(bytes[1], Is.EqualTo(0x8b));
        }

        [Test]
        public void WriteLeavesTheStreamOpen()
        {
            using var stream = new MemoryStream();
            ScoreSyncFileFormat.Write(stream, SampleFile());

            Assert.That(stream.CanWrite, Is.True);
            stream.Position = 0;
            Assert.That(ScoreSyncFileFormat.Read(stream).Status, Is.EqualTo(SyncReadStatus.Ok));
        }

        [Test]
        public void NewerFormatIsReportedNotParsed()
        {
            // A future format may change shape entirely; only the number is read
            var result = ScoreSyncFileFormat.ReadFromBytes(Gzip(
                "{\"Format\": 2, \"Games\": \"something else entirely\"}"));

            Assert.That(result.Status, Is.EqualTo(SyncReadStatus.NewerFormat));
            Assert.That(result.File, Is.Null);
            Assert.That(result.Error, Does.Contain("Update YARG"));
        }

        [Test]
        public void UnknownFieldsAreIgnored()
        {
            var result = ReadEdited(root =>
            {
                root["SomethingNew"] = 5;
                ((JObject) root["Games"][0])["AnotherNewField"] = "x";
            });

            Assert.That(result.Status, Is.EqualTo(SyncReadStatus.Ok), result.Error);
        }

        [TestCase(new byte[0], TestName = "Empty file")]
        [TestCase(new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0x00 }, TestName = "Truncated gzip")]
        [TestCase(new byte[] { (byte) '{', (byte) '}' }, TestName = "Not gzip")]
        public void CorruptBytesAreInvalid(byte[] bytes)
        {
            var result = ScoreSyncFileFormat.ReadFromBytes(bytes);

            Assert.That(result.Status, Is.EqualTo(SyncReadStatus.Invalid));
            Assert.That(result.File, Is.Null);
            Assert.That(result.Error, Is.Not.Empty);
        }

        [Test]
        public void TruncatedPayloadIsInvalid()
        {
            var json = Json(SampleFile());

            var result = ScoreSyncFileFormat.ReadFromBytes(Gzip(json.Substring(0, json.Length / 2)));

            Assert.That(result.Status, Is.EqualTo(SyncReadStatus.Invalid));
        }

        [TestCase("[1, 2, 3]", TestName = "Array root")]
        [TestCase("null", TestName = "Null root")]
        [TestCase("{}", TestName = "No format")]
        [TestCase("{\"Format\": \"1\"}", TestName = "String format")]
        [TestCase("{\"Format\": 0}", TestName = "Format zero")]
        public void MalformedDocumentsAreInvalid(string json)
        {
            var result = ScoreSyncFileFormat.ReadFromBytes(Gzip(json));

            Assert.That(result.Status, Is.EqualTo(SyncReadStatus.Invalid));
        }

        private static readonly (string name, Action<JObject> edit)[] BadEdits =
        {
            ("no device ID", r => r["DeviceId"] = ""),
            ("missing Games", r => r.Remove("Games")),
            ("missing DeviceName", r => r.Remove("DeviceName")),
            ("game missing BandScore", r => ((JObject) r["Games"][0]).Remove("BandScore")),
            ("game missing PlayerScores", r => ((JObject) r["Games"][0]).Remove("PlayerScores")),
            ("score missing Percent", r => ((JObject) r["Games"][0]["PlayerScores"][0]).Remove("Percent")),
            ("progress missing SectionCount", r => ((JObject) r["SectionProgress"][0]).Remove("SectionCount")),
            ("null Profiles", r => r["Profiles"] = null),
            ("Games not a list", r => r["Games"] = 5),
            ("profile without name", r => r["Profiles"][0]["Name"] = "  "),
            ("profile with empty ID", r => r["Profiles"][0]["Id"] = Guid.Empty.ToString()),
            ("profile with bad GUID", r => r["Profiles"][0]["Id"] = "not-a-guid"),
            ("unknown game mode", r => r["Profiles"][0]["GameMode"] = 200),
            ("unknown instrument", r => r["Games"][0]["PlayerScores"][0]["Instrument"] = 9999),
            ("unknown difficulty", r => r["SectionCompletions"][0]["Difficulty"] = 77),
            ("game without checksum", r => r["Games"][0]["SongChecksum"] = null),
            ("game with empty checksum", r => r["Games"][0]["SongChecksum"] = ""),
            ("game with zero date", r => r["Games"][0]["DateTicks"] = 0),
            ("game with negative date", r => r["Games"][0]["DateTicks"] = -5),
            ("game date past MaxValue", r => r["Games"][0]["DateTicks"] = long.MaxValue),
            ("null game", r => ((JArray) r["Games"])[0] = null),
            ("null player scores", r => r["Games"][0]["PlayerScores"] = null),
            ("score without player", r => r["Games"][0]["PlayerScores"][0]["PlayerId"] = Guid.Empty.ToString()),
            ("negative notes hit", r => r["Games"][0]["PlayerScores"][0]["NotesHit"] = -1),
            ("score as text", r => r["Games"][0]["BandScore"] = "lots"),
            ("negative section index", r => r["SectionCompletions"][0]["SectionIndex"] = -1),
            ("negative harmony index", r => r["SectionCompletions"][0]["HarmonyIndex"] = -1),
            ("completion without player", r => r["SectionCompletions"][0]["PlayerId"] = Guid.Empty.ToString()),
            ("progress without checksum", r => r["SectionProgress"][0]["SongChecksum"] = null),
            ("negative completed count", r => r["SectionProgress"][0]["CompletedCount"] = -3),
            ("player with empty ID", r => r["Players"][0]["Id"] = Guid.Empty.ToString()),
        };

        private static TestCaseData[] BadEditCases =>
            BadEdits.Select(e => new TestCaseData(e.edit).SetName($"Invalid: {e.name}")).ToArray();

        [TestCaseSource(nameof(BadEditCases))]
        public void ImplausibleValuesAreInvalid(Action<JObject> edit)
        {
            var result = ReadEdited(edit);

            Assert.That(result.Status, Is.EqualTo(SyncReadStatus.Invalid));
            Assert.That(result.File, Is.Null);
            Assert.That(result.Error, Is.Not.Empty);
        }

        [Test]
        public void ReadNeverThrowsOnRandomBytes()
        {
            var random = new Random(1234);
            var valid = ScoreSyncFileFormat.WriteToBytes(SampleFile());

            for (int i = 0; i < 500; i++)
            {
                var bytes = (byte[]) valid.Clone();
                int flips = 1 + random.Next(8);
                for (int f = 0; f < flips; f++)
                {
                    bytes[random.Next(bytes.Length)] = (byte) random.Next(256);
                }

                Assert.DoesNotThrow(() => ScoreSyncFileFormat.ReadFromBytes(bytes));
            }
        }
    }
}
