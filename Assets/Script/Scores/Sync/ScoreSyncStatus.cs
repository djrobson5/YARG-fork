using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace YARG.Scores.Sync
{
    /// <summary>
    /// Text for the Score Sync status line in Settings (docs/score-sync-design.md, "UI").
    /// </summary>
    public static class ScoreSyncStatus
    {
        public const string OFF = "Score sync is off.";
        public const string NO_FOLDER = "No sync folder is set.";
        public const string NEVER_SYNCED = "Not synced on this PC yet.";

        // The "-" plus the first 8 hex digits of the device GUID that ScoreSyncDevice appends
        private static readonly Regex DeviceIdSuffix = new("-[0-9a-fA-F]{8}$");

        /// <summary>
        /// The status line: whether sync can run, else the last result with when it happened.
        /// </summary>
        public static string Line(bool enabled, string syncRoot, bool rootExists, ScoreSyncState state,
            DateTime nowLocal)
        {
            if (!enabled)
            {
                return OFF;
            }

            if (string.IsNullOrWhiteSpace(syncRoot))
            {
                return NO_FOLDER;
            }

            if (!rootExists)
            {
                return $"The sync folder doesn't exist: {syncRoot}";
            }

            var when = state?.LastResultUtc ?? state?.LastSyncUtc;
            if (when is null || string.IsNullOrEmpty(state.LastResult))
            {
                return NEVER_SYNCED;
            }

            return $"{DescribeWhen(when.Value, nowLocal)} · {state.LastResult}";
        }

        /// <summary>
        /// "Today 9:14 PM", "Yesterday 8:02 AM", else the date and time.
        /// </summary>
        public static string DescribeWhen(DateTime utc, DateTime nowLocal)
        {
            var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            return DescribeLocal(local, nowLocal);
        }

        /// <summary>
        /// <see cref="DescribeWhen"/> for a time already in local time. Separate so tests do not
        /// depend on the machine's time zone.
        /// </summary>
        public static string DescribeLocal(DateTime local, DateTime nowLocal)
        {
            var culture = CultureInfo.CurrentCulture;
            string time = local.ToString("t", culture);

            int daysAgo = (nowLocal.Date - local.Date).Days;
            return daysAgo switch
            {
                0 => $"Today {time}",
                1 => $"Yesterday {time}",
                _ => local.ToString("g", culture),
            };
        }

        /// <summary>
        /// The PC name in an export file name: <c>DESKTOP-GAMING-3f9a1c2e.yargsync</c> gives
        /// <c>DESKTOP-GAMING</c>. Used for files that were not opened because they had not changed.
        /// </summary>
        public static string DeviceNameFromFileName(string fileName)
        {
            string name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
            string stripped = DeviceIdSuffix.Replace(name, string.Empty);
            return stripped.Length > 0 ? stripped : name;
        }
    }
}
