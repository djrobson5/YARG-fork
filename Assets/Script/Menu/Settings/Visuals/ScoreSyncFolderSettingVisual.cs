using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Helpers;
using YARG.Menu.Navigation;
using YARG.Scores.Sync;
using YARG.Settings;
using YARG.Settings.Types;

namespace YARG.Menu.Settings.Visuals
{
    /// <summary>
    /// The Sync Folder row: the full <c>YARG Score Sync</c> path, Browse, and Open Folder
    /// (docs/score-sync-design.md, "UI").
    /// </summary>
    public class ScoreSyncFolderSettingVisual : BaseSettingVisual<ScoreSyncFolderSetting>
    {
        private const string MISSING_COLOR = "#FFBB0D";

        [SerializeField]
        private TextMeshProUGUI _pathText;

        [SerializeField]
        private Image _openButton;

        public override NavigationScheme GetNavigationScheme() => NavigationScheme.Empty;

        private Color _enabledButtonColor;
        private readonly Color _disabledButtonColor = Color.gray;

        protected override void OnSettingInit()
        {
            _enabledButtonColor = _openButton.color;
            RefreshVisual();
        }

        public override void RefreshVisual()
        {
            string root = Setting.Value;
            bool exists = !string.IsNullOrEmpty(root) && Directory.Exists(root);

            if (string.IsNullOrEmpty(root))
            {
                bool custom = SettingsManager.Settings.SyncProvider.Value.Provider == ScoreSyncProvider.CustomFolder;
                _pathText.text = custom
                    ? "No folder chosen"
                    : $"<color={MISSING_COLOR}>Not found. Choose the folder with Browse.</color>";
            }
            else
            {
                string path = ScoreSyncFolder.GetSyncDirectory(root);
                _pathText.text = exists ? path : $"<color={MISSING_COLOR}>{path}</color>";
            }

            _openButton.color = exists ? _enabledButtonColor : _disabledButtonColor;
        }

        public void Browse()
        {
            FileExplorerHelper.OpenChooseFolder(Setting.Value, folder =>
            {
                Setting.Value = ScoreSyncFolder.RootFromBrowsedFolder(folder);
                RefreshVisual();
            });
        }

        public void OpenFolder()
        {
            string root = Setting.Value;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return;
            }

            // The subfolder only exists after the first export. Quoted because cloud folder
            // names have spaces ("OneDrive - School", "My Drive").
            string syncDirectory = ScoreSyncFolder.GetSyncDirectory(root);
            FileExplorerHelper.OpenFolder($"\"{(Directory.Exists(syncDirectory) ? syncDirectory : root)}\"");
        }
    }
}
