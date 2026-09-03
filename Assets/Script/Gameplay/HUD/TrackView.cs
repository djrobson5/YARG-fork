using System;
using UnityEngine;
using YARG.Core.Engine;
using YARG.Gameplay.Visuals;
using YARG.Helpers.Extensions;
using YARG.Player;

namespace YARG.Gameplay.HUD
{
    public class TrackView : MonoBehaviour
    {

        [SerializeField]
        private RectTransform _highwayEditContainer;
        [SerializeField]
        private RectTransform _topElementContainer;
        [SerializeField]
        private RectTransform _centerElementContainer;
        [SerializeField]
        private RectTransform _scaleContainer;

        [Space]
        [SerializeField]
        private SoloBox _soloBox;
        [SerializeField]
        private TextNotifications _textNotifications;
        [SerializeField]
        private CountdownDisplay _countdownDisplay;
        [SerializeField]
        private PlayerNameDisplay _playerNameDisplay;
        [SerializeField]
        private SectionStrip _sectionStrip;


        private HighwayCameraRendering _highwayRenderer;
        private Vector3 _lastTrackPlayerPosition;

        private const float CENTER_ELEMENT_DEPTH = 0.35f;

        // The gap between the top of the top element container and the bottom of the section
        // strip, in canvas units. The strip's pivot is its bottom edge, so everything inside the
        // top container (the solo box included) is left untouched below it.
        private const float SECTION_STRIP_GAP = 6f;

        // Perspective leaves the far end of the highway only about 45% as wide on screen as the
        // near end, so a strip drawn at exactly that width would read as a stub floating over the
        // vanishing point. Doubling it fills the space the highway visually occupies while still
        // landing well inside the slice of screen the highway owns at any player count.
        private const float SECTION_STRIP_WIDTH_FACTOR = 2.0f;

        // Screen pixels left between a strip and the edge of the slot its highway owns, so two
        // strips always have visible air between them.
        private const float SECTION_STRIP_SLOT_MARGIN = 32f;

        // The widest the strip is ever drawn, in its own canvas units. This is the width the
        // prefab ships with, and single player has room for all of it.
        private const float SECTION_STRIP_MAX_WIDTH = 768f;

        // Below this the section name stops being readable even at the label's minimum auto-size,
        // so the strip is allowed to overhang its slot rather than shrink further. At any sane
        // resolution the slot is far wider than this, so the floor never actually binds.
        private const float SECTION_STRIP_MIN_WIDTH = 280f;

        private DraggableHudElement _topDraggable;
        private DraggableHudElement _highwayDraggable;
        private DraggableHudElement _sectionStripDraggable;
        private RectTransform _sectionStripRect;
        private readonly Vector3[] _topElementCorners = new Vector3[4];
        private RectTransform _topElementParentRect;
        private Canvas _highwayEditCanvas;
        private RectTransform _highwayEditParentRect;
        private bool _defaultsInitialized;

        // Which highway this view belongs to and how many there are, remembered from the last
        // layout pass. The drag callbacks have no index to hand us and every one of them only
        // fires in single player, where the answer is always highway 0 of 1.
        private int _highwayIndex;
        private int _highwayCount = 1;

        private readonly Vector3 _hiddenPosition = new(-10000f, -10000f, 0f);
        private float ExtraTopElementOffset => 8f * Screen.height / 1000f;

        public void Initialize(HighwayCameraRendering highwayRenderer)
        {
            _highwayRenderer = highwayRenderer;
            _topDraggable = _topElementContainer.GetComponent<DraggableHudElement>();
            _highwayDraggable = _highwayEditContainer.GetComponent<DraggableHudElement>();
            _sectionStripRect = _sectionStrip.GetComponent<RectTransform>();
            _sectionStripDraggable = _sectionStrip.GetComponent<DraggableHudElement>();
            _topElementParentRect = _topElementContainer.parent as RectTransform;
            _highwayEditCanvas = _highwayEditContainer.GetComponentInParent<Canvas>();
            _highwayEditParentRect = _highwayEditContainer.parent as RectTransform;
            _defaultsInitialized = false;
            _highwayDraggable.PositionChanged += OnHighwayDraggablePositionChanged;
            _highwayDraggable.ScaleChanged += OnHighwayDraggableScaleChanged;
            _topDraggable.PositionChanged += OnTopDraggablePositionChanged;
            _highwayRenderer.SetScaleMultiplier(_highwayDraggable.CurrentScale);

            _centerElementContainer.position = _hiddenPosition;
        }

        public void UpdateHUDPosition(int highwayIndex, int highwayCount)
        {
            // Scale ui according to number of highways,
            // 1 highway = 1.0 scale, 2 highways = 0.9 scale, 3 highways = 0.8 scale, etc, minimum of 0.5
            var newScale = Math.Max(0.5f, 1.1f - (0.1f * highwayCount));
            _scaleContainer.localScale = _scaleContainer.localScale.WithX(newScale).WithY(newScale);

            _highwayIndex = highwayIndex;
            _highwayCount = Math.Max(1, highwayCount);

            if (!_defaultsInitialized)
            {
                SetupDefaultHudPositions();
                _defaultsInitialized = true;
            }

            UpdateHudElements(highwayIndex);
        }

        private void UpdateHudElements(int highwayIndex)
        {
            // Apply highway offset first so top/center positions are calculated from the current track position.
            UpdateTrackPosition(highwayIndex);
            UpdateTopHud(highwayIndex);
            UpdateSectionStrip();
            UpdateCenterHud(highwayIndex);
        }

        private void SetupDefaultHudPositions()
        {
            // Compute highway default at center (offset 0)
            _highwayRenderer.SetHorizontalOffsetPx(0);
            _highwayDraggable.SetDefaultPosition(GetHighwayDefaultPosition());

            SetHighwayOffsetX(_highwayDraggable.CurrentPosition.x);
            UpdateTopDefaultPosition();
        }

        private void UpdateTopDefaultPosition()
        {
            _topDraggable.SetDefaultPosition(GetTopDefaultPosition());
        }

        private Vector2 GetTopDefaultPosition()
        {
            var topScreenPosition =
                _highwayRenderer.GetTrackPositionScreenSpaceRaised(0, 0.5f, 1.0f)?.AddY(ExtraTopElementOffset)
                ?? _hiddenPosition;
            return _topElementParentRect.ScreenPointToLocalPoint(topScreenPosition) ?? _hiddenPosition;
        }

        private Vector2 GetHighwayDefaultPosition()
        {
            var trackBounds = _highwayRenderer.GetTrackBoundsScreenSpaceRaised(0);
            return _highwayEditParentRect.ScreenPointToLocalPoint(trackBounds.center) ?? _hiddenPosition;
        }

        private void UpdateTopHud(int highwayIndex)
        {
            if (_topDraggable.HasCustomPosition)
            {
                return;
            }

            // Place top elements at 100% depth of the track, plus some extra amount above the track.
            var topPosition =
                _highwayRenderer.GetTrackPositionScreenSpace(highwayIndex, 0.5f, 1.0f)?.AddY(ExtraTopElementOffset)
                ?? _hiddenPosition;
            _topElementContainer.position = topPosition;
        }

        /// <summary>
        /// Sits the section strip directly on top of the top element container.
        /// </summary>
        /// <remarks>
        /// Driven off the container's own world corners rather than off the track position, so it
        /// follows the container whether that was placed automatically or dragged there, and so
        /// it can never overlap the solo box or the streak text that live inside it.
        /// </remarks>
        private void UpdateSectionStrip()
        {
            bool hasCustomPosition = _sectionStripDraggable.HasCustomPosition;

            // Even a strip the player has placed themselves is kept inside its own slot, so a
            // saved single player position can never spill across a neighbouring highway when
            // the same profile is later used in a band
            UpdateSectionStripWidth(hasCustomPosition);

            if (hasCustomPosition)
            {
                return;
            }

            // 0 is the bottom left corner and they run counter-clockwise, so 1 and 2 are the top
            _topElementContainer.GetWorldCorners(_topElementCorners);
            var topCenter = (_topElementCorners[1] + _topElementCorners[2]) * 0.5f;

            float gap = SECTION_STRIP_GAP * _scaleContainer.localScale.y * _highwayEditCanvas.scaleFactor;
            _sectionStripRect.position = topCenter.AddY(gap);
        }

        /// <summary>
        /// Sizes the strip to the highway it belongs to, rather than to the fixed width the
        /// prefab ships with.
        /// </summary>
        /// <remarks>
        /// The prefab width is sized for single player. With two or more highways it is wider
        /// than the space a highway has, and the strips run into each other in the middle of the
        /// screen. The width is taken from the highway's own screen-space extent at the far end
        /// (the same track-position API the top HUD is placed with) and then capped to the slice
        /// of the screen this highway owns, so the strips can never touch however the highways
        /// end up scaled.
        /// </remarks>
        private void UpdateSectionStripWidth(bool hasCustomPosition)
        {
            float canvasScale = _highwayEditCanvas.scaleFactor;
            float containerScale = _scaleContainer.localScale.x;
            if (canvasScale <= 0f || containerScale <= 0f)
            {
                return;
            }

            // The strip's slice of the screen, in canvas units
            float slotWidth = ((float) Screen.width / _highwayCount - SECTION_STRIP_SLOT_MARGIN) / canvasScale;
            float width = slotWidth;

            if (!hasCustomPosition)
            {
                // Depth 1.0 is the far end of the highway, where the strip sits
                var farLeft = _highwayRenderer.GetTrackPositionScreenSpace(_highwayIndex, 0f, 1f);
                var farRight = _highwayRenderer.GetTrackPositionScreenSpace(_highwayIndex, 1f, 1f);
                if (farLeft == null || farRight == null)
                {
                    return;
                }

                float farWidth = Math.Abs(farRight.Value.x - farLeft.Value.x) / canvasScale;
                width = Math.Min(farWidth * SECTION_STRIP_WIDTH_FACTOR, slotWidth);
            }

            // Everything above is in the canvas units of the root canvas, but the strip hangs off
            // the scale container, so its own units are that much smaller
            width /= containerScale;

            width = Mathf.Clamp(width, SECTION_STRIP_MIN_WIDTH, SECTION_STRIP_MAX_WIDTH);

            if (Mathf.Approximately(_sectionStripRect.sizeDelta.x, width))
            {
                return;
            }

            // The label and the block container are both stretch-anchored to the strip, so they
            // follow this on their own
            _sectionStripRect.sizeDelta = new Vector2(width, _sectionStripRect.sizeDelta.y);
        }

        private void UpdateCenterHud(int highwayIndex)
        {
            var trackPositionScreenSpace =
                _highwayRenderer.GetTrackPositionScreenSpace(highwayIndex, 0.5f, CENTER_ELEMENT_DEPTH);
            var centerPosition = trackPositionScreenSpace ?? _hiddenPosition;
            _centerElementContainer.transform.position = centerPosition;
        }

        // Keep the edit box sized to the track bounds and vertically centered to the track.
        private void UpdateTrackPosition(int highwayIndex)
        {
            bool hasCustomPosition = _highwayDraggable.HasCustomPosition;
            SetHighwayOffsetX(hasCustomPosition ? _highwayDraggable.CurrentPosition.x : 0f);

            var trackBounds = _highwayRenderer.GetTrackBoundsScreenSpace(highwayIndex);
            if (trackBounds == null)
            {
                _highwayEditContainer.position = _hiddenPosition;
                return;
            }

            //Set highway edit box size in canvas units
            float width = trackBounds.Value.width / _highwayEditCanvas.scaleFactor;
            float height = trackBounds.Value.height / _highwayEditCanvas.scaleFactor;
            _highwayEditContainer.sizeDelta = new Vector2(width, height);

            //Center the highway edit box on the highway
            var trackCenterScreenSpace = trackBounds.Value.center;
            var localCenter = _highwayEditParentRect.ScreenPointToLocalPoint(trackCenterScreenSpace);
            if (localCenter == null)
            {
                _highwayEditContainer.position = _hiddenPosition;
                return;
            }

            float targetX = hasCustomPosition
                ? _highwayDraggable.CurrentPosition.x
                : localCenter.Value.x;
            _highwayEditContainer.anchoredPosition = new Vector2(targetX, localCenter.Value.y);
        }

        private void OnHighwayDraggablePositionChanged(Vector2 position)
        {
            UpdateHudElements(0);
            UpdateTopDefaultPosition();
        }

        /// <remarks>
        /// The strip rides on top of the top element container, so it has to follow when that
        /// container is dragged in HUD edit mode, where nothing else is driving a HUD update.
        /// </remarks>
        private void OnTopDraggablePositionChanged(Vector2 position)
        {
            UpdateSectionStrip();
        }

        private void OnHighwayDraggableScaleChanged(float scale)
        {
            _highwayRenderer.SetScaleMultiplier(scale);
            UpdateTopHud(0);
            UpdateSectionStrip();
            UpdateCenterHud(0);
            UpdateTrackPosition(0);
            UpdateTopDefaultPosition();
        }

        private void SetHighwayOffsetX(float xOffsetLocal)
        {
            float offsetPx = xOffsetLocal * _highwayEditCanvas.scaleFactor;
            _highwayRenderer.SetHorizontalOffsetPx(offsetPx);
        }

        public void UpdateCountdown(double countdownLength, double endTime)
        {
            _countdownDisplay.UpdateCountdown(countdownLength, endTime);
        }

        /// <summary>
        /// Hands the strip the player's section state, or <c>null</c> to hide it.
        /// </summary>
        public void SetSectionState(SectionStripState state)
        {
            _sectionStrip.SetState(state);
        }

        public void StartSolo(SoloSection solo)
        {
            _soloBox.StartSolo(solo);

            // No text notifications during the solo
            _textNotifications.SetActive(false);
        }

        public void EndSolo(int soloBonus)
        {
            _soloBox.EndSolo(soloBonus, () =>
            {
                // Show text notifications again
                _textNotifications.SetActive(true);
            });
        }

        public void UpdateNoteStreak(int streak)
        {
            _textNotifications.UpdateNoteStreak(streak);
        }

        public void ShowNewHighScore()
        {
            _textNotifications.ShowNewHighScore();
        }

        public void ShowFullCombo()
        {
            _textNotifications.ShowFullCombo();
        }

        public void ShowHotStart()
        {
            _textNotifications.ShowHotStart();
        }

        public void ShowBassGroove()
        {
            _textNotifications.ShowBassGroove();
        }

        public void ShowStarPowerReady()
        {
            _textNotifications.ShowStarPowerReady();
        }

        public void ShowStrongFinish()
        {
            _textNotifications.ShowStrongFinish();
        }

        public void ShowPlayerName(YargPlayer player)
        {
            _playerNameDisplay.ShowPlayer(player);
        }

        public void ForceReset()
        {
            _textNotifications.SetActive(true);

            _soloBox.ForceReset();
            _textNotifications.ForceReset();
            _countdownDisplay.ForceReset();
        }

        private void OnDestroy()
        {
            _highwayDraggable.PositionChanged -= OnHighwayDraggablePositionChanged;
            _highwayDraggable.ScaleChanged -= OnHighwayDraggableScaleChanged;
            _topDraggable.PositionChanged -= OnTopDraggablePositionChanged;
        }
    }
}
