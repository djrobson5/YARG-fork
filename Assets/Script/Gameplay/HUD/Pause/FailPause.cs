using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Menu.Navigation;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    public class FailPause : GenericPause
    {
        [SerializeField]
        private GameObject _separatorObject;

        protected override void OnEnable()
        {
            // Not base.OnEnable(): the fail menu pushes its own (Back-less) scheme after a delay.
            ResetRewindPane();

            // The rewind row still has to be gated, and the design offers it here
            // (docs/rewind-design.md, "Fail menu").
            ApplyRewindRowVisibility();

            // Nothing on the list can be clicked until that scheme is up. The half second exists to
            // swallow the button mash that failed the song, and until it elapses there is no scheme
            // at all, so a mouse click on the rewind row would open the picker and then have this
            // menu's Back-less scheme land on top of the picker's, stranding the player in it.
            SetPauseListPointerEnabled(false);

            HandleNavigationScheme();
        }

        private async void HandleNavigationScheme()
        {
            await UniTask.WaitForSeconds(0.5f, true);
            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
            }, false));

            if (this != null)
            {
                SetPauseListPointerEnabled(true);
            }
        }
        public void EnableNoFail(bool resume)
        {
            // It feels a bit icky reaching down into the settings like this
            SettingsManager.Settings.NoFail.SetValueWithoutNotify(NoFailMode.On);
            if (resume)
            {
                // UnfailSong already resumes, don't do it twice by calling Back()
                PauseMenuManager.PopAllMenus();
                GameManager.UnfailSong();
            }
            else
            {
                Restart();
            }
        }
    }
}