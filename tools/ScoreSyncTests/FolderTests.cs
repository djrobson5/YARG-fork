using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using YARG.Scores.Sync;
using static YARG.ScoreSyncTests.Fixtures;

namespace YARG.ScoreSyncTests
{
    public class FolderTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ScoreSyncTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static ScoreSyncFile Sample(string deviceId = "LAPTOP-7b04d1e8")
        {
            var data = new ScoreSyncData();
            var id = Guid.NewGuid();
            data.Profiles.Add(Profile(id, "Alice"));
            data.Games.Add(Game(SongA, T0, 1000, Score(id, 1000)));
            return File(data, deviceId);
        }

        #region Device

        [Test]
        public void DeviceIsCreatedOnceAndReloaded()
        {
            var first = ScoreSyncDevice.LoadOrCreate(_root, "DESKTOP-GAMING");
            var second = ScoreSyncDevice.LoadOrCreate(_root, "RENAMED-PC");

            Assert.That(second.Id, Is.EqualTo(first.Id));
            Assert.That(second.MachineName, Is.EqualTo("DESKTOP-GAMING"), "the name is fixed at creation");
            Assert.That(first.ExportFileName, Is.EqualTo($"DESKTOP-GAMING-{first.Id.ToString("N").Substring(0, 8)}.yargsync"));
            Assert.That(first.DeviceId, Has.Length.EqualTo(32));
        }

        [Test]
        public void CorruptDeviceFileIsReplaced()
        {
            System.IO.File.WriteAllText(Path.Combine(_root, ScoreSyncDevice.FILE_NAME), "{ not json");

            var device = ScoreSyncDevice.LoadOrCreate(_root, "PC1");

            Assert.That(device.Id, Is.Not.EqualTo(Guid.Empty));
            Assert.That(ScoreSyncDevice.LoadOrCreate(_root, "PC1").Id, Is.EqualTo(device.Id));
        }

        [Test]
        public void TwoPcsWithTheSameNameGetDifferentFiles()
        {
            var other = Path.Combine(_root, "other");
            var a = ScoreSyncDevice.LoadOrCreate(_root, "DESKTOP");
            var b = ScoreSyncDevice.LoadOrCreate(other, "DESKTOP");

            Assert.That(a.ExportFileName, Is.Not.EqualTo(b.ExportFileName));
        }

        [TestCase("DESKTOP-GAMING", "DESKTOP-GAMING")]
        [TestCase("my:pc/one?", "my_pc_one_")]
        [TestCase("  ..PC.. ", "PC")]
        [TestCase("", "PC")]
        [TestCase(null, "PC")]
        public void MachineNamesAreSafeFileNames(string name, string expected)
        {
            Assert.That(ScoreSyncDevice.SanitizeFileNamePart(name), Is.EqualTo(expected));
        }

        #endregion

        #region Folder

        [Test]
        public void ExportIsWrittenIntoTheSyncFolderAndReadsBack()
        {
            var path = ScoreSyncFolder.WriteExport(_root, "PC1-aaaaaaaa.yargsync", Sample());

            Assert.That(path, Is.EqualTo(Path.Combine(_root, "YARG Score Sync", "PC1-aaaaaaaa.yargsync")));
            var read = ScoreSyncFolder.ReadSource(path);
            Assert.That(read.Status, Is.EqualTo(SourceReadStatus.Ok), read.Error);
            Assert.That(read.File.Games, Has.Count.EqualTo(1));
        }

        [Test]
        public void ReExportReplacesTheFileAndLeavesNoTemp()
        {
            ScoreSyncFolder.WriteExport(_root, "PC1-aaaaaaaa.yargsync", Sample());
            var bigger = Sample();
            bigger.Games.Add(Game(SongB, T0 + 1, 5, Score(bigger.Profiles[0].Id, 5)));
            var path = ScoreSyncFolder.WriteExport(_root, "PC1-aaaaaaaa.yargsync", bigger);

            Assert.That(ScoreSyncFolder.ReadSource(path).File.Games, Has.Count.EqualTo(2));
            Assert.That(Directory.GetFiles(ScoreSyncFolder.GetSyncDirectory(_root)).Select(Path.GetFileName),
                Is.EquivalentTo(new[] { "PC1-aaaaaaaa.yargsync" }));
        }

        [Test]
        public void ListingSkipsOwnFileTempFilesAndOtherExtensions()
        {
            string dir = ScoreSyncFolder.GetSyncDirectory(_root);
            ScoreSyncFolder.WriteExport(_root, "PC1-aaaaaaaa.yargsync", Sample());
            ScoreSyncFolder.WriteExport(_root, "PC2-bbbbbbbb.yargsync", Sample("PC2"));
            ScoreSyncFolder.WriteExport(_root, "PC3-cccccccc.YARGSYNC", Sample("PC3"));
            System.IO.File.WriteAllText(Path.Combine(dir, "PC4-dddddddd.yargsync.tmp"), "half");
            System.IO.File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
            System.IO.File.WriteAllText(Path.Combine(dir, "PC5.yargsyncx"), "x");

            var sources = ScoreSyncFolder.ListSources(_root, "pc1-AAAAAAAA.yargsync");

            Assert.That(sources.Select(s => s.FileName), Is.EqualTo(new[] { "PC2-bbbbbbbb.yargsync", "PC3-cccccccc.YARGSYNC" }));
            Assert.That(sources[0].Length, Is.GreaterThan(0));
        }

        [Test]
        public void ListingAMissingSyncFolderIsEmpty()
        {
            Assert.That(ScoreSyncFolder.ListSources(_root, "x.yargsync"), Is.Empty);
        }

        [Test]
        [Platform("Win", Reason = "Only Windows enforces FileShare.None")]
        public void MissingOrLockedFileIsUnavailable()
        {
            var missing = ScoreSyncFolder.ReadSource(Path.Combine(_root, "gone.yargsync"));
            Assert.That(missing.Status, Is.EqualTo(SourceReadStatus.Unavailable));

            var path = ScoreSyncFolder.WriteExport(_root, "PC2-bbbbbbbb.yargsync", Sample());
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var locked = ScoreSyncFolder.ReadSource(path);
                Assert.That(locked.Status, Is.EqualTo(SourceReadStatus.Unavailable));
                Assert.That(locked.Error, Does.Contain("PC2-bbbbbbbb.yargsync"));
            }
        }

        [Test]
        public void GarbageFileIsInvalid()
        {
            Directory.CreateDirectory(ScoreSyncFolder.GetSyncDirectory(_root));
            var path = Path.Combine(ScoreSyncFolder.GetSyncDirectory(_root), "bad.yargsync");
            System.IO.File.WriteAllText(path, "hello");

            Assert.That(ScoreSyncFolder.ReadSource(path).Status, Is.EqualTo(SourceReadStatus.Invalid));
        }

        [Test]
        public void ReadingIsNotBlockedByAWriterThatSharesTheFile()
        {
            // How a sync client typically holds a file it is updating
            var path = ScoreSyncFolder.WriteExport(_root, "PC2-bbbbbbbb.yargsync", Sample());
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.That(ScoreSyncFolder.ReadSource(path).Status, Is.EqualTo(SourceReadStatus.Ok));
            }
        }

        #endregion

        #region State

        [Test]
        public void UnchangedFileIsSkippedUntilItChanges()
        {
            var path = ScoreSyncFolder.WriteExport(_root, "PC2-bbbbbbbb.yargsync", Sample("PC2"));
            var source = ScoreSyncFolder.ListSources(_root, "own.yargsync").Single();
            var state = new ScoreSyncState();
            Assert.That(state.IsUnchanged(source), Is.False);

            state.RecordImported(source, ScoreSyncFolder.ReadSource(path).File);
            state.Save(_root);
            state = ScoreSyncState.Load(_root);
            Assert.That(state.IsUnchanged(ScoreSyncFolder.ListSources(_root, "own.yargsync").Single()), Is.True);

            var newer = Sample("PC2");
            newer.ExportedAt = newer.ExportedAt.AddMinutes(5);
            newer.Games.Add(Game(SongB, T0 + 9, 9, Score(newer.Profiles[0].Id, 9)));
            ScoreSyncFolder.WriteExport(_root, "PC2-bbbbbbbb.yargsync", newer);
            System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.That(state.IsUnchanged(ScoreSyncFolder.ListSources(_root, "own.yargsync").Single()), Is.False);
        }

        [Test]
        public void SameExportUnderATouchedFileIsAlreadyImported()
        {
            var file = Sample("PC2");
            var path = ScoreSyncFolder.WriteExport(_root, "PC2-bbbbbbbb.yargsync", file);
            var state = new ScoreSyncState();
            state.RecordImported(ScoreSyncFolder.ListSources(_root, "own.yargsync").Single(), file);

            System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
            var touched = ScoreSyncFolder.ListSources(_root, "own.yargsync").Single();

            Assert.That(state.IsUnchanged(touched), Is.False);
            Assert.That(state.AlreadyImported(ScoreSyncFolder.ReadSource(path).File), Is.True);
        }

        [Test]
        public void StateRoundTripsAndCorruptStateIsEmpty()
        {
            var state = new ScoreSyncState
            {
                LastExportUtc = new DateTime(2026, 9, 22, 1, 2, 3, DateTimeKind.Utc),
                LastResult = "Up to date",
            };
            state.Save(_root);
            var loaded = ScoreSyncState.Load(_root);
            Assert.That(loaded.LastExportUtc, Is.EqualTo(state.LastExportUtc));
            Assert.That(loaded.LastResult, Is.EqualTo("Up to date"));

            System.IO.File.WriteAllText(Path.Combine(_root, ScoreSyncState.FILE_NAME), "[[[");
            Assert.That(ScoreSyncState.Load(_root).Sources, Is.Empty);
            Assert.That(ScoreSyncState.Load(Path.Combine(_root, "nowhere")).Sources, Is.Empty);
        }

        #endregion

        #region Detection

        [Test]
        public void OneDriveSkipsSignedOutAccountsAndMissingFolders()
        {
            // The dev PC's shape: a stale Personal key with empty values, a live Business1
            var accounts = new[]
            {
                new OneDriveAccountEntry { KeyName = "Personal", UserEmail = "", UserFolder = "" },
                new OneDriveAccountEntry { KeyName = "Business1", UserEmail = "me@uoregon.edu", UserFolder = @"C:\Users\me\OneDrive - University Of Oregon" },
                new OneDriveAccountEntry { KeyName = "Business2", UserEmail = "old@corp.com", UserFolder = @"C:\Users\me\OneDrive - Gone" },
            };
            var existing = new HashSet<string> { @"C:\Users\me\OneDrive - University Of Oregon", @"C:\Users\me\OneDrive" };

            var found = ScoreSyncProviders.DetectOneDrive(accounts, existing.Contains);

            Assert.That(found.Select(f => f.Label), Is.EqualTo(new[] { "me@uoregon.edu" }));
            Assert.That(found[0].Path, Is.EqualTo(@"C:\Users\me\OneDrive - University Of Oregon"));
        }

        [Test]
        public void OneDriveListsSeveralAccountsPersonalFirst()
        {
            var accounts = new[]
            {
                new OneDriveAccountEntry { KeyName = "Business1", UserEmail = "work@x.com", UserFolder = @"C:\W" },
                new OneDriveAccountEntry { KeyName = "Personal", UserEmail = "me@x.com", UserFolder = @"C:\P" },
            };

            var found = ScoreSyncProviders.DetectOneDrive(accounts, _ => true);

            Assert.That(found.Select(f => f.Label), Is.EqualTo(new[] { "me@x.com", "work@x.com" }));
        }

        [Test]
        public void GoogleDriveFindsTheStreamedDriveFirst()
        {
            // The dev PC's shape: G: labelled "Google Drive" with My Drive at its root
            var drives = new[]
            {
                new DriveEntry { RootDirectory = @"C:\", VolumeLabel = "Windows-SSD", IsReady = true },
                new DriveEntry { RootDirectory = @"E:\", VolumeLabel = "USB", IsReady = true },
                new DriveEntry { RootDirectory = @"G:\", VolumeLabel = "Google Drive", IsReady = true },
                new DriveEntry { RootDirectory = @"D:\", VolumeLabel = null, IsReady = false },
            };
            // Path.Combine on both sides, so the test also holds on the Linux CI runner
            string g = Path.Combine(@"G:\", "My Drive"), e = Path.Combine(@"E:\", "My Drive");
            var existing = new HashSet<string> { g, e, Path.Combine(@"D:\", "My Drive") };

            var found = ScoreSyncProviders.DetectGoogleDrive(drives, @"C:\Users\me", existing.Contains);

            Assert.That(found.Select(f => f.Path), Is.EqualTo(new[] { g, e }));
        }

        [Test]
        public void GoogleDriveFallsBackToTheMirrorFolder()
        {
            var drives = new[] { new DriveEntry { RootDirectory = @"C:\", VolumeLabel = "OS", IsReady = true } };

            string mirrored = Path.Combine(@"C:\Users\me", "My Drive");
            var found = ScoreSyncProviders.DetectGoogleDrive(drives, @"C:\Users\me", p => p == mirrored);

            Assert.That(found.Single().Path, Is.EqualTo(mirrored));
        }

        [Test]
        public void NothingFoundIsEmpty()
        {
            Assert.That(ScoreSyncProviders.DetectOneDrive(Array.Empty<OneDriveAccountEntry>(), _ => true), Is.Empty);
            Assert.That(ScoreSyncProviders.DetectGoogleDrive(Array.Empty<DriveEntry>(), null, _ => true), Is.Empty);
        }

        #endregion
    }
}
