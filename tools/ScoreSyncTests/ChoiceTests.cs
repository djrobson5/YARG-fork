using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YARG.Scores.Sync;

namespace YARG.ScoreSyncTests
{
    public class ChoiceTests
    {
        private static SyncRootCandidate Account(string email, string path) => new() { Label = email, Path = path };

        private static readonly List<SyncRootCandidate> TwoAccounts = new()
        {
            Account("me@outlook.com", @"C:\Users\me\OneDrive"),
            Account("me@school.edu", @"C:\Users\me\OneDrive - School"),
        };

        [Test]
        public void OneAccountGivesAPlainOneDriveEntry()
        {
            var entries = ScoreSyncChoice.Entries(TwoAccounts.Take(1).ToList(), new ScoreSyncChoice());
            Assert.That(entries.Select(e => e.Label),
                Is.EqualTo(new[] { "Off", "OneDrive", "Google Drive", "Custom Folder" }));
        }

        [Test]
        public void SeveralAccountsGiveOneEntryEach()
        {
            var entries = ScoreSyncChoice.Entries(TwoAccounts, new ScoreSyncChoice());
            Assert.That(entries.Select(e => e.Label), Is.EqualTo(new[]
            {
                "Off", "OneDrive (me@outlook.com)", "OneDrive (me@school.edu)", "Google Drive", "Custom Folder",
            }));
        }

        [Test]
        public void ASavedAccountThatIsNotSignedInStaysListed()
        {
            var saved = new ScoreSyncChoice(ScoreSyncProvider.OneDrive, "gone@school.edu");
            var entries = ScoreSyncChoice.Entries(new List<SyncRootCandidate>(), saved);
            Assert.That(entries, Does.Contain(saved));
            Assert.That(entries.IndexOf(saved), Is.EqualTo(2));
        }

        [Test]
        public void EqualityIgnoresEmailCase()
        {
            Assert.That(new ScoreSyncChoice(ScoreSyncProvider.OneDrive, "Me@School.edu"),
                Is.EqualTo(new ScoreSyncChoice(ScoreSyncProvider.OneDrive, "me@school.edu")));
            Assert.That(new ScoreSyncChoice(ScoreSyncProvider.OneDrive),
                Is.Not.EqualTo(new ScoreSyncChoice(ScoreSyncProvider.OneDrive, "me@school.edu")));
        }

        [Test]
        public void PickRootByAccount()
        {
            var choice = new ScoreSyncChoice(ScoreSyncProvider.OneDrive, "me@school.edu");
            Assert.That(choice.PickRoot(TwoAccounts), Is.EqualTo(@"C:\Users\me\OneDrive - School"));
        }

        [Test]
        public void PickRootWithoutAccountTakesTheFirst()
        {
            Assert.That(new ScoreSyncChoice(ScoreSyncProvider.GoogleDrive).PickRoot(TwoAccounts),
                Is.EqualTo(@"C:\Users\me\OneDrive"));
        }

        [Test]
        public void PickRootFindsNothing()
        {
            Assert.That(new ScoreSyncChoice(ScoreSyncProvider.OneDrive, "gone@school.edu").PickRoot(TwoAccounts),
                Is.Empty);
            Assert.That(new ScoreSyncChoice(ScoreSyncProvider.GoogleDrive).PickRoot(new List<SyncRootCandidate>()),
                Is.Empty);
            Assert.That(new ScoreSyncChoice(ScoreSyncProvider.CustomFolder).PickRoot(TwoAccounts), Is.Empty);
        }

        [Test]
        public void BrowsingToTheSyncFolderItselfMeansItsParent()
        {
            string root = Path.Combine(Path.GetTempPath(), "Cloud");
            Assert.That(ScoreSyncFolder.RootFromBrowsedFolder(Path.Combine(root, ScoreSyncFolder.FOLDER_NAME)),
                Is.EqualTo(root));
            Assert.That(ScoreSyncFolder.RootFromBrowsedFolder(root), Is.EqualTo(root));
        }
    }
}
