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
    /// the probe gathers, so the rules are testable anywhere.
    /// </summary>
    public static class ScoreSyncProviders
    {
        public const string GOOGLE_MY_DRIVE = "My Drive";
        public const string GOOGLE_VOLUME_LABEL = "Google Drive";

        /// <summary>Where macOS keeps File Provider sync roots, under the home folder.</summary>
        public const string MAC_CLOUD_STORAGE = "Library/CloudStorage";
        public const string MAC_ONEDRIVE_PREFIX = "OneDrive-";
        public const string MAC_ONEDRIVE_PERSONAL = "Personal";
        public const string MAC_GOOGLE_PREFIX = "GoogleDrive-";

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

        /// <summary>
        /// OneDrive on macOS: one <c>OneDrive-&lt;account&gt;</c> folder per signed-in account in
        /// <c>~/Library/CloudStorage</c>, where the account part is <c>Personal</c> or the
        /// organization's name. That part is the label, since the folder name carries no email.
        /// </summary>
        /// <remarks>
        /// Personal verified 2026-10-17 (<c>OneDrive-Personal</c>, with a <c>~/OneDrive</c> symlink
        /// to it). Business accounts are not.
        /// </remarks>
        /// <param name="cloudStorage">The full path of <c>~/Library/CloudStorage</c>.</param>
        /// <param name="folderNames">The names of the folders directly inside it.</param>
        public static List<SyncRootCandidate> DetectOneDriveMac(string cloudStorage, IEnumerable<string> folderNames)
        {
            return folderNames
                .Where(n => n.StartsWith(MAC_ONEDRIVE_PREFIX, StringComparison.OrdinalIgnoreCase)
                    && n.Length > MAC_ONEDRIVE_PREFIX.Length)
                .OrderBy(n => string.Equals(n.Substring(MAC_ONEDRIVE_PREFIX.Length), MAC_ONEDRIVE_PERSONAL,
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new SyncRootCandidate
                {
                    Path = Path.Combine(cloudStorage, n),
                    Label = n.Substring(MAC_ONEDRIVE_PREFIX.Length),
                })
                .ToList();
        }

        /// <summary>
        /// Google Drive for desktop on macOS: <c>GoogleDrive-&lt;email&gt;/My Drive</c> in
        /// <c>~/Library/CloudStorage</c>, in both streaming and mirror mode.
        /// </summary>
        /// <remarks>
        /// Not verified on a Mac with Google Drive installed. When nothing is found, the user picks
        /// the folder with Browse.
        /// </remarks>
        public static List<SyncRootCandidate> DetectGoogleDriveMac(string cloudStorage, IEnumerable<string> folderNames,
            Func<string, bool> directoryExists)
        {
            return folderNames
                .Where(n => n.StartsWith(MAC_GOOGLE_PREFIX, StringComparison.OrdinalIgnoreCase)
                    && n.Length > MAC_GOOGLE_PREFIX.Length)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => Path.Combine(cloudStorage, n, GOOGLE_MY_DRIVE))
                .Where(directoryExists)
                .Select(p => new SyncRootCandidate { Path = p, Label = p })
                .ToList();
        }
    }
}
