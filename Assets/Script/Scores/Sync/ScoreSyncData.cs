using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using YARG.Core;
using YARG.Core.Game;

namespace YARG.Scores.Sync
{
    // The score sync export model (docs/score-sync-design.md, "Export file format").
    //
    // Everything under Assets/Script/Scores/Sync/ is compiled by link into
    // tools/ScoreSyncTests, so it must stay free of UnityEngine and of sqlite-net. The database
    // layer maps its records to and from these types.
    //
    // Dates are stored as raw ticks rather than DateTime: game records are matched on the exact
    // Date ticks, and a DateTime round trip through JSON would depend on its Kind.
    //
    // Every member is required when reading (ItemRequired), so a file with a missing field is
    // rejected instead of read with a default. A field added later must either bump
    // ScoreSyncFile.CURRENT_FORMAT or be marked optional with [JsonProperty(Required = ...)].

    /// <summary>
    /// The rows being synced: a whole local score database, or the payload of one export file.
    /// </summary>
    [JsonObject(ItemRequired = Required.AllowNull)]
    public class ScoreSyncData
    {
        public List<SyncProfile>           Profiles           = new();
        public List<SyncPlayer>            Players            = new();
        public List<SyncGame>              Games              = new();
        public List<SyncSectionCompletion> SectionCompletions = new();
        public List<SyncSectionProgress>   SectionProgress    = new();
    }

    /// <summary>
    /// One PC's export file.
    /// </summary>
    [JsonObject(ItemRequired = Required.AllowNull)]
    public class ScoreSyncFile : ScoreSyncData
    {
        /// <summary>
        /// The export format this build writes and understands.
        /// </summary>
        public const int CURRENT_FORMAT = 1;

        public int      Format;
        public string   DeviceId;
        public string   DeviceName;
        public DateTime ExportedAt;
        public string   AppVersion;
    }

    /// <summary>
    /// A profile's identity plus the fields needed to make a usable profile on another PC.
    /// Bindings and presets never travel.
    /// </summary>
    [JsonObject(ItemRequired = Required.AllowNull)]
    public class SyncProfile
    {
        public Guid       Id;
        public string     Name;
        public GameMode   GameMode;
        public Instrument CurrentInstrument;
        public Difficulty CurrentDifficulty;
    }

    /// <summary>
    /// A row of the <c>Players</c> table.
    /// </summary>
    [JsonObject(ItemRequired = Required.AllowNull)]
    public class SyncPlayer
    {
        public Guid   Id;
        public string Name;
    }

    /// <summary>
    /// A <c>GameRecords</c> row with its <c>PlayerScores</c> rows nested, so the link between
    /// them survives without the local auto-increment IDs.
    /// </summary>
    [JsonObject(ItemRequired = Required.AllowNull)]
    public class SyncGame
    {
        public long   DateTicks;
        public byte[] SongChecksum;

        public string GameVersion;

        public string SongName;
        public string SongArtist;
        public string SongCharter;

        public string ReplayFileName;
        public byte[] ReplayChecksum;

        public int        BandScore;
        public StarAmount BandStars;

        public float SongSpeed;
        public bool  PlayedWithReplay;
        public bool  HasBots;

        public List<SyncPlayerScore> PlayerScores = new();

        /// <remarks>Shallow: the copy shares the player scores list.</remarks>
        public SyncGame Clone() => (SyncGame) MemberwiseClone();
    }

    [JsonObject(ItemRequired = Required.AllowNull)]
    public class SyncPlayerScore
    {
        public Guid       PlayerId;
        public Instrument Instrument;
        public Difficulty Difficulty;

        public Guid EnginePresetId;

        public int        Score;
        public StarAmount Stars;

        public int    NotesHit;
        public int    NotesMissed;
        public bool   IsFc;
        public bool   IsReplay;
        public float? Percent;

        public SyncPlayerScore Clone() => (SyncPlayerScore) MemberwiseClone();
    }

    [JsonObject(ItemRequired = Required.AllowNull)]
    public class SyncSectionCompletion
    {
        public byte[]     SongChecksum;
        public Guid       PlayerId;
        public Instrument Instrument;
        public Difficulty Difficulty;
        public int        HarmonyIndex;
        public int        SectionIndex;
        public int        SectionCount;
        public long       FirstCompletedTicks;

        public SyncSectionCompletion Clone() => (SyncSectionCompletion) MemberwiseClone();
    }

    [JsonObject(ItemRequired = Required.AllowNull)]
    public class SyncSectionProgress
    {
        public Guid       PlayerId;
        public Instrument Instrument;
        public byte[]     SongChecksum;
        public Difficulty Difficulty;
        public int        HarmonyIndex;
        public int        SectionCount;
        public int        CompletedCount;
        public long       LastUpdatedTicks;

        public SyncSectionProgress Clone() => (SyncSectionProgress) MemberwiseClone();
    }
}
