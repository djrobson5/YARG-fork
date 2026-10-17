using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core.Logging;
using YARG.Scores.Sync;

namespace YARG.Scores
{
    /// <summary>
    /// Gathers what provider detection needs from this machine (the registry and drives on
    /// Windows, <c>~/Library/CloudStorage</c> on macOS) and runs the rules in
    /// <see cref="ScoreSyncProviders"/>. Elsewhere it finds nothing.
    /// </summary>
    public static class ScoreSyncProviderProbe
    {
        /// <summary>
        /// The candidate sync roots for a provider, best first. Empty for Off, Custom Folder, or
        /// when nothing is found; the user then picks the folder with Browse.
        /// </summary>
        public static List<SyncRootCandidate> Detect(ScoreSyncProvider provider)
        {
            try
            {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
                string cloudStorage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ScoreSyncProviders.MAC_CLOUD_STORAGE);
                return provider switch
                {
                    ScoreSyncProvider.OneDrive    => ScoreSyncProviders.DetectOneDriveMac(cloudStorage, ReadFolderNames(cloudStorage)),
                    ScoreSyncProvider.GoogleDrive => ScoreSyncProviders.DetectGoogleDriveMac(cloudStorage,
                        ReadFolderNames(cloudStorage), Directory.Exists),
                    _ => new List<SyncRootCandidate>(),
                };
#else
                return provider switch
                {
                    ScoreSyncProvider.OneDrive    => ScoreSyncProviders.DetectOneDrive(ReadOneDriveAccounts(), Directory.Exists),
                    ScoreSyncProvider.GoogleDrive => ScoreSyncProviders.DetectGoogleDrive(ReadDrives(),
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists),
                    _ => new List<SyncRootCandidate>(),
                };
#endif
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Failed to detect the {provider} folder.");
                return new List<SyncRootCandidate>();
            }
        }

        private static List<string> ReadFolderNames(string directory)
        {
            var names = new List<string>();
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.EnumerateDirectories(directory))
                {
                    names.Add(Path.GetFileName(path));
                }
            }

            return names;
        }

        private static List<OneDriveAccountEntry> ReadOneDriveAccounts()
        {
            var accounts = new List<OneDriveAccountEntry>();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            if (root is null)
            {
                return accounts;
            }

            foreach (string name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                accounts.Add(new OneDriveAccountEntry
                {
                    KeyName = name,
                    UserEmail = key?.GetValue("UserEmail") as string,
                    UserFolder = key?.GetValue("UserFolder") as string,
                });
            }
#endif
            return accounts;
        }

        private static List<DriveEntry> ReadDrives()
        {
            var drives = new List<DriveEntry>();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            foreach (var drive in DriveInfo.GetDrives())
            {
                // A drive can drop out between listing and reading (a disconnected share)
                try
                {
                    bool ready = drive.IsReady;
                    drives.Add(new DriveEntry
                    {
                        RootDirectory = drive.RootDirectory.FullName,
                        VolumeLabel = ready ? drive.VolumeLabel : null,
                        IsReady = ready,
                    });
                }
                catch (Exception)
                {
                    // Skip it
                }
            }
#endif
            return drives;
        }
    }
}
