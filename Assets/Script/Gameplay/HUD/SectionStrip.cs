using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Localization;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// The per-player strip of section blocks that sits above the far end of the highway.
    /// </summary>
    /// <remarks>
    /// One block per section that has notes for this player, colored by whether the section is
    /// already banked, still needed, clean so far, or dropped this run. The block the song is
    /// currently in is drawn taller and named above the strip.
    /// <para>
    /// All of the state lives in <see cref="SectionStripState"/>; this component only draws it.
    /// A player that cannot earn section credit is never given a state, and the strip hides
    /// itself in that case.
    /// </para>
    /// </remarks>
    public class SectionStrip : GameplayBehaviour
    {
        [SerializeField]
        private RectTransform _blockContainer;
        [SerializeField]
        private TextMeshProUGUI _label;

        [Space]
        [Header("Block colors")]
        [SerializeField]
        private Color _perfectedEarlierColor = new(0.361f, 0.294f, 0.541f);
        [SerializeField]
        private Color _neededColor = new(0.149f, 0.161f, 0.255f);
        [SerializeField]
        private Color _cleanColor = new(0.678f, 0.478f, 1f);
        [SerializeField]
        private Color _droppedColor = new(0.878f, 0.322f, 0.396f);

        [Space]
        [Header("Label colors")]
        [SerializeField]
        private Color _nameColor = new(0.925f, 0.933f, 1f);
        [SerializeField]
        private Color _neededTextColor = new(0.725f, 0.745f, 0.878f);

        [Space]
        [Header("Sizing")]
        [SerializeField]
        private float _blockHeight = 12f;
        [SerializeField]
        private float _currentBlockHeight = 26f;
        // The strip itself is gated by the ShowSectionStrip and TrackSectionCompletion
        // settings. EnableHighwayAnimation is scoped to the highway camera and strikeline,
        // and ReduceFlashingLights to venue lighting, so neither is the right switch for
        // motion here; no motion switch exists. The ease duration is the serialized field
        // below.
        [SerializeField]
        private float _easeDuration = 0.15f;

        private readonly List<Image> _blockPool = new();

        private HorizontalLayoutGroup _blockLayout;
        private float _defaultSpacing;

        private SectionStripState _state;
        private int _currentBlock = -1;

        /// <summary>
        /// How many blocks are showing, and how wide the container was when their spacing was
        /// last decided.
        /// </summary>
        /// <remarks>
        /// <c>TrackView.UpdateSectionStripWidth</c> can resize the strip at any point - the HUD
        /// layout runs before the player hands over a state, but dragging or rescaling the highway
        /// in HUD edit mode runs long after - so the spacing has to be re-derived when the width
        /// moves, not only when the blocks are built.
        /// </remarks>
        private int   _blockCount;
        private float _lastLayoutWidth;

        private const int PREVIEW_BLOCK_COUNT = 8;

        /// <summary>
        /// Once a block would be drawn narrower than this, the gaps cost more width than they buy
        /// readability and the blocks are packed edge to edge instead.
        /// </summary>
        /// <remarks>
        /// In the strip's own canvas units. A block this wide is still a clearly separate mark at
        /// the strip's height; below it the gap starts eating a visible fraction of the block.
        /// </remarks>
        private const float MIN_SPACED_BLOCK_WIDTH = 6f;

        protected override void GameplayAwake()
        {
            _blockLayout = _blockContainer.GetComponent<HorizontalLayoutGroup>();
            if (_blockLayout != null)
            {
                _defaultSpacing = _blockLayout.spacing;
            }

            // Nothing is shown until a player hands over a state
            SetVisible(false);
        }

        /// <summary>
        /// Attaches the strip to a player's state, or hides it when there is none.
        /// </summary>
        public void SetState(SectionStripState state)
        {
            if (_state != null)
            {
                _state.BlockStateChanged -= OnBlockStateChanged;
                _state.BlockProgressChanged -= OnBlockProgressChanged;
                _state.CurrentBlockChanged -= OnCurrentBlockChanged;
            }

            _state = state;

            if (_state == null)
            {
                SetVisible(false);
                return;
            }

            _state.BlockStateChanged += OnBlockStateChanged;
            _state.BlockProgressChanged += OnBlockProgressChanged;
            _state.CurrentBlockChanged += OnCurrentBlockChanged;

            SetVisible(true);
            BuildBlocks(_state.BlockCount);

            for (int i = 0; i < _state.BlockCount; i++)
            {
                _blockPool[i].color = GetBlockColor(_state.GetBlockState(i));
            }

            // Set without easing, so the strip doesn't animate into place on the first frame
            _currentBlock = _state.CurrentBlockIndex;
            ApplyCurrentBlock(false);
        }

        /// <summary>
        /// Tells the strip its width has changed, so the blocks can be re-spaced for it.
        /// </summary>
        /// <remarks>
        /// Called by <c>TrackView</c> after it sizes the strip. Cheap enough to call on every HUD
        /// update: the width is compared first, and only an actual change touches the layout.
        /// </remarks>
        public void OnStripResized()
        {
            if (_blockLayout == null || Mathf.Approximately(_blockContainer.rect.width, _lastLayoutWidth))
            {
                return;
            }

            UpdateSpacing();
        }

        /// <remarks>
        /// <see cref="GameplayBehaviour"/> keeps this disabled until the song starts, so the
        /// cursor is never walked against a song clock that hasn't begun running yet.
        /// </remarks>
        private void Update()
        {
            _state?.UpdateSongTime(GameManager.SongTime);
        }

        private void OnBlockStateChanged(int blockIndex)
        {
            if (blockIndex >= _blockPool.Count)
            {
                return;
            }

            _blockPool[blockIndex].color = GetBlockColor(_state.GetBlockState(blockIndex));

            if (blockIndex == _currentBlock)
            {
                UpdateLabel();
            }
        }

        /// <remarks>
        /// Only the current block is named, so progress anywhere else changes nothing on screen.
        /// This is what keeps the percent off a per-frame poll: it is redrawn on the notes that
        /// move it and on nothing else.
        /// </remarks>
        private void OnBlockProgressChanged(int blockIndex)
        {
            if (blockIndex == _currentBlock)
            {
                UpdateLabel();
            }
        }

        private void OnCurrentBlockChanged(int blockIndex)
        {
            int previous = _currentBlock;
            _currentBlock = blockIndex;

            if (previous >= 0 && previous < _blockPool.Count)
            {
                SetBlockHeight(_blockPool[previous], _blockHeight, true);
            }

            ApplyCurrentBlock(true);
        }

        private void ApplyCurrentBlock(bool animate)
        {
            if (_currentBlock >= 0 && _currentBlock < _blockPool.Count)
            {
                SetBlockHeight(_blockPool[_currentBlock], _currentBlockHeight, animate);
            }

            UpdateLabel();
        }

        private void UpdateLabel()
        {
            if (_state == null || _currentBlock < 0 || _currentBlock >= _state.BlockCount)
            {
                _label.text = string.Empty;
                return;
            }

            string name = _state.GetBlockName(_currentBlock);
            var state = _state.GetBlockState(_currentBlock);

            if (state == SectionStripBlockState.PerfectedEarlier)
            {
                // Nothing left to earn here, so the state word would only be noise
                _label.text = $"<color=#{ColorUtility.ToHtmlStringRGB(_nameColor)}>{name}</color>";
                return;
            }

            // The label only changes when the section, its state, or its progress does, so
            // building it as a string here costs nothing per frame
            _label.text = $"<color=#{ColorUtility.ToHtmlStringRGB(_nameColor)}>{name}</color>" +
                $"  <color=#{ColorUtility.ToHtmlStringRGB(GetLabelColor(state))}>·  {GetDetail(state)}</color>";
        }

        /// <summary>
        /// The part of the label after the section name.
        /// </summary>
        /// <remarks>
        /// A section still in play shows how much of it this run has hit rather than a state
        /// word: the percent says everything "clean" did and keeps saying it as the section is
        /// played. A dropped section has no progress worth reading, so it keeps its word.
        /// <para>
        /// On drums a clean section can be left one note short of 100%: an SP activation note the
        /// player skips is auto-hit inside the engine with no hit dispatch (see the note in
        /// <c>TrackPlayer.OnNoteHit</c>), so nothing ever raises the count for it. Nothing is done
        /// about it here. Only the block that is currently being played is named, and a block
        /// stops being current the moment the song crosses its end, so a stale sub-100 percent is
        /// never left sitting on a finished section - it is only ever read while the section is
        /// still in play and the missing note is still, as far as the strip knows, ahead.
        /// </para>
        /// </remarks>
        private string GetDetail(SectionStripBlockState state)
        {
            if (state == SectionStripBlockState.Dropped)
            {
                return Localize.Key("Gameplay.SectionStrip.Dropped");
            }

            var (hit, total) = _state.GetSectionProgress(_currentBlock);
            if (total <= 0)
            {
                return string.Empty;
            }

            // Floored, so a section one note short of finished can never read as 100%
            return $"{Mathf.FloorToInt(100f * hit / total)}%";
        }

        private Color GetBlockColor(SectionStripBlockState state)
        {
            return state switch
            {
                SectionStripBlockState.PerfectedEarlier => _perfectedEarlierColor,
                SectionStripBlockState.Clean            => _cleanColor,
                SectionStripBlockState.Dropped          => _droppedColor,
                _                                       => _neededColor,
            };
        }

        private Color GetLabelColor(SectionStripBlockState state)
        {
            // The clean and dropped block colors are bright enough to read as text; the "needed"
            // block is nearly black by design, so the word gets its own color
            return state switch
            {
                SectionStripBlockState.Clean   => _cleanColor,
                SectionStripBlockState.Dropped => _droppedColor,
                _                              => _neededTextColor,
            };
        }

        private void SetBlockHeight(Image block, float height, bool animate)
        {
            var rect = block.rectTransform;
            rect.DOKill();

            if (!animate || _easeDuration <= 0f)
            {
                SetHeight(rect, height);
                return;
            }

            DOTween
                .To(() => rect.sizeDelta.y, value => SetHeight(rect, value), height, _easeDuration)
                .SetEase(Ease.OutQuad)
                .SetTarget(rect);
        }

        private void SetHeight(RectTransform rect, float height)
        {
            // The width is driven by the horizontal layout group, so only the height is ours
            rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);

            // The blocks sit on a bottom-aligned layout group, which only places them on a
            // rebuild. Marking the container keeps the row lined up while the height tweens;
            // repeat marks within one frame are coalesced by the canvas update loop.
            LayoutRebuilder.MarkLayoutForRebuild(_blockContainer);
        }

        private void BuildBlocks(int count)
        {
            _blockCount = count;
            UpdateSpacing();

            for (int i = 0; i < count; i++)
            {
                var block = GetOrCreateBlock(i);
                SetBlockHeight(block, _blockHeight, false);
                block.gameObject.SetActive(true);
            }

            for (int i = count; i < _blockPool.Count; i++)
            {
                _blockPool[i].rectTransform.DOKill();
                _blockPool[i].gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Decides whether the blocks get their gaps, from how wide each one would end up.
        /// </summary>
        /// <remarks>
        /// A count on its own cannot answer this: the strip is sized to its highway, so the same
        /// forty sections are comfortable across a single player strip and cramped across one of
        /// four. The width the layout group has to share out is the container minus its padding
        /// and the gaps between the blocks, which is what the block width below is.
        /// </remarks>
        private void UpdateSpacing()
        {
            if (_blockLayout == null)
            {
                return;
            }

            _lastLayoutWidth = _blockContainer.rect.width;

            float available = _lastLayoutWidth - _blockLayout.padding.horizontal;
            // With nothing to lay out there is no width to be short of, so the gaps stay
            float spacedWidth = _blockCount <= 0
                ? float.MaxValue
                : (available - _defaultSpacing * (_blockCount - 1)) / _blockCount;

            _blockLayout.spacing = spacedWidth < MIN_SPACED_BLOCK_WIDTH ? 0f : _defaultSpacing;
        }

        private Image GetOrCreateBlock(int index)
        {
            while (_blockPool.Count <= index)
            {
                var blockObject = new GameObject($"Section {_blockPool.Count}",
                    typeof(RectTransform), typeof(Image));
                blockObject.transform.SetParent(_blockContainer, false);

                // Grow from the baseline: with a centered pivot the taller current block would
                // spill half of its extra height below the container
                var blockRect = (RectTransform) blockObject.transform;
                blockRect.pivot = new Vector2(0.5f, 0f);

                var blockImage = blockObject.GetComponent<Image>();
                blockImage.raycastTarget = false;
                _blockPool.Add(blockImage);
            }

            return _blockPool[index];
        }

        private void SetVisible(bool visible)
        {
            _blockContainer.gameObject.SetActive(visible);
            _label.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Shows a dummy strip while the HUD is being repositioned, so the element can be seen
        /// and dragged even when this run has no section state.
        /// </summary>
        /// <remarks>
        /// Wired to the draggable element's edit mode event in the prefab, the same way the solo
        /// box previews itself.
        /// </remarks>
        public void PreviewForEditMode(bool on)
        {
            if (_state != null)
            {
                // The real strip is already showing; nothing to preview
                return;
            }

            // A strip that is switched off must not reappear as a dummy while the HUD is being
            // dragged, or the element would look available when it can never show in a run
            if (!SettingsManager.Settings.TrackSectionCompletion.Value ||
                !SettingsManager.Settings.ShowSectionStrip.Value)
            {
                SetVisible(false);
                return;
            }

            if (!on)
            {
                SetVisible(false);
                return;
            }

            SetVisible(true);
            BuildBlocks(PREVIEW_BLOCK_COUNT);

            for (int i = 0; i < PREVIEW_BLOCK_COUNT; i++)
            {
                _blockPool[i].color = i < PREVIEW_BLOCK_COUNT / 2 ? _perfectedEarlierColor : _neededColor;
            }

            _currentBlock = PREVIEW_BLOCK_COUNT / 2;
            SetBlockHeight(_blockPool[_currentBlock], _currentBlockHeight, false);

            _label.text = $"<color=#{ColorUtility.ToHtmlStringRGB(_nameColor)}>" +
                $"{Localize.Key("Gameplay.SectionStrip.Preview")}</color>";
        }

        protected override void GameplayDestroy()
        {
            if (_state != null)
            {
                _state.BlockStateChanged -= OnBlockStateChanged;
                _state.BlockProgressChanged -= OnBlockProgressChanged;
                _state.CurrentBlockChanged -= OnCurrentBlockChanged;
                _state = null;
            }

            foreach (var block in _blockPool)
            {
                if (block != null)
                {
                    block.rectTransform.DOKill();
                }
            }
        }
    }
}
