using TMPro;
using UnityEngine;
using UnityEngine.UI;
using YARG.Assets.Script.Helpers;
using YARG.Menu.Navigation;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// One row of the rewind picker: a result glyph, the section name, the <c>CURRENT</c> chip and
    /// the section's start time.
    /// </summary>
    /// <remarks>
    /// A fixed slot, not a section. The pane creates one per visible position and slides the
    /// chart's sections through them, exactly as <c>PracticeSectionView</c> does, so the cursor
    /// stays at the vertical centre however long the chart is.
    /// <para>
    /// It is a <see cref="NavigatableBehaviour"/> so that the cursor slot - the only slot the pane
    /// ever registers - can carry the pane's navigation group and take Confirm through the ordinary
    /// <c>NavigationGroup.ConfirmSelection</c> path. Its selected visual is the cursor wash, so the
    /// group drives that too.
    /// </para>
    /// </remarks>
    public class RewindSectionView : NavigatableBehaviour
    {
        [SerializeField]
        private CanvasGroup _canvasGroup;

        [Space]
        [SerializeField]
        private Image _glyphDot;
        [SerializeField]
        private Image _glyphRing;
        [SerializeField]
        private TextMeshProUGUI _sectionName;
        [SerializeField]
        private GameObject _currentChip;
        [SerializeField]
        private TextMeshProUGUI _startTime;

        [Space]
        [Header("Result glyph colours (the section strip's)")]
        [SerializeField]
        private Color _perfectedEarlierColor = new(0.361f, 0.294f, 0.541f);
        [SerializeField]
        private Color _cleanColor = new(0.678f, 0.478f, 1f);
        [SerializeField]
        private Color _droppedColor = new(0.878f, 0.322f, 0.396f);
        [SerializeField]
        private Color _unplayedColor = new(0.149f, 0.161f, 0.255f);
        [SerializeField]
        private Color _inProgressColor = new(0.725f, 0.745f, 0.878f);

        [Space]
        [Header("Row text")]
        [SerializeField]
        private Color _cursorNameColor = new(1f, 1f, 1f, 1f);
        [SerializeField]
        private Color _targetNameColor = new(1f, 1f, 1f, 0.55f);
        [SerializeField]
        private Color _dimNameColor = new(1f, 1f, 1f, 0.15f);
        [SerializeField]
        private Color _cursorTimeColor = new(1f, 1f, 1f, 0.55f);
        [SerializeField]
        private Color _targetTimeColor = new(1f, 1f, 1f, 0.28f);
        [SerializeField]
        private Color _dimTimeColor = new(1f, 1f, 1f, 0.09f);

        private int _relativeIndex;
        private RewindSectionPane _pane;

        public void Init(int relativeIndex, RewindSectionPane pane)
        {
            _relativeIndex = relativeIndex;
            _pane = pane;

            // Rows are slots, not entries: the pointer must not be able to pick one out of the
            // middle of the list and leave the cursor somewhere else.
            SelectOnHover = false;
        }

        public override void Confirm()
        {
            _pane.ConfirmSelection();
        }

        public void UpdateView()
        {
            int realIndex = _pane.HoveredIndex + _relativeIndex;

            if (realIndex < 0 || realIndex >= _pane.Sections.Count)
            {
                _canvasGroup.alpha = 0f;
                return;
            }

            _canvasGroup.alpha = 1f;

            bool isCursor = _relativeIndex == 0;
            bool isCurrent = realIndex == _pane.CurrentIndex;
            bool isTarget = _pane.IsTarget(realIndex);

            _sectionName.text = PracticeSectionHelper.ParseSectionName(_pane.Sections[realIndex].Name);
            _startTime.text = RewindSectionPane.FormatStartTime(_pane.GetTargetTime(realIndex));
            _currentChip.SetActive(isCurrent);

            if (!isTarget)
            {
                _sectionName.color = _dimNameColor;
                _startTime.color = _dimTimeColor;
            }
            else
            {
                _sectionName.color = isCursor ? _cursorNameColor : _targetNameColor;
                _startTime.color = isCursor ? _cursorTimeColor : _targetTimeColor;
            }

            SetGlyph(isCurrent, isTarget, realIndex);
        }

        /// <summary>
        /// Draws the section's result: a ring on the section in progress, a filled dot otherwise.
        /// </summary>
        /// <remarks>
        /// A section that is not a target has no result worth showing, whether that is because the
        /// player has not reached it or because it starts inside a coda, so both get the unplayed
        /// colour. So does a section with no result to report: one with no notes on this
        /// instrument, and every section on a run that has no section state at all (the strip is
        /// off, or the score is already invalid). The in-progress colour is reserved for the ring
        /// on the current section, so that a stateless run does not read as if the whole chart
        /// were in progress.
        /// </remarks>
        private void SetGlyph(bool isCurrent, bool isTarget, int sectionIndex)
        {
            if (isCurrent && isTarget)
            {
                _glyphDot.gameObject.SetActive(false);
                _glyphRing.gameObject.SetActive(true);
                _glyphRing.color = _inProgressColor;
                return;
            }

            _glyphRing.gameObject.SetActive(false);
            _glyphDot.gameObject.SetActive(true);

            if (!isTarget)
            {
                _glyphDot.color = _unplayedColor;
                return;
            }

            var state = _pane.SectionState;
            if (state == null || !state.TryGetSectionState(sectionIndex, out var blockState))
            {
                _glyphDot.color = _unplayedColor;
                return;
            }

            _glyphDot.color = blockState switch
            {
                SectionStripBlockState.PerfectedEarlier => _perfectedEarlierColor,
                SectionStripBlockState.Clean            => _cleanColor,
                SectionStripBlockState.Dropped          => _droppedColor,
                _                                       => _unplayedColor,
            };
        }
    }
}
