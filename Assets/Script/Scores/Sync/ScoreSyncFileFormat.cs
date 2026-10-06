using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using YARG.Core;
using YARG.Core.Game;

namespace YARG.Scores.Sync
{
    public enum SyncReadStatus
    {
        Ok,

        /// <summary>
        /// The file was written by a newer build; this PC needs updating before it can read it.
        /// </summary>
        NewerFormat,

        /// <summary>
        /// The file could not be parsed, or held missing or implausible values.
        /// </summary>
        Invalid,
    }

    public readonly struct SyncReadResult
    {
        public readonly SyncReadStatus Status;
        public readonly ScoreSyncFile  File;
        public readonly string         Error;

        public SyncReadResult(SyncReadStatus status, ScoreSyncFile file, string error)
        {
            Status = status;
            File = file;
            Error = error;
        }
    }

    /// <summary>
    /// Reads and writes the gzip-compressed JSON export file.
    /// </summary>
    /// <remarks>
    /// A source file is untrusted input: <see cref="Read"/> never throws for bad content, it
    /// reports it. The whole file is rejected on the first problem, so a merge only ever sees a
    /// fully valid file.
    /// </remarks>
    public static class ScoreSyncFileFormat
    {
        // Explicit settings, so that nothing a host sets on JsonConvert.DefaultSettings can
        // change the file format
        private static readonly JsonSerializerSettings _settings = new()
        {
            Formatting = Formatting.None,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            DateParseHandling = DateParseHandling.DateTime,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 16,
        };

        private static readonly JsonSerializer _serializer = JsonSerializer.Create(_settings);

        public static void Write(Stream stream, ScoreSyncFile file)
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
            using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
            _serializer.Serialize(writer, file);
        }

        public static byte[] WriteToBytes(ScoreSyncFile file)
        {
            using var stream = new MemoryStream();
            Write(stream, file);
            return stream.ToArray();
        }

        public static SyncReadResult Read(Stream stream)
        {
            JObject root;
            try
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                using var json = new JsonTextReader(reader)
                {
                    DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                    DateParseHandling = DateParseHandling.DateTime,
                    MaxDepth = 16,
                };
                root = JObject.Load(json);
            }
            catch (Exception ex)
            {
                return Invalid($"Not a readable score sync file ({ex.GetType().Name}).");
            }

            // Check the format before binding anything, so a newer file whose shape changed
            // is reported as newer rather than as corrupt
            if (root["Format"] is not JValue { Type: JTokenType.Integer } formatToken)
            {
                return Invalid("The file has no format number.");
            }

            long format = formatToken.Value<long>();
            if (format > ScoreSyncFile.CURRENT_FORMAT)
            {
                return new SyncReadResult(SyncReadStatus.NewerFormat, null,
                    $"The file uses format {format}, but this version only reads up to format "
                    + $"{ScoreSyncFile.CURRENT_FORMAT}. Update YARG on this PC.");
            }

            if (format < 1)
            {
                return Invalid($"The file has an unknown format number ({format}).");
            }

            ScoreSyncFile file;
            try
            {
                file = root.ToObject<ScoreSyncFile>(_serializer);
            }
            catch (Exception ex)
            {
                return Invalid($"The file's contents are malformed ({ex.GetType().Name}).");
            }

            string error = Validate(file);
            return error is null
                ? new SyncReadResult(SyncReadStatus.Ok, file, null)
                : Invalid(error);
        }

        public static SyncReadResult ReadFromBytes(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return Read(stream);
        }

        private static SyncReadResult Invalid(string error)
        {
            return new SyncReadResult(SyncReadStatus.Invalid, null, error);
        }

        /// <returns>
        /// A description of the first problem found, or null if the file is usable.
        /// </returns>
        private static string Validate(ScoreSyncFile file)
        {
            if (file is null)
                return "The file is empty.";
            if (string.IsNullOrWhiteSpace(file.DeviceId))
                return "The file has no device ID.";
            if (file.Profiles is null || file.Players is null || file.Games is null
                || file.SectionCompletions is null || file.SectionProgress is null)
                return "The file is missing a section.";

            foreach (var profile in file.Profiles)
            {
                if (profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name))
                    return "A profile has no ID or name.";
                if (!Enum.IsDefined(typeof(GameMode), profile.GameMode)
                    || !Enum.IsDefined(typeof(Instrument), profile.CurrentInstrument)
                    || !Enum.IsDefined(typeof(Difficulty), profile.CurrentDifficulty))
                    return $"Profile '{profile.Name}' has an unknown game mode, instrument or difficulty.";
            }

            foreach (var player in file.Players)
            {
                if (player is null || player.Id == Guid.Empty)
                    return "A player has no ID.";
            }

            foreach (var game in file.Games)
            {
                if (game is null || !IsChecksum(game.SongChecksum))
                    return "A game has no song checksum.";
                if (!IsTicks(game.DateTicks))
                    return "A game has an impossible date.";
                if (game.PlayerScores is null)
                    return "A game has no player scores list.";

                foreach (var score in game.PlayerScores)
                {
                    if (score is null || score.PlayerId == Guid.Empty)
                        return "A player score has no player ID.";
                    if (!Enum.IsDefined(typeof(Instrument), score.Instrument)
                        || !Enum.IsDefined(typeof(Difficulty), score.Difficulty))
                        return "A player score has an unknown instrument or difficulty.";
                    if (score.NotesHit < 0 || score.NotesMissed < 0)
                        return "A player score has a negative note count.";
                }
            }

            foreach (var completion in file.SectionCompletions)
            {
                if (completion is null || !IsChecksum(completion.SongChecksum) || completion.PlayerId == Guid.Empty)
                    return "A section completion has no song checksum or player ID.";
                if (!Enum.IsDefined(typeof(Instrument), completion.Instrument)
                    || !Enum.IsDefined(typeof(Difficulty), completion.Difficulty))
                    return "A section completion has an unknown instrument or difficulty.";
                if (completion.SectionIndex < 0 || completion.SectionCount < 0 || completion.HarmonyIndex < 0)
                    return "A section completion has a negative index or count.";
                if (!IsTicks(completion.FirstCompletedTicks))
                    return "A section completion has an impossible date.";
            }

            foreach (var progress in file.SectionProgress)
            {
                if (progress is null || !IsChecksum(progress.SongChecksum) || progress.PlayerId == Guid.Empty)
                    return "A section progress row has no song checksum or player ID.";
                if (!Enum.IsDefined(typeof(Instrument), progress.Instrument)
                    || !Enum.IsDefined(typeof(Difficulty), progress.Difficulty))
                    return "A section progress row has an unknown instrument or difficulty.";
                if (progress.SectionCount < 0 || progress.CompletedCount < 0 || progress.HarmonyIndex < 0)
                    return "A section progress row has a negative count.";
                if (!IsTicks(progress.LastUpdatedTicks))
                    return "A section progress row has an impossible date.";
            }

            return null;
        }

        private static bool IsChecksum(byte[] checksum)
        {
            return checksum is { Length: > 0 };
        }

        private static bool IsTicks(long ticks)
        {
            return ticks > 0 && ticks <= DateTime.MaxValue.Ticks;
        }
    }
}
