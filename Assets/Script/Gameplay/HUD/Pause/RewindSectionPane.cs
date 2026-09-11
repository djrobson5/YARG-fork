using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core.Chart;
using YARG.Core.Input;
using YARG.Localization;
using YARG.Menu.Navigation;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    /// <summary>
    /// The rewind-to-section picker, shown in the pause page's otherwise empty
    /// <c>Graphics Container</c> (<c>docs/rewind-design.md</c>, "Pause row and picker").
    /// </summary>
    /// <remarks>
    /// Not a <c>PauseMenuObject</c>. The pane lives on the same page as the pause list, which
    /// stays on screen behind it, so opening it swaps the active <see cref="NavigationGroup"/>
    /// rather than pushing a menu. The group swap is the <c>QuickSettings</c> sub-settings idiom:
    /// <see cref="NavigationGroup.ClearNavigatables"/>, <see cref="NavigationGroup.AddNavigatable"/>
    /// and <see cref="NavigationGroup.SelectFirst"/>.
    /// <para>
    /// The list itself is the practice picker's shape (<c>PracticeSectionMenu</c>): a fixed ring of
    /// row views laid out by a centred <c>VerticalLayoutGroup</c> inside a soft mask, with the
    /// chart's sections sliding through them so the cursor never leaves the vertical centre. There
    /// is no scroll view and no grouping, however long the chart.
    /// </para>
    /// </remarks>
    public class RewindSectionPane : MonoBehaviour
    {
        /// <summary>
        /// How many row views sit above and below the cursor.
        /// </summary>
        /// <remarks>
        /// Thirteen slots at a 63 px pitch is 819 px of content in a viewport of about 556, so the
        /// outermost rows sit under the soft mask and are the fade itself rather than spare
        /// capacity. Four rows either side of the cursor read as fully lit.
        /// </remarks>
        private const int SECTION_VIEW_EXTRA = 6;

        private const float SCROLL_TIME = 1f / 60f;

        [SerializeField]
        private Transform _sectionContainer;
        [SerializeField]
        private GameObject _sectionViewPrefab;

        [Space]
        [SerializeField]
        private NavigationGroup _navGroup;
        [SerializeField]
        private TextMeshProUGUI _captionText;

        private GameManager  _gameManager;
        private GenericPause _owner;

        private readonly List<RewindSectionView> _views = new();

        /// <summary>The cursor slot: the one view that is ever registered as navigatable.</summary>
        private RewindSectionView _cursorView;

        private IReadOnlyList<Section> _sections;

        /// <summary>Whether each section is a rewind target, rather than dimmed and skipped.</summary>
        private bool[] _isTarget;

        /// <summary>Where each section rewinds to, which is not its marker for the first one.</summary>
        private double[] _targetTimes;

        private bool  _navigationPushed;
        private float _scrollTimer;

        public IReadOnlyList<Section> Sections => _sections;

        /// <summary>The section under the cursor.</summary>
        public int HoveredIndex { get; private set; }

        /// <summary>The section the run is in right now, which carries the <c>CURRENT</c> chip.</summary>
        public int CurrentIndex { get; private set; }

        /// <summary>The one player's live section results, or <c>null</c> if this run has none.</summary>
        public SectionStripState SectionState { get; private set; }

        public bool IsTarget(int sectionIndex)
            => _isTarget != null && sectionIndex >= 0 && sectionIndex < _isTarget.Length &&
                _isTarget[sectionIndex];

        /// <summary>The start time to print against a section: song start for the first one.</summary>
        public double GetTargetTime(int sectionIndex) => _targetTimes[sectionIndex];

        /// <summary>
        /// Opens the picker and moves focus to it, leaving the pause list on screen behind.
        /// </summary>
        public void Open(GenericPause owner, GameManager gameManager)
        {
            var sections = gameManager.Chart?.Sections;
            if (sections is null || sections.Count == 0)
            {
                return;
            }

            _owner = owner;
            _gameManager = gameManager;
            _sections = sections;

            // Active first: the row views are instantiated under it, and their LocalizeText
            // components only run their Awake when they land in an active hierarchy.
            gameObject.SetActive(true);

            BuildViews();
            BuildTargets();

            // The lead-in length is a setting and is editable from this same pause menu, so the
            // caption is written every time the pane opens rather than once at build time.
            var leadIn = SettingsManager.Settings.RewindLeadIn.Value;
            _captionText.text = Localize.KeyFormat("Menu.Pause.Rewind.LeadInCaption",
                leadIn.ToString("0.#"),
                Localize.Key(leadIn == 1
                    ? "Menu.Pause.Rewind.SecondSingular"
                    : "Menu.Pause.Rewind.SecondPlural"));

            // One Confirm restarts the section in progress; Up walks back from there.
            HoveredIndex = IsTarget(CurrentIndex) ? CurrentIndex : PreviousTarget(CurrentIndex);
            UpdateViews();

            PushNavigation();

            // The group swap. Activating the pane pushed its group onto the navigation stack (it
            // is a default group, the QuickSettings sub-settings shape), so it is now the current
            // one; the pause list's own group stays enabled underneath, and therefore keeps the
            // Rewind row selected and washed, but Up, Down and Confirm all land here instead.
            _navGroup.ClearNavigatables();
            _navGroup.AddNavigatable(_cursorView);
            _navGroup.SelectFirst();

            _owner.SetPauseListDimmed(true);
        }

        /// <summary>
        /// Closes the picker without rewinding and hands focus back to the pause list.
        /// </summary>
        public void Close()
        {
            ResetClosed();
        }

        /// <summary>
        /// Puts the picker back into its closed state, from anywhere.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Close"/> only so the pause page can call it by name. The page
        /// can be switched off with the picker open - Start or Escape resumes through
        /// <c>GameManager.Resume</c>, which calls <c>PauseMenuManager.PopAllMenus</c> - and on that
        /// path nothing runs Back. Without this the next pause would inherit a dimmed, unclickable
        /// list, a cursor left on a section from the previous pause, and a navigation group that
        /// pushes itself as current the moment the pane activates while holding the old cursor
        /// slot: Green would rewind to the stale index, and no scheme with a Back entry would ever
        /// have been pushed to get out of it.
        /// <para>
        /// Safe to call when already closed; every step is idempotent.
        /// </para>
        /// </remarks>
        public void ResetClosed()
        {
            PopNavigation();

            // Clears the cursor wash, and drops the slot so the next open starts clean.
            _navGroup.ClearNavigatables();

            if (_owner != null)
            {
                _owner.SetPauseListDimmed(false);
                _owner = null;
            }

            // Deactivating takes the pane's group back off the navigation stack, which makes the
            // pause list's group current again. Its selection was never cleared, so the Rewind row
            // is still the one under the cursor.
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Rewinds to the section under the cursor. There is no confirmation step.
        /// </summary>
        public void ConfirmSelection()
        {
            if (_sections is null || !IsTarget(HoveredIndex))
            {
                return;
            }

            double target = _targetTimes[HoveredIndex];

            // Close first: the rewind unwinds the pause underneath us, and the pane must have
            // given the navigation scheme and group back before that happens.
            var gameManager = _gameManager;
            Close();

            gameManager.RewindToSectionFromPause(target);
        }

        private void BuildViews()
        {
            if (_views.Count > 0)
            {
                return;
            }

            for (int i = 0; i < SECTION_VIEW_EXTRA * 2 + 1; i++)
            {
                int relativeIndex = i - SECTION_VIEW_EXTRA;

                var viewObject = Instantiate(_sectionViewPrefab, _sectionContainer);
                var view = viewObject.GetComponent<RewindSectionView>();
                view.Init(relativeIndex, this);
                _views.Add(view);

                if (relativeIndex == 0)
                {
                    _cursorView = view;
                }
            }
        }

        /// <summary>
        /// Works out which sections can be rewound to, and where each one rewinds to.
        /// </summary>
        /// <remarks>
        /// Rebuilt on every open, because both the run's position and its section results move
        /// while the song plays. Two things disqualify a section: being ahead of the player, and
        /// starting inside a BRE or coda, whose lane timers and success latch cannot be re-entered
        /// cleanly without a <c>YARG.Core</c> edit. Both are drawn dimmed and skipped by the
        /// cursor, so the two cases look the same to the player.
        /// </remarks>
        private void BuildTargets()
        {
            int count = _sections.Count;
            _isTarget = new bool[count];
            _targetTimes = new double[count];

            // While a lead-in is running the clock is deliberately behind the marker, so "where the
            // player is" is the marker, not the song time.
            double reference = _gameManager.RewindReferenceSongTime;
            var codaRanges = CollectCodaRanges();

            CurrentIndex = 0;
            for (int i = 0; i < count; i++)
            {
                // The first section owns the intro, so it rewinds to the start of the song, which
                // matches section FC's index-0 folding. Negative song time is already accepted.
                _targetTimes[i] = i == 0 ? 0 : _sections[i].Time;

                bool reached = i == 0 || _sections[i].Time <= reference;
                if (reached)
                {
                    CurrentIndex = i;
                }

                _isTarget[i] = reached && !StartsInsideCoda(_sections[i], codaRanges);
            }

            SectionState = null;
            foreach (var player in _gameManager.Players)
            {
                if (player.SectionState != null)
                {
                    SectionState = player.SectionState;
                    break;
                }
            }
        }

        private List<(double Start, double End)> CollectCodaRanges()
        {
            var ranges = new List<(double Start, double End)>();

            foreach (var player in _gameManager.Players)
            {
                var phrases = player.TrackPhrases;
                if (phrases is null)
                {
                    continue;
                }

                foreach (var phrase in phrases)
                {
                    if (phrase.Type is PhraseType.BigRockEnding or PhraseType.Coda)
                    {
                        ranges.Add((phrase.Time, phrase.TimeEnd));
                    }
                }
            }

            return ranges;
        }

        /// <remarks>
        /// Half-open: a section starting exactly where the coda ends is outside it, and the
        /// sections before one are ordinary targets because the fresh engine rebuilds the coda.
        /// </remarks>
        private static bool StartsInsideCoda(Section section, List<(double Start, double End)> ranges)
        {
            foreach (var (start, end) in ranges)
            {
                if (section.Time >= start && section.Time < end)
                {
                    return true;
                }
            }

            return false;
        }

        private int PreviousTarget(int from)
        {
            for (int i = from; i >= 0; i--)
            {
                if (_isTarget[i])
                {
                    return i;
                }
            }

            // Index 0 is always a target, so this is unreachable; return it anyway rather than -1.
            return 0;
        }

        /// <summary>
        /// Steps the cursor to the next target in the given direction, skipping dimmed sections.
        /// </summary>
        /// <remarks>
        /// No wrap-around. The list is the whole chart with everything ahead of the player dimmed,
        /// so wrapping would jump the cursor across the song rather than to a neighbour.
        /// </remarks>
        private void MoveCursor(int direction)
        {
            for (int i = HoveredIndex + direction; i >= 0 && i < _sections.Count; i += direction)
            {
                if (_isTarget[i])
                {
                    HoveredIndex = i;
                    UpdateViews();
                    return;
                }
            }
        }

        private void UpdateViews()
        {
            foreach (var view in _views)
            {
                view.UpdateView();
            }
        }

        private void PushNavigation()
        {
            if (_navigationPushed)
            {
                return;
            }

            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Close),
                new NavigationScheme.Entry(MenuAction.Up, "Menu.Common.Up", () => MoveCursor(-1)),
                new NavigationScheme.Entry(MenuAction.Down, "Menu.Common.Down", () => MoveCursor(1)),
            }, false));

            _navigationPushed = true;
        }

        private void PopNavigation()
        {
            if (!_navigationPushed)
            {
                return;
            }

            Navigator.Instance.PopScheme();
            _navigationPushed = false;
        }

        private void OnDisable()
        {
            // Belt and braces: the pane can be switched off with the whole pause page (a restart,
            // a quit), and the scheme must not outlive it.
            PopNavigation();
        }

        private void Update()
        {
            if (_scrollTimer > 0f)
            {
                _scrollTimer -= Time.unscaledDeltaTime;
                return;
            }

            var delta = Mouse.current.scroll.ReadValue().y * Time.unscaledDeltaTime;

            if (delta > 0f)
            {
                MoveCursor(-1);
                _scrollTimer = SCROLL_TIME;
            }
            else if (delta < 0f)
            {
                MoveCursor(1);
                _scrollTimer = SCROLL_TIME;
            }
        }

        /// <summary>
        /// A section's start time as the picker prints it.
        /// </summary>
        public static string FormatStartTime(double seconds)
        {
            if (seconds < 0)
            {
                seconds = 0;
            }

            return TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
        }
    }
}
