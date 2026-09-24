namespace YARG.Settings.Types
{
    /// <summary>
    /// The sync root: the cloud folder whose <c>YARG Score Sync</c> subfolder holds the export
    /// files. Empty when no folder is set. See docs/score-sync-design.md, "UI".
    /// </summary>
    public class ScoreSyncFolderSetting : AbstractSetting<string>
    {
        public override string AddressableName => "Setting/ScoreSyncFolder";

        public ScoreSyncFolderSetting() : base(null)
        {
            _value = string.Empty;
        }

        protected override void SetValue(string value)
        {
            _value = value ?? string.Empty;
        }

        public override bool ValueEquals(string value) => value == Value;
    }
}
