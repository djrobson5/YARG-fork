using System;
using System.Globalization;
using NUnit.Framework;
using YARG.Scores.Sync;

namespace YARG.ScoreSyncTests
{
    public class StatusTests
    {
        private CultureInfo _previousCulture;

        [SetUp]
        public void SetUp()
        {
            _previousCulture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
        }

        [TearDown]
        public void TearDown()
        {
            CultureInfo.CurrentCulture = _previousCulture;
        }

        private static readonly DateTime Now = new(2026, 9, 22, 22, 30, 0, DateTimeKind.Local);

        // ICU (Linux, macOS) puts a narrow no-break space before AM/PM in en-US; Windows uses a
        // plain space. Either is right for the player's machine, so compare them as one.
        private static string Spaces(string text) => text?.Replace(' ', ' ');

        [Test]
        public void Today()
        {
            Assert.That(Spaces(ScoreSyncStatus.DescribeLocal(new DateTime(2026, 9, 22, 21, 14, 0), Now)),
                Is.EqualTo("Today 9:14 PM"));
        }

        [Test]
        public void Yesterday()
        {
            Assert.That(Spaces(ScoreSyncStatus.DescribeLocal(new DateTime(2026, 9, 21, 8, 2, 0), Now)),
                Is.EqualTo("Yesterday 8:02 AM"));
        }

        [Test]
        public void OlderShowsTheDate()
        {
            Assert.That(Spaces(ScoreSyncStatus.DescribeLocal(new DateTime(2026, 9, 18, 8, 2, 0), Now)),
                Is.EqualTo("9/18/2026 8:02 AM"));
        }

        [Test]
        public void JustAfterMidnightIsYesterday()
        {
            var now = new DateTime(2026, 9, 23, 0, 5, 0);
            Assert.That(Spaces(ScoreSyncStatus.DescribeLocal(new DateTime(2026, 9, 22, 23, 50, 0), now)),
                Is.EqualTo("Yesterday 11:50 PM"));
        }

        [Test]
        public void LineWhenOff()
        {
            Assert.That(ScoreSyncStatus.Line(false, @"C:\x", true, new ScoreSyncState(), Now),
                Is.EqualTo(ScoreSyncStatus.OFF));
        }

        [Test]
        public void LineWithoutFolder()
        {
            Assert.That(ScoreSyncStatus.Line(true, "", false, new ScoreSyncState(), Now),
                Is.EqualTo(ScoreSyncStatus.NO_FOLDER));
        }

        [Test]
        public void LineWithMissingFolder()
        {
            Assert.That(ScoreSyncStatus.Line(true, @"G:\My Drive", false, new ScoreSyncState(), Now),
                Is.EqualTo(@"The sync folder doesn't exist: G:\My Drive"));
        }

        [Test]
        public void LineNeverSynced()
        {
            Assert.That(ScoreSyncStatus.Line(true, @"C:\x", true, new ScoreSyncState(), Now),
                Is.EqualTo(ScoreSyncStatus.NEVER_SYNCED));
        }

        [Test]
        public void LineWithResult()
        {
            var when = new DateTime(2026, 9, 22, 21, 14, 0, DateTimeKind.Local).ToUniversalTime();
            var state = new ScoreSyncState { LastResult = "Up to date", LastResultUtc = when };
            Assert.That(Spaces(ScoreSyncStatus.Line(true, @"C:\x", true, state, Now)),
                Is.EqualTo("Today 9:14 PM · Up to date"));
        }

        [Test]
        public void LineFallsBackToLastSyncTime()
        {
            // A state written before LastResultUtc existed
            var when = new DateTime(2026, 9, 21, 8, 2, 0, DateTimeKind.Local).ToUniversalTime();
            var state = new ScoreSyncState { LastResult = "Up to date", LastSyncUtc = when };
            Assert.That(Spaces(ScoreSyncStatus.Line(true, @"C:\x", true, state, Now)),
                Is.EqualTo("Yesterday 8:02 AM · Up to date"));
        }

        [TestCase(14, 0, new string[0], "Added 14 scores from DESKTOP-GAMING")]
        [TestCase(1, 0, new string[0], "Added 1 score from DESKTOP-GAMING")]
        [TestCase(0, 3, new string[0], "Added 3 section records from DESKTOP-GAMING")]
        [TestCase(2, 1, new string[0], "Added 2 scores and 1 section record from DESKTOP-GAMING")]
        [TestCase(14, 0, new[] { "Riffmaster" },
            "Added 14 scores from DESKTOP-GAMING, and a new profile: Riffmaster")]
        [TestCase(14, 0, new[] { "A", "B" }, "Added 14 scores from DESKTOP-GAMING, and new profiles: A, B")]
        [TestCase(0, 0, new[] { "A" }, "New profile from DESKTOP-GAMING: A")]
        public void ImportToast(int games, int sections, string[] created, string expected)
        {
            Assert.That(ScoreSyncStatus.ImportToast("DESKTOP-GAMING", games, sections, created),
                Is.EqualTo(expected));
        }

        [Test]
        public void ImportToastNothingAdded()
        {
            Assert.That(ScoreSyncStatus.ImportToast("X", 0, 0, Array.Empty<string>()), Is.Null);
            Assert.That(ScoreSyncStatus.ImportToast("X", 0, 0, null), Is.Null);
        }

        [TestCase("DESKTOP-GAMING-3f9a1c2e.yargsync", "DESKTOP-GAMING")]
        [TestCase("LAPTOP-7B04D1E8.yargsync", "LAPTOP")]
        [TestCase("LAPTOP.yargsync", "LAPTOP")]
        [TestCase("-3f9a1c2e.yargsync", "-3f9a1c2e")]
        public void DeviceNameFromFileName(string fileName, string expected)
        {
            Assert.That(ScoreSyncStatus.DeviceNameFromFileName(fileName), Is.EqualTo(expected));
        }
    }
}
