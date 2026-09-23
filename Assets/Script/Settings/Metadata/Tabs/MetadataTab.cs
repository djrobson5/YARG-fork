using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AddressableAssets;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Menu.Settings;
using YARG.Menu.Settings.Visuals;

namespace YARG.Settings.Metadata
{
    public class MetadataTab : Tab, IEnumerable<AbstractMetadata>
    {
        // Prefabs needed for this tab type
        private static GameObject _headerPrefab;
        private static GameObject _buttonPrefab;
        private static GameObject _textPrefab;

        // The status line is SettingsText shortened to a single dim line
        private const float STATUS_TEXT_HEIGHT = 70f;
        private const float STATUS_TEXT_ALPHA = 0.55f;
        private const float DIMMED_ALPHA = 0.5f;

        private Dictionary<string, BaseSettingVisual> _settingVisuals = new();
        private readonly List<(SettingsButton Button, ButtonRowMetadata Metadata)> _buttonRows = new();
        private readonly List<(TextMeshProUGUI Text, StatusTextMetadata Metadata)> _statusTexts = new();
        private readonly List<AbstractMetadata> _settings = new();

        public IReadOnlyList<AbstractMetadata> Settings => _settings;

        public MetadataTab(string name, string icon = "Generic", IPreviewBuilder previewBuilder = null)
            : base(name, icon, previewBuilder)
        {
        }

        public override void BuildSettingTab(Transform container, NavigationGroup navGroup)
        {
            _settingVisuals.Clear();
            _buttonRows.Clear();
            _statusTexts.Clear();

            var showAdvanced = SettingsMenu.Instance.ShowAdvanced;
            var settingIndex = 0;

            // Once we've found the tab, add the settings
            foreach (var settingMetadata in _settings)
            {
                if (!settingMetadata.IsVisible || (settingMetadata.IsAdvanced && !showAdvanced))
                {
                    continue;
                }

                switch (settingMetadata)
                {
                    case HeaderMetadata header:
                    {
                        if (_headerPrefab == null)
                        {
                            _headerPrefab = Addressables
                                .LoadAssetAsync<GameObject>("SettingTab/Header")
                                .WaitForCompletion();
                        }
                        // Spawn in the header
                        var go = Object.Instantiate(_headerPrefab, container);

                        // Set header text
                        go.GetComponentInChildren<TextMeshProUGUI>().text =
                            Localize.Key("Settings.Header", header.HeaderName);

                        settingIndex = 0;
                        break;
                    }
                    case ButtonRowMetadata buttonRow:
                    {
                        if (_buttonPrefab == null)
                        {
                            _buttonPrefab = Addressables
                                .LoadAssetAsync<GameObject>("SettingTab/Button")
                                .WaitForCompletion();
                        }
                        // Spawn the button
                        var go = Object.Instantiate(_buttonPrefab, container);

                        var buttonGroup = go.GetComponent<SettingsButton>();
                        buttonGroup.SetInfo(buttonRow.Buttons);
                        navGroup.AddNavigatable(buttonGroup);

                        if (buttonRow.EditableWhen is not null)
                        {
                            buttonGroup.SetEditable(buttonRow.IsEditable);
                            _buttonRows.Add((buttonGroup, buttonRow));
                        }

                        break;
                    }
                    case TextMetadata text:
                    {
                        if (_textPrefab == null)
                        {
                            _textPrefab = Addressables
                                .LoadAssetAsync<GameObject>("SettingTab/Text")
                                .WaitForCompletion();
                        }
                        // Spawn in the header
                        var go = Object.Instantiate(_textPrefab, container);

                        // Set text
                        go.GetComponentInChildren<TextMeshProUGUI>().text =
                            Localize.Key("Settings.Text", text.TextName);

                        break;
                    }
                    case StatusTextMetadata status:
                    {
                        if (_textPrefab == null)
                        {
                            _textPrefab = Addressables
                                .LoadAssetAsync<GameObject>("SettingTab/Text")
                                .WaitForCompletion();
                        }
                        var go = Object.Instantiate(_textPrefab, container);
                        var rect = (RectTransform) go.transform;
                        rect.sizeDelta = new Vector2(rect.sizeDelta.x, STATUS_TEXT_HEIGHT);

                        var text = go.GetComponentInChildren<TextMeshProUGUI>();
                        text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                        text.alpha = STATUS_TEXT_ALPHA;

                        _statusTexts.Add((text, status));
                        RefreshStatusText(text, status);
                        break;
                    }
                    case FieldMetadata field:
                    {
                        var setting = SettingsManager.GetSettingByName(field.FieldName);

                        var visual = SpawnSettingVisual(setting, container);
                        visual.AssignSetting(field.FieldName, field.HasDescription);
                        visual.AssignIndex(settingIndex);
                        visual.ShowAdvancedMarker(field.IsAdvanced);
                        visual.SetEditable(setting.IsEditable);

                        _settingVisuals.Add(field.FieldName, visual);
                        navGroup.AddNavigatable(visual.gameObject);

                        settingIndex++;
                        break;
                    }
                }
            }
        }

        public override void OnSettingChanged()
        {
            foreach (var pair in _settingVisuals)
            {
                var setting = SettingsManager.GetSettingByName(pair.Key);
                pair.Value.SetEditable(setting.IsEditable);
                pair.Value.RefreshVisual();
            }

            foreach (var (button, metadata) in _buttonRows)
            {
                button.SetEditable(metadata.IsEditable);
            }

            foreach (var (text, metadata) in _statusTexts)
            {
                RefreshStatusText(text, metadata);
            }
        }

        private static void RefreshStatusText(TextMeshProUGUI text, StatusTextMetadata metadata)
        {
            text.text = metadata.Text();
            text.alpha = metadata.IsEditable ? STATUS_TEXT_ALPHA : STATUS_TEXT_ALPHA * DIMMED_ALPHA;
        }

        // For collection initializer support
        public void Add(AbstractMetadata setting) => _settings.Add(setting);
        private List<AbstractMetadata>.Enumerator GetEnumerator() => _settings.GetEnumerator();
        IEnumerator<AbstractMetadata> IEnumerable<AbstractMetadata>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
