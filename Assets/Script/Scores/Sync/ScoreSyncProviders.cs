using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YARG.Scores.Sync
{
    public enum ScoreSyncProvider
    {
        Off,
        OneDrive,
        GoogleDrive,
        CustomFolder,
    }

    /// <summary>
    /// One place the sync folder could live, offered in the Sync Folder row.
    /// </summary>
    public class SyncRootCandidate
    {
        public string Path;

        /// <summary>What to show for it: the account email for OneDrive, the path otherwise.</summary>
        public string Label;
    }

    /// <summary>
    /// A OneDrive account as the registry lists it under
    /// <c>HKCU\Software\Microsoft\OneDrive\Accounts\*</c>.
    /// </summary>
    public class OneDriveAccountEntry
    {
        public string KeyName;
        public string UserEmail;
        public string UserFolder;
    }

    /// <summary>
    /// A drive as <c>DriveInfo</c> reports it.
    /// </summary>
    public class DriveEntry
    {
        public string RootDirectory;
        public string VolumeLabel;
        public bool   IsReady;
    }

    /// <summary>
    /// Provider folder detection (docs/score-sync-design.md, "Folder detection") over data that
    /// the Windows-only probe gathers, so the rules are testable anywhere.
    /// </summary>
    public static class ScoreSyncProviders
    {
        public const string GOOGLE_MY_DRIVE = "My Drive";
        public const string GOOGLE_VOLUME_LABEL = "Google Drive";

        /// <summary>
        /// The signed-in OneDrive accounts. An account counts only when both its email and its
        /// folder are set and the folder exists; a signed-out account leaves an empty key, and
        /// its old folder may still be on disk.
        /// </summary>
        /// <remarks>
        /// Not the <c>OneDrive</c> environment variable (stale on the dev PC), and not the
        /// folder's ReparsePoint attribute: Windows hides cloud placeholders from processes that
        /// don't opt in, so .NET does not see it even on a live sync root (checked 2026-09-22).
        /// </remarks>
        public static List<SyncRootCandidate> DetectOneDrive(IEnumerable<OneDriveAccountEntry> accounts,
            Func<string, bool> directoryExists)
        {
            return accounts
                .Where(a => !string.IsNullOrWhiteSpace(a.UserEmail) && !string.IsNullOrWhiteSpace(a.UserFolder))
                .Where(a => directoryExists(a.UserFolder))
                // Personal before Business1, Business2...
                .OrderBy(a => a.KeyName == "Personal" ? 0 : 1)
                .ThenBy(a => a.KeyName, StringComparer.OrdinalIgnoreCase)
                .Select(a => new SyncRootCandidate { Path = a.UserFolder, Label = a.UserEmail })
                .ToList();
        }

        /// <summary>
        /// Google Drive for desktop's My Drive. In streaming mode (the default) it is a virtual
        /// drive, usually G:, with <c>My Drive</c> at its root; drives labelled "Google Drive"
        /// come first. In mirror mode My Drive is a normal folder, by default
        /// <c>%USERPROFILE%\My Drive</c>.
        /// </summary>
        /// <remarks>
        /// Streaming mode verified 2026-09-22 (G:, label "Google Drive"). Mirror mode is not.
        /// When nothing is found, the user picks the folder with Browse.
        /// </remarks>
        public static List<SyncRootCandidate> DetectGoogleDrive(IEnumerable<DriveEntry> drives, string userProfile,
            Func<string, bool> directoryExists)
        {
            var found = new List<SyncRootCandidate>();

            foreach (var drive in drives
                .Where(d => d.IsReady && !string.IsNullOrEmpty(d.RootDirectory))
                .OrderBy(d => string.Equals(d.VolumeLabel, GOOGLE_VOLUME_LABEL, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => d.RootDirectory, StringComparer.OrdinalIgnoreCase))
            {
                string myDrive = Path.Combine(drive.RootDirectory, GOOGLE_MY_DRIVE);
                if (directoryExists(myDrive))
                {
                    found.Add(new SyncRootCandidate { Path = myDrive, Label = myDrive });
                }
            }

            if (!string.IsNullOrEmpty(userProfile))
            {
                string mirrored = Path.Combine(userProfile, GOOGLE_MY_DRIVE);
                if (directoryExists(mirrored))
                {
                    found.Add(new SyncRootCandidate { Path = mirrored, Label = mirrored });
                }
            }

            return found;
        }
    }
}
