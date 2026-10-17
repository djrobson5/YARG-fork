using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace YARG.Scores.Sync
{
    /// <summary>
    /// This PC's identity in the sync folder (docs/score-sync-design.md, "Device identity").
    /// Stored locally in <c>score-sync-device.json</c> and never synced.
    /// </summary>
    public class ScoreSyncDevice
    {
        public const string FILE_NAME = "score-sync-device.json";

        private const string LOCAL_SUFFIX = ".local";

        public Guid   Id;
        public string MachineName;

        /// <summary>
        /// The ID as written into export files.
        /// </summary>
        [JsonIgnore]
        public string DeviceId => Id.ToString("N");

        /// <summary>
        /// This PC's export file: <c>&lt;machine name&gt;-&lt;first 8 hex of the ID&gt;.yargsync</c>.
        /// The ID part keeps two PCs with the same machine name apart.
        /// </summary>
        [JsonIgnore]
        public string ExportFileName =>
            $"{SanitizeFileNamePart(MachineName)}-{DeviceId.Substring(0, 8)}{ScoreSyncFolder.EXTENSION}";

        /// <summary>
        /// Loads this PC's identity, creating it on first use. A missing or unreadable identity
        /// file is replaced by a new identity.
        /// </summary>
        /// <param name="directory">The persistent data directory.</param>
        /// <param name="machineName">The current machine name, used only when creating.</param>
        public static ScoreSyncDevice LoadOrCreate(string directory, string machineName)
        {
            string path = Path.Combine(directory, FILE_NAME);
            try
            {
                if (File.Exists(path))
                {
                    var device = JsonConvert.DeserializeObject<ScoreSyncDevice>(File.ReadAllText(path));
                    if (device is not null && device.Id != Guid.Empty && !string.IsNullOrWhiteSpace(device.MachineName))
                    {
                        return device;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable; replaced below. A new identity only means a new export file name,
                // and the union merge makes the old file harmless
            }

            string name = string.IsNullOrWhiteSpace(machineName) ? "PC" : machineName.Trim();

            // macOS reports its Bonjour host name, e.g. "Dustins-MacBook-Pro.local"
            if (name.Length > LOCAL_SUFFIX.Length && name.EndsWith(LOCAL_SUFFIX, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - LOCAL_SUFFIX.Length);
            }

            var created = new ScoreSyncDevice
            {
                Id = Guid.NewGuid(),
                MachineName = name,
            };

            Directory.CreateDirectory(directory);
            ScoreSyncFolder.WriteAllBytesAtomic(path, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(created, Formatting.Indented)));
            return created;
        }

        /// <summary>
        /// Replaces characters that can't appear in a Windows file name.
        /// </summary>
        public static string SanitizeFileNamePart(string name)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' });
            var builder = new StringBuilder();
            foreach (char c in (name ?? string.Empty).Trim())
            {
                builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            }

            string result = builder.ToString().Trim('.', ' ');
            return result.Length == 0 ? "PC" : result;
        }
    }
}
