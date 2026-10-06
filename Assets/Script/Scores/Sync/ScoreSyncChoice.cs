using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace YARG.Scores.Sync
{
    /// <summary>
    /// One entry of the Sync Provider dropdown: a provider, plus the OneDrive account when
    /// several are signed in (docs/score-sync-design.md, "UI").
    /// </summary>
    public sealed class ScoreSyncChoice : IEquatable<ScoreSyncChoice>
    {
        public ScoreSyncProvider Provider;

        /// <summary>
        /// The OneDrive account email, or null for "whichever OneDrive account is signed in".
        /// </summary>
        public string Account;

        public ScoreSyncChoice()
        {
        }

        public ScoreSyncChoice(ScoreSyncProvider provider, string account = null)
        {
            Provider = provider;
            Account = account;
        }

        [JsonIgnore]
        public bool IsOff => Provider == ScoreSyncProvider.Off;

        [JsonIgnore]
        public string Label => Provider switch
        {
            ScoreSyncProvider.OneDrive when Account is not null => $"OneDrive ({Account})",
            ScoreSyncProvider.OneDrive     => "OneDrive",
            ScoreSyncProvider.GoogleDrive  => "Google Drive",
            ScoreSyncProvider.CustomFolder => "Custom Folder",
            _                              => "Off",
        };

        /// <summary>
        /// The dropdown entries. One OneDrive entry per account when several are signed in, a
        /// plain "OneDrive" otherwise. The current choice is kept even when its account is not
        /// signed in on this PC, so the setting still shows what is saved.
        /// </summary>
        public static List<ScoreSyncChoice> Entries(IReadOnlyList<SyncRootCandidate> oneDriveAccounts,
            ScoreSyncChoice current)
        {
            var oneDrive = oneDriveAccounts.Count > 1
                ? oneDriveAccounts.Select(a => new ScoreSyncChoice(ScoreSyncProvider.OneDrive, a.Label)).ToList()
                : new List<ScoreSyncChoice> { new(ScoreSyncProvider.OneDrive) };

            if (current is not null && current.Provider == ScoreSyncProvider.OneDrive && !oneDrive.Contains(current))
            {
                oneDrive.Add(current);
            }

            var entries = new List<ScoreSyncChoice> { new(ScoreSyncProvider.Off) };
            entries.AddRange(oneDrive);
            entries.Add(new ScoreSyncChoice(ScoreSyncProvider.GoogleDrive));
            entries.Add(new ScoreSyncChoice(ScoreSyncProvider.CustomFolder));
            return entries;
        }

        /// <summary>
        /// The folder to store when this entry is picked: the account's folder for a named
        /// OneDrive account, else the best candidate, else empty (Browse needed).
        /// </summary>
        public string PickRoot(IReadOnlyList<SyncRootCandidate> candidates)
        {
            if (Provider is ScoreSyncProvider.Off or ScoreSyncProvider.CustomFolder)
            {
                return string.Empty;
            }

            if (Account is not null)
            {
                return candidates.FirstOrDefault(c => string.Equals(c.Label, Account, StringComparison.OrdinalIgnoreCase))
                    ?.Path ?? string.Empty;
            }

            return candidates.FirstOrDefault()?.Path ?? string.Empty;
        }

        public bool Equals(ScoreSyncChoice other)
        {
            return other is not null && Provider == other.Provider
                && string.Equals(Account, other.Account, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj) => obj is ScoreSyncChoice other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(Provider, Account?.ToLowerInvariant());
        }

        public override string ToString() => Label;
    }
}
