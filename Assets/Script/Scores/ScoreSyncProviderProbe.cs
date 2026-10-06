using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core.Logging;
using YARG.Scores.Sync;

namespace YARG.Scores
{
    /// <summary>
    /// Gathers what provider detection needs from this PC (registry, drives) and runs the rules
    /// in <see cref="ScoreSyncProviders"/>. Windows only; elsewhere it finds nothing.
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
                return provider switch
                {
                    ScoreSyncProvider.OneDrive    => ScoreSyncProviders.DetectOneDrive(ReadOneDriveAccounts(), Directory.Exists),
                    ScoreSyncProvider.GoogleDrive => ScoreSyncProviders.DetectGoogleDrive(ReadDrives(),
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Directory.Exists),
                    _ => new List<SyncRootCandidate>(),
                };
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Failed to detect the {provider} folder.");
                return new List<SyncRootCandidate>();
            }
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
