using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Scores;
using YARG.Scores.Sync;

namespace YARG.Settings.Types
{
    /// <summary>
    /// The Sync Provider dropdown (docs/score-sync-design.md, "UI"). Its entries depend on the
    /// OneDrive accounts signed in right now, so they are re-detected when the dropdown is drawn.
    /// Picking an entry detects that provider's folder and stores it in <see cref="_folder"/>;
    /// loading the settings never does, so a Browse override sticks.
    /// </summary>
    public class ScoreSyncProviderSetting : DropdownSetting<ScoreSyncChoice>, IDropdownSetting
    {
        // The dropdown asks for Count once per entry while drawing, so detection results are
        // reused for a moment instead of reading the registry each time.
        private const float DETECTION_REUSE_SECONDS = 2f;

        private readonly ScoreSyncFolderSetting _folder;
        private float _detectedAt = float.NegativeInfinity;

        public ScoreSyncProviderSetting(ScoreSyncFolderSetting folder)
            : base(new ScoreSyncChoice(ScoreSyncProvider.Off), localizable: false)
        {
            _folder = folder;
        }

        protected override void SetValue(ScoreSyncChoice value)
        {
            _value = value ?? new ScoreSyncChoice(ScoreSyncProvider.Off);
        }

        public override string ValueToString(ScoreSyncChoice value) => value.Label;

        public override void UpdateValues()
        {
            _possibleValues.Clear();
            _possibleValues.AddRange(ScoreSyncChoice.Entries(DetectOneDrive(), Value));
        }

        int IDropdownSetting.Count
        {
            get
            {
                if (Time.realtimeSinceStartup - _detectedAt > DETECTION_REUSE_SECONDS)
                {
                    _detectedAt = Time.realtimeSinceStartup;
                    UpdateValues();
                }

                return _possibleValues.Count;
            }
        }

        void IDropdownSetting.SelectIndex(int index)
        {
            var choice = _possibleValues[index];
            if (!choice.IsOff)
            {
                _folder.Value = choice.PickRoot(ScoreSyncProviderProbe.Detect(choice.Provider));
            }

            Value = choice;
        }

        private List<SyncRootCandidate> DetectOneDrive()
        {
            // The base constructor calls UpdateValues before _folder is set. Nothing is drawn yet
            // then, so the probe can wait for the first Count.
            return _folder is null
                ? new List<SyncRootCandidate>()
                : ScoreSyncProviderProbe.Detect(ScoreSyncProvider.OneDrive);
        }
    }
}
