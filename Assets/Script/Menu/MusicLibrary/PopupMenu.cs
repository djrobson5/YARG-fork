using System.Linq;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using YARG.Core;
using YARG.Core.Extensions;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Song;
using YARG.Helpers;
using YARG.Helpers.Extensions;
using YARG.Localization;
using YARG.Menu.Data;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Player;
using YARG.Playlists;
using YARG.Settings;
using YARG.Song;

namespace YARG.Menu.MusicLibrary
{
    public class PopupMenu : MonoBehaviour
    {
        private enum State
        {
            Main,
            SortSelect,
            GoToSection,
            AddToPlaylist,
        }

        [SerializeField]
        private PopupMenuItem _menuItemPrefab;

        [Space]
        [SerializeField]
        private GameObject _header;
        [SerializeField]
        private TextMeshProUGUI _headerText;
        [SerializeField]
        private MusicLibraryMenu _musicLibrary;
        [SerializeField]
        private Transform _container;
        [SerializeField]
        private NavigationGroup _navGroup;

        private ScrollRect _scrollRect;

        private State _menuState;
        private Playlist _playlistToAdd;
        private bool _openedAddToPlaylistDirectly;

        public void OpenAddToPlaylist(Playlist playlist)
        {
            _playlistToAdd = playlist;
            gameObject.SetActive(true);
            _openedAddToPlaylistDirectly = true;
            _menuState = State.AddToPlaylist;
            UpdateForState();
        }

        private void Awake()
        {
            _scrollRect = _navGroup.GetComponent<ScrollRect>();
        }

        private void OnEnable()
        {
            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", () =>
                {
                    if (_menuState == State.Main || _openedAddToPlaylistDirectly)
                    {
                        gameObject.SetActive(false);
                    }
                    else
                    {
                        _menuState = State.Main;
                        UpdateForState();
                    }
                })
            }, false));

            _menuState = State.Main;
            UpdateForState();
        }

        private void OnDisable()
        {
            Navigator.Instance.PopScheme();
            _musicLibrary.RefreshNavigationSchemeAfterPopup();
            _playlistToAdd = null;
            _openedAddToPlaylistDirectly = false;
        }

        private void UpdateForState()
        {
            // Reset content
            _navGroup.ClearNavigatables();
            ClearItems();

            // Create the menu
            switch (_menuState)
            {
                case State.Main:
                    CreateMainMenu();
                    break;
                case State.SortSelect:
                    CreateSortSelect();
                    break;
                case State.GoToSection:
                    CreateGoToSection();
                    break;
                case State.AddToPlaylist:
                    CreateAddToPlayList();
                    break;
            }

            ResetScroll();
            _navGroup.SelectFirst();
        }

        private void ResetScroll()
        {
            if (_scrollRect == null) return;

            Canvas.ForceUpdateCanvases();

            if (_container is RectTransform containerTransform)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(containerTransform);
                containerTransform.anchoredPosition = containerTransform.anchoredPosition.WithY(0f);
            }

            _scrollRect.verticalNormalizedPosition = 1f;
        }

        private void ClearItems()
        {
            for (int i = _container.childCount - 1; i >= 0; i--)
            {
                var child = _container.GetChild(i);
                child.SetParent(null, false);
                Destroy(child.gameObject);
            }
        }

        private void CreateMainMenu()
        {
            SetHeader(null);

            if (_musicLibrary.MenuState != MenuState.PlaylistSelect)
            {
                CreateItem("RandomSong", () =>
                {
                    _musicLibrary.SelectRandomSong();
                    gameObject.SetActive(false);
                });
            }

            CreateItem("BackToTop", () =>
            {
                _musicLibrary.SelectedIndex = 0;
                gameObject.SetActive(false);
            });

            CreateItem("ScanSongs", () =>
            {
                _musicLibrary.RefreshSongs();
                gameObject.SetActive(false);
            });

            if (_musicLibrary.MenuState != MenuState.PlaylistSelect)
            {
                CreateItem("SortBy", () =>
                {
                    _menuState = State.SortSelect;
                    UpdateForState();
                });
            }

            if (_musicLibrary.MenuState != MenuState.Playlist &&
                _musicLibrary.MenuState != MenuState.Show &&
                _musicLibrary.MenuState != MenuState.PlaylistSelect &&
                _musicLibrary.HasSortHeaders)
            {
                CreateItem("GoToSection", () =>
                {
                    _menuState = State.GoToSection;
                    UpdateForState();
                });
            }

            if (_musicLibrary.MenuState == MenuState.Library && !_musicLibrary.PlaylistMode)
            {
                _musicLibrary.GetSortHeaderCollapseState(out bool hasCollapsed, out bool hasExpanded);

                if (hasCollapsed)
                {
                    CreateItem("ExpandAll", () =>
                    {
                        _musicLibrary.ExpandAll();
                        gameObject.SetActive(false);
                    });
                }

                if (hasExpanded)
                {
                    CreateItem("CollapseAll", () =>
                    {
                        _musicLibrary.CollapseAll();
                        gameObject.SetActive(false);
                    });
                }
            }

            var viewType = _musicLibrary.CurrentSelection;

            // Add/remove to favorites
            var favoriteInfo = viewType.GetFavoriteInfo();
            if (favoriteInfo.ShowFavoriteButton)
            {
                if (!favoriteInfo.IsFavorited)
                {
                    CreateItem("AddToFavorites", () =>
                    {
                        viewType.FavoriteClick();
                        _musicLibrary.RefreshViewsObjects();

                        gameObject.SetActive(false);
                    });
                }
                else
                {
                    CreateItem("RemoveFromFavorites", () =>
                    {
                        viewType.FavoriteClick();
                        _musicLibrary.RefreshViewsObjects();

                        gameObject.SetActive(false);
                    });
                }

                if (viewType is SongViewType)
                {
                    CreateItemUnlocalized(_musicLibrary.GetGreenHoldActionLabel(), () =>
                    {
                        _musicLibrary.ExecuteGreenHoldAction();
                        gameObject.SetActive(false);
                    });

                    bool isInPlaylist = _musicLibrary.MenuState == MenuState.Playlist &&
                        _musicLibrary.SelectedPlaylist != null &&
                        _musicLibrary.SelectedPlaylist != PlaylistContainer.FavoritesPlaylist;
                    bool isInShow = _musicLibrary.MenuState == MenuState.Show;
                    var removalPlaylist = isInShow
                        ? _musicLibrary.ShowPlaylist
                        : _musicLibrary.SelectedPlaylist;
                    bool isSetlist = (isInPlaylist || isInShow) && removalPlaylist.Ephemeral;

                    void AddRemoveFromPlaylistItem()
                    {
                        var removeKey = removalPlaylist.Ephemeral
                            ? "RemoveFromSetlist"
                            : "RemoveFromPlaylist";
                        CreateItem(removeKey, () =>
                        {
                            var songView = viewType as SongViewType;
                            removalPlaylist.RemoveSong(songView.SongEntry);
                            _musicLibrary.RefreshAndReselect();
                            gameObject.SetActive(false);
                            ToastManager.ToastSuccess("Removed from playlist");
                        });
                    }

                    if (isSetlist)
                        AddRemoveFromPlaylistItem();

                    // Show "Add to Playlist" even when editing a playlist.
                    CreateItem("AddToPlaylist", () =>
                    {
                        _menuState = State.AddToPlaylist;
                        UpdateForState();
                    });

                    // Show "Remove from Playlist" if we're editing a playlist
                    if (isInPlaylist && !isSetlist)
                        AddRemoveFromPlaylistItem();
                }
            }

            if (viewType is PlaylistViewType addablePlaylistView &&
                !addablePlaylistView.Playlist.Ephemeral)
            {
                CreateItem("AddPlaylistToSetlist", () =>
                {
                    _musicLibrary.AddPlaylistToSetlist(addablePlaylistView.Playlist);
                    gameObject.SetActive(false);
                });
            }

            if (viewType is PlaylistViewType playlistView &&
                playlistView.Playlist != PlaylistContainer.FavoritesPlaylist)
            {
                if (!playlistView.Playlist.Ephemeral)
                {
                    CreateItem("RenamePlaylist", () =>
                    {
                        DialogManager.Instance.ShowRenameDialog(playlistView.Playlist.Name, newName =>
                        {
                            PlaylistContainer.RenamePlaylist(playlistView.Playlist, newName);
                            ToastManager.ToastSuccess($"Renamed to '{newName}'");
                            _musicLibrary.RefreshAndSelectPlaylist(playlistView.Playlist);
                        });

                        CloseAfterDialog().Forget();
                    });
                }

                var deleteLabel = playlistView.Playlist.Ephemeral
                    ? Localize.Key("Menu.MusicLibrary.Popup.Item.DeleteSetlist")
                    : Localize.Key("Menu.MusicLibrary.Popup.Item.DeletePlaylist");
                CreateItemUnlocalized(deleteLabel, () =>
                {
                    // Special handling for the ad hoc setlist
                    if (playlistView.Playlist.Ephemeral)
                    {
                        playlistView.Playlist.Clear();
                    }
                    else
                    {
                        PlaylistContainer.DeletePlaylist(playlistView.Playlist);
                    }
                    
                    ToastManager.ToastSuccess($"Deleted '{playlistView.Playlist.Name}'");

                    _musicLibrary.RefreshAndReselect();
                    gameObject.SetActive(false);
                    // Annoyingly, this has to be done after the popup menu is made inactive, requring duplicate if statements
                    if (playlistView.Playlist.Ephemeral)
                    {
                        _musicLibrary.SetNavigationScheme(true);
                    }
                });
            }

            // Only show these options if we are selecting a song
            if (viewType is SongViewType songViewType &&
                SettingsManager.Settings.ShowAdvancedMusicLibraryOptions.Value)
            {
                var song = songViewType.SongEntry;

                CreateItem("ViewSongFolder", () =>
                {
                    switch (song.SubType)
                    {
                        case EntryType.Ini:
                        case EntryType.ExCON:
                            FileExplorerHelper.OpenFolder(song.ActualLocation);
                            break;
                        case EntryType.Sng:
                        case EntryType.CON:
                            FileExplorerHelper.OpenToFile(song.ActualLocation);
                            break;
                    }
                    gameObject.SetActive(false);
                });

                CreateItem("CopySongChecksum", () =>
                {
                    GUIUtility.systemCopyBuffer = song.Hash.ToString();

                    gameObject.SetActive(false);
                });

                // Last in the menu on purpose: it is the only destructive entry here,
                // so it should not sit where the cursor comes to rest.
                if (song.SubType == EntryType.CON)
                {
                    // A packed CON's "location" is the whole pack file, often dozens of songs.
                    // The item stays visible so its absence isn't a mystery, but it can only explain itself.
                    CreateItem("DeleteSong", () =>
                    {
                        DialogManager.Instance.ShowMessage(
                            Localize.Key("Menu.Dialog.DeleteSong.PackedCon.Title"),
                            Localize.Key("Menu.Dialog.DeleteSong.PackedCon.Description"));

                        CloseAfterDialog().Forget();
                    }, MenuData.Colors.DeactivatedText);
                }
                else
                {
                    CreateItem("DeleteSong", () => DeleteSong(song).Forget());
                }
            }
        }

        /// <summary>
        /// Neutralizes TMP rich text in a value that came from song metadata or the file system,
        /// so a song called <c>&lt;b&gt;</c> shows up as its own name instead of turning the rest
        /// of the dialog bold.
        /// </summary>
        private static string EscapeRichText(string value)
        {
            return value?.Replace("<", "<noparse><</noparse>");
        }

        private async UniTaskVoid DeleteSong(SongEntry song)
        {
            string path = song.ActualLocation;
            string name = song.Name;

            // Refuse anything outside the library before asking the user to confirm anything.
            // A song's location comes from scan data, which a hand-edited songs.dta can point
            // anywhere, and one of them is "the song folder itself".
            switch (FileDeleteHelper.CheckSongPath(path))
            {
                case SongPathSafety.IsLibraryRoot:
                    DialogManager.Instance.ShowMessage(
                        Localize.Key("Menu.Dialog.DeleteSong.LibraryRoot.Title"),
                        Localize.KeyFormat("Menu.Dialog.DeleteSong.LibraryRoot.Description",
                            EscapeRichText(path)));

                    CloseAfterDialog().Forget();
                    return;

                case SongPathSafety.OutsideLibrary:
                    YargLogger.LogFormatWarning<string>(
                        "Refusing to delete `{0}`: it is not inside any configured song folder.",
                        path);

                    gameObject.SetActive(false);
                    _musicLibrary.SetNavigationScheme(true);
                    ToastManager.ToastError(Localize.KeyFormat(
                        "Menu.Dialog.DeleteSong.Failed", EscapeRichText(name)));
                    return;
            }

            using var messageBuilder = ZString.CreateStringBuilder();
            messageBuilder.Append(Localize.Key("Menu.Dialog.DeleteSong",
                FileDeleteHelper.SupportsTrash ? "Trash" : "Permanent"));

            if (song.SubType == EntryType.ExCON)
            {
                messageBuilder.Append(Localize.Key("Menu.Dialog.DeleteSong.ExConWarning"));
            }

            messageBuilder.Append(Localize.KeyFormat("Menu.Dialog.DeleteSong.Path", EscapeRichText(path)));

            bool delete = false;
            // The confirm text is compared against what the user types, so it has to stay raw.
            var dialog = DialogManager.Instance.ShowConfirmDeleteDialog(
                messageBuilder.ToString(), () => delete = true, name);

            await dialog.WaitUntilClosed();

            if (this == null) return;

            // Close the popup the same way CloseAfterDialog does
            gameObject.SetActive(false);
            _musicLibrary.SetNavigationScheme(true);

            if (!delete) return;

            // The preview holds the song's audio files open; deleting under it fails on Windows
            await _musicLibrary.StopPreviewForFileOperationAsync();

            if (this == null) return;

            if (!FileDeleteHelper.SendToTrashOrDelete(path, out bool trashed))
            {
                ToastManager.ToastError(Localize.KeyFormat(
                    "Menu.Dialog.DeleteSong.Failed", EscapeRichText(name)));
                return;
            }

            if (ReferenceEquals(GlobalVariables.State.CurrentSong, song))
            {
                GlobalVariables.State.CurrentSong = null;
            }

            ToastManager.ToastSuccess(Localize.KeyFormat(
                ("Menu.Dialog.DeleteSong", trashed ? "Trashed" : "Deleted"), EscapeRichText(name)));

            // Slices 1-3 reconcile the song cache the blunt way. Slice 4 replaces this
            // with in-memory removal plus a dirty flag that defers the scan to next launch.
            _musicLibrary.RefreshSongs();
        }

        private void CreateSortSelect()
        {
            SetLocalizedHeader("SortBy");

            if (_musicLibrary.MenuState == MenuState.Playlist ||
                _musicLibrary.MenuState == MenuState.Show)
            {
                CreateItemUnlocalized($"{SortAttribute.Name.ToLocalizedName()} (A-Z)", () =>
                {
                    _musicLibrary.ApplySortFromPopup(SortAttribute.Name, ascending: true);
                    gameObject.SetActive(false);
                });

                CreateItemUnlocalized($"{SortAttribute.Name.ToLocalizedName()} (Z-A)", () =>
                {
                    _musicLibrary.ApplySortFromPopup(SortAttribute.Name, ascending: false);
                    gameObject.SetActive(false);
                });

                CreateItemUnlocalized($"{SortAttribute.Artist.ToLocalizedName()} (A-Z)", () =>
                {
                    _musicLibrary.ApplySortFromPopup(SortAttribute.Artist, ascending: true);
                    gameObject.SetActive(false);
                });

                CreateItemUnlocalized($"{SortAttribute.Artist.ToLocalizedName()} (Z-A)", () =>
                {
                    _musicLibrary.ApplySortFromPopup(SortAttribute.Artist, ascending: false);
                    gameObject.SetActive(false);
                });

                return;
            }

            foreach (var sort in EnumExtensions<SortAttribute>.Values)
            {
                // Skip theses because they don't make sense
                if (sort == SortAttribute.Unspecified)
                    continue;

                if (sort == SortAttribute.Playable)
                    continue;

                // Skip Play count if there are no real players
                if (sort == SortAttribute.Playcount && PlayerContainer.OnlyHasBotsActive())
                    continue;

                if (sort >= SortAttribute.Instrument)
                    break;

                CreateItemUnlocalized(sort.ToLocalizedName(), () =>
                {
                    _musicLibrary.ApplySortFromPopup(sort);
                    gameObject.SetActive(false);
                });
            }

            foreach (var instrument in EnumExtensions<Instrument>.Values)
            {
                if (SongContainer.HasInstrument(instrument))
                {
                    var attribute = instrument.ToSortAttribute();
                    CreateItemUnlocalized(attribute.ToLocalizedName(), () =>
                    {
                        _musicLibrary.ChangeSort(attribute);
                        gameObject.SetActive(false);
                    });
                }

                if (instrument == Instrument.EliteDrums && MidiDrumkitHelper.Instruments.Any(SongContainer.HasInstrument))
                {
                    CreateItemUnlocalized(SortAttribute.AggregateDrums.ToLocalizedName(), () =>
                    {
                        _musicLibrary.ChangeSort(SortAttribute.AggregateDrums);
                        gameObject.SetActive(false);
                    });
                }
            }
        }

        private void CreateGoToSection()
        {
            SetLocalizedHeader("GoToSection");

            foreach (var (name, index) in _musicLibrary.Shortcuts)
            {
                CreateItemUnlocalized(name, () =>
                {
                    _musicLibrary.SelectedIndex = index;
                    gameObject.SetActive(false);
                });
            }
        }

        private void CreateAddToPlayList()
        {
            SetLocalizedHeader("AddToPlaylist");

            // Get the list of playlists from PlaylistContainer and create items for each
            foreach (var playlist in PlaylistContainer.Playlists)
            {
                var sourcePlaylist = _playlistToAdd ??
                    (_musicLibrary.CurrentSelection as PlaylistViewType)?.Playlist;
                if (ReferenceEquals(playlist, sourcePlaylist))
                {
                    continue;
                }

                CreateItemUnlocalized(playlist.Name, () =>
                {
                    if (_musicLibrary.CurrentSelection is SongViewType songView)
                    {
                        var song = songView.SongEntry;
                        var artist = song.Artist;
                        var title = song.Name;
                        // Add the song to the playlist
                        _musicLibrary.CurrentSelection.AddToPlaylist(playlist);
                        gameObject.SetActive(false);
                        ToastManager.ToastSuccess($"Added {artist} - {title} to {playlist.Name}");
                    }
                    else if (sourcePlaylist != null)
                    {
                        var songs = sourcePlaylist.ToList();
                        foreach (var song in songs)
                        {
                            playlist.AddSong(song);
                        }

                        gameObject.SetActive(false);
                        ToastManager.ToastSuccess($"Added {songs.Count} songs to {playlist.Name}");
                        _musicLibrary.RefreshAndReselect();
                    }
                });
            }

            // Add option to create new playlist
            CreateItem("CreateNewPlaylist", () =>
            {

                // TODO: Localize all these strings

                // Show text entry dialog
                DialogManager.Instance.ShowRenameDialog("New Playlist Name", value =>
                {
                    value = value.Trim();

                    bool nameAlreadyExists = string.Equals(
                            PlaylistContainer.FavoritesPlaylist.Name,
                            value,
                            System.StringComparison.OrdinalIgnoreCase) ||
                        PlaylistContainer.Playlists.Any(playlist => string.Equals(
                            playlist.Name,
                            value,
                            System.StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(
                            Localize.Key("Menu.MusicLibrary.CurrentSetlist"),
                            value,
                            System.StringComparison.OrdinalIgnoreCase);

                    if (string.IsNullOrEmpty(value) || nameAlreadyExists)
                    {
                        ToastManager.ToastError("A playlist with that name already exists");
                        CloseAfterDialog().Forget();
                        return;
                    }

                    // Create the playlist
                    var playlist = PlaylistContainer.CreatePlaylist(value);
                    // Add selected song to new playlist
                    if (_musicLibrary.CurrentSelection is SongViewType songView)
                    {
                        songView.AddToPlaylist(playlist);
                    }
                    else
                    {
                        var sourcePlaylist = _playlistToAdd ??
                            (_musicLibrary.CurrentSelection as PlaylistViewType)?.Playlist;
                        if (sourcePlaylist == null)
                        {
                            ToastManager.ToastError("You can't add that to a playlist");
                            PlaylistContainer.DeletePlaylist(playlist);
                            CloseAfterDialog().Forget();
                            return;
                        }

                        foreach (var song in sourcePlaylist.ToList())
                        {
                            playlist.AddSong(song);
                        }
                    }

                    // Close the popup after the rename dialog is fully closed
                    CloseAfterDialog().Forget();
                    _musicLibrary.RefreshAndReselect();
                    ToastManager.ToastSuccess("Playlist Created");
                });
            });
        }

        private void SetLocalizedHeader(string localizeKey)
        {
            SetHeader(Localize.Key("Menu.MusicLibrary.Popup.Header", localizeKey));
        }

        private void SetHeader(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                _header.SetActive(false);
            }
            else
            {
                _header.SetActive(true);
                _headerText.text = text;
            }
        }

        private void CreateItem(string localizeKey, UnityAction a)
        {
            var localized = Localize.Key("Menu.MusicLibrary.Popup.Item", localizeKey);
            CreateItemUnlocalized(localized, a);
        }

        private void CreateItem(string localizeKey, UnityAction a, Color textColor)
        {
            var localized = Localize.Key("Menu.MusicLibrary.Popup.Item", localizeKey);
            CreateItemUnlocalized(localized, a, textColor);
        }

        private void CreateItem(string localizeKey, string formatArg, UnityAction a)
        {
            var localized = Localize.KeyFormat(("Menu.MusicLibrary.Popup.Item", localizeKey), formatArg);
            CreateItemUnlocalized(localized, a);
        }

        private async UniTaskVoid CloseAfterDialog()
        {
            await DialogManager.Instance.WaitUntilCurrentClosed();
            if (this == null) return;
            gameObject.SetActive(false);
            _musicLibrary.SetNavigationScheme(true);
        }

        private void CreateItemUnlocalized(string body, UnityAction a)
        {
            var btn = Instantiate(_menuItemPrefab, _container);
            btn.Initialize(body, a);
            _navGroup.AddNavigatable(btn.Button);
        }

        private void CreateItemUnlocalized(string body, UnityAction a, Color textColor)
        {
            var btn = Instantiate(_menuItemPrefab, _container);
            btn.Initialize(body, a, textColor);
            _navGroup.AddNavigatable(btn.Button);
        }
    }
}
