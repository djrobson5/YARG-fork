using System;
using System.Collections.Generic;
using UnityEngine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Replays;

namespace YARG.Gameplay.HUD
{
    public class GenericPause : GameplayBehaviour
    {
        /// <summary>
        /// The pause list's own navigation group, so the rewind row can be pulled out of it when
        /// the run is not eligible.
        /// </summary>
        /// <remarks>
        /// Serialized rather than found, because the picker pane carries a second group and
        /// <c>GetComponentInChildren</c> would be a coin toss between them.
        /// </remarks>
        [SerializeField]
        private NavigationGroup _pauseListNavGroup;

        [Space]
        [SerializeField]
        private GameObject _rewindRowObject;
        [SerializeField]
        private RewindSectionPane _rewindPane;

        /// <summary>
        /// White at about 45%: what the pause list fades to while the picker has focus.
        /// </summary>
        private static readonly Color DimmedOptionColor = new(1f, 1f, 1f, 0.45f);

        /// <summary>
        /// The pointer settings each pause row had before the picker took focus.
        /// </summary>
        private readonly Dictionary<NavigatableBehaviour, (bool Hover, bool Click)> _pointerStates = new();

        private CanvasGroup _pauseListCanvasGroup;

        protected PauseMenuManager PauseMenuManager { get; private set; }

        protected override void GameplayAwake()
        {
            PauseMenuManager = FindAnyObjectByType<PauseMenuManager>();
        }

        protected virtual void OnEnable()
        {
            // The page can be switched off with the picker still open (Start, Escape, a quit, a
            // restart), and nothing on that path runs the picker's own close. Force it shut before
            // anything else, or this open inherits a dimmed list, a stale cursor and a navigation
            // group that pushes itself as current with no scheme that can leave it.
            ResetRewindPane();

            ApplyRewindRowVisibility();

            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back),
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
            }, false));
        }

        protected virtual void OnDisable()
        {
            // Before this page's own scheme, so the two come off the stack in the order they went
            // on. The picker's own OnDisable would pop it too, but only after Unity has finished
            // with this component, which is the wrong way round.
            ResetRewindPane();

            Navigator.Instance.PopScheme();
        }

        public virtual void Back()
        {
            PauseMenuManager.PopAllMenusWithResume();
        }

        public virtual void Restart()
        {
            PauseMenuManager.Restart();
        }

        /// <summary>
        /// Opens the section picker in this page's graphics container.
        /// </summary>
        /// <remarks>
        /// No <c>PushMenu</c>: the pane is part of this page, and the pause list stays on screen
        /// behind it (<c>docs/rewind-design.md</c>, "Pause row and picker" -> Navigation plumbing).
        /// </remarks>
        public void RewindToSection()
        {
            if (_rewindPane == null || GameManager == null || !GameManager.CanRewindToSection)
            {
                return;
            }

            _rewindPane.Open(this, GameManager);
        }

        /// <summary>
        /// Fades the pause list back while the picker has focus, and restores it afterwards.
        /// </summary>
        /// <remarks>
        /// Only the option text is dimmed, not the whole list: the selection wash on the Rewind row
        /// has to stay at full strength so the list still shows where the player came from. That
        /// rules out dimming through a <c>CanvasGroup</c> alpha, which would take the wash with it.
        /// <para>
        /// The pointer goes with it. A hover or a press on a row that is still on screen selects
        /// it, and selecting pushes the list's navigation group back to the top of the stack; Green
        /// would then confirm <i>that</i> row, because
        /// <c>NavigationScheme.Entry.NavigateSelect</c> always confirms the current group.
        /// </para>
        /// </remarks>
        public void SetPauseListDimmed(bool dimmed)
        {
            foreach (var option in GetComponentsInChildren<GenericPauseOption>(true))
            {
                option.SetTextColor(dimmed ? DimmedOptionColor : Color.white);
            }

            SetPauseListPointerEnabled(!dimmed);
        }

        /// <summary>
        /// Turns pointer interaction with the pause list on or off, leaving it fully visible.
        /// </summary>
        /// <remarks>
        /// Two layers, because neither is enough on its own. The two
        /// <see cref="NavigatableBehaviour"/> pointer flags stop a hover or a press from moving the
        /// selection, which is what drags the navigation group across; the raycast block stops the
        /// press reaching the row at all, which is what <c>NavigatableButton.OnPointerDown</c>
        /// needs, since it invokes the row's action without consulting those flags. Alpha is left
        /// alone, so nothing changes visually.
        /// </remarks>
        protected void SetPauseListPointerEnabled(bool enabled)
        {
            if (_pauseListNavGroup == null)
            {
                return;
            }

            if (_pauseListCanvasGroup == null &&
                !_pauseListNavGroup.TryGetComponent(out _pauseListCanvasGroup))
            {
                _pauseListCanvasGroup = _pauseListNavGroup.gameObject.AddComponent<CanvasGroup>();
            }

            _pauseListCanvasGroup.blocksRaycasts = enabled;

            foreach (var navigatable in _pauseListNavGroup.GetComponentsInChildren<NavigatableBehaviour>(true))
            {
                if (!enabled)
                {
                    // Only the first disable records the originals; a second one would record the
                    // values this method has already overwritten.
                    if (!_pointerStates.ContainsKey(navigatable))
                    {
                        _pointerStates[navigatable] = (navigatable.SelectOnHover, navigatable.SelectOnClick);
                    }

                    navigatable.SelectOnHover = false;
                    navigatable.SelectOnClick = false;
                }
                else if (_pointerStates.TryGetValue(navigatable, out var prior))
                {
                    navigatable.SelectOnHover = prior.Hover;
                    navigatable.SelectOnClick = prior.Click;
                }
            }

            if (enabled)
            {
                _pointerStates.Clear();
            }
        }

        /// <summary>
        /// Forces the section picker shut, for the paths that switch this page off without going
        /// through the picker's own Back.
        /// </summary>
        protected void ResetRewindPane()
        {
            if (_rewindPane != null)
            {
                _rewindPane.ResetClosed();
            }
        }

        /// <summary>
        /// Shows or hides the rewind row for the run as it currently stands.
        /// </summary>
        /// <remarks>
        /// Re-evaluated on every open rather than latched. Most of the gate is fixed for the run
        /// (player count, bots, practice, replay) but the song-time part is not, so a pause taken
        /// during the start delay must not hide the row for the rest of the song.
        /// <para>
        /// The row is only deactivated, never pulled out of the navigation group. Every selection
        /// move in <see cref="NavigationGroup"/> already skips entries that are not
        /// <c>activeInHierarchy</c>, and removing it would be one-way: re-adding appends to the end
        /// of the list, which would move the row out of its authored place in the order.
        /// </para>
        /// </remarks>
        protected void ApplyRewindRowVisibility()
        {
            if (_rewindRowObject == null)
            {
                return;
            }

            bool available = GameManager != null && GameManager.CanRewindToSection;
            if (available != _rewindRowObject.activeSelf)
            {
                _rewindRowObject.SetActive(available);
            }
        }

        public void TogglePractice()
        {
            GlobalVariables.State.IsPractice = !GlobalVariables.State.IsPractice;
            GlobalVariables.State.SavedInputTime = GameManager.InputTime;
            PauseMenuManager.Restart();
        }

        public void SaveReplay()
        {
            bool succeeded = false;
            try
            {
                succeeded = GameManager.SaveReplay(GameManager.InputTime, ReplayContainer.ReplayDirectory) != null;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to save replay mid-song");
            }

            if (succeeded)
            {
                DialogManager.Instance.ShowMessage("Replay Saved",
                    "The replay was successfully saved mid-song. This replay can be accessed in the " +
                    "\"Imported Songs\" tab in the \"History\" menu.");
            }
            else
            {
                DialogManager.Instance.ShowMessage("Failed to Save Replay",
                    "The replay was unable to be saved mid-song. This could be because the replay only had bots " +
                    "or an error occurred. Please check the logs for more info.");
            }
        }

        public void BackToLibrary()
        {
            PauseMenuManager.Quit();
        }

        public void OpenQuickSettings()
        {
            PauseMenuManager.PushMenu(PauseMenuManager.Menu.QuickSettings);
        }
    }
}
