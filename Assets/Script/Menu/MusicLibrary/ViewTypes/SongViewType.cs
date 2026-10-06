using System.Linq;
using Cysharp.Text;
using UnityEngine;
using YARG.Core.Game;
using YARG.Core.Song;
using YARG.Helpers;
using YARG.Player;
using YARG.Playlists;
using YARG.Scores;
using YARG.Settings;
using YARG.Song;

namespace YARG.Menu.MusicLibrary
{
    public enum HighScoreInfoMode
    {
        Stars,
        Score,
        Off
    }

    public class SongViewType : ViewType
    {
        public override BackgroundType Background => BackgroundType.Normal;

        public override bool UseAsMadeFamousBy => !SongEntry.IsMaster;

        public readonly SongEntry SongEntry;
        public override string StableId => _stableId;
        public string ContentStableId => _contentStableId;

        private readonly MusicLibraryMenu _musicLibrary;
        private readonly string _stableId;
        private readonly string _contentStableId;

        private bool _fetchedScores;
        private PlayerScoreRecord _playerScoreRecord;
        private GameRecord _bandScoreRecord;
        private ScoreContext _fetchedScoreContext;
        private SectionProgress? _sectionProgress;

        public SongViewType(MusicLibraryMenu musicLibrary, SongEntry songEntry, string context = "library")
        {
            _musicLibrary = musicLibrary;
            SongEntry = songEntry;
            _contentStableId = $"Song:{SongEntry.Hash}_{SongEntry.ActualLocation}";
            _stableId = $"Song:{context}:{_contentStableId}";
        }

        public override string GetPrimaryText(bool selected)
        {
            return FormatAs(SongEntry.Name, TextType.Primary, selected);
        }

        public override string GetSecondaryText(bool selected)
        {
            return FormatAs(SongEntry.Artist, TextType.Secondary, selected);
        }

#nullable enable
        public override Sprite? GetIcon()
#nullable disable
        {
            return SongSources.SourceToIcon(SongEntry.Source);
        }

        public override string GetSideText(bool selected)
        {
            FetchHighScores();

            using var builder = ZString.CreateStringBuilder();

            // If non-null, band score is being requested
            if (_bandScoreRecord is not null)
            {
                builder.AppendFormat("{0:N0}", _bandScoreRecord.BandScore);
                return builder.ToString();
            }

            // Never played!
            if (_playerScoreRecord is null)
            {
                return string.Empty;
            }

            var scoreColor = _playerScoreRecord.IsFc ? "#ffd029" : "#ffffff";
            builder.AppendFormat("<mspace=.5em><color={1}>{0:N0}</color></mspace>",
                _playerScoreRecord.Score, scoreColor);
            return builder.ToString();
        }

        public override ScoreInfo? GetScoreInfo()
        {
            FetchHighScores();

            // Never played!
            if (_playerScoreRecord is null)
            {
                return null;
            }

            return new ScoreInfo
            {
                Score = _playerScoreRecord.Score,
                Difficulty = _playerScoreRecord.Difficulty,
                Percent = _playerScoreRecord.GetPercent(),
                Instrument = _playerScoreRecord.Instrument,
                IsFc = _playerScoreRecord.IsFc,
                Sections = _sectionProgress
            };
        }

        public override StarAmount? GetStarAmount()
        {
            FetchHighScores();

            return GetStarAmount(_playerScoreRecord, _bandScoreRecord);
        }

        public static StarAmount? GetStarAmountForSong(SongEntry songEntry)
        {
            FetchHighScores(songEntry, out var playerScoreRecord, out var bandScoreRecord);

            return GetStarAmount(playerScoreRecord, bandScoreRecord);
        }

#nullable enable
        private static StarAmount? GetStarAmount(
            PlayerScoreRecord? playerScoreRecord,
            GameRecord? bandScoreRecord)
#nullable disable
        {
            if (bandScoreRecord is not null)
            {
                return bandScoreRecord.BandStars;
            }

            return playerScoreRecord?.Stars;
        }

        public override FavoriteInfo GetFavoriteInfo()
        {
            return new FavoriteInfo
            {
                ShowFavoriteButton = true,
                IsFavorited = PlaylistContainer.FavoritesPlaylist.ContainsSong(SongEntry)
            };
        }

        public override void SecondaryTextClick()
        {
            base.SecondaryTextClick();
           _musicLibrary.SetSearchInput(SortAttribute.Artist, $"\"{SongEntry.Artist.SearchStr}\"");
        }

        public override void PrimaryButtonClick()
        {
            base.PrimaryButtonClick();

            if (PlayerContainer.Players.Count <= 0)
            {
                return;
            }

            // Reset library's main index so we don't return to the index set by play a show
            MusicLibraryMenu.ResetMainLibraryIndex();
            MusicLibraryMenu.SetReload(MusicLibraryReloadState.Partial);

            GlobalVariables.State.CurrentSong = SongEntry;
            // This just makes stuff in DifficultySelectMenu easier
            GlobalVariables.State.ShowSongs.Clear();
            GlobalVariables.State.ShowSongs.Add(SongEntry);
            GlobalVariables.State.PlayingAShow = false;

            MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
        }

        public override void IconClick()
        {
           _musicLibrary.SetSearchInput(SortAttribute.Source, $"\"{SongEntry.Source.SearchStr}\"");
        }

        public override void FavoriteClick()
        {
            base.FavoriteClick();

            var info = GetFavoriteInfo();

            if (!info.IsFavorited)
            {
                PlaylistContainer.FavoritesPlaylist.AddSong(SongEntry);
            }
            else
            {
                PlaylistContainer.FavoritesPlaylist.RemoveSong(SongEntry);

                // Refresh the view to update the filter results
                _musicLibrary.RefreshAndReselect();
            }

            _musicLibrary.RefreshSidebar();
        }

        public override void AddToPlaylist(Playlist playlist)
        {
            playlist.AddSong(SongEntry);
        }

        public override void RemoveFromPlaylist(Playlist playlist)
        {
            playlist.RemoveSong(SongEntry);

            // Refresh the view to update the filter results
            _musicLibrary.RefreshAndReselect();
        }

        private void FetchHighScores()
        {
            var context = ScoreContext.Capture();
            if (_fetchedScores && _fetchedScoreContext.Equals(context))
            {
                return;
            }

            FetchHighScores(SongEntry, out _playerScoreRecord, out _bandScoreRecord);
            _sectionProgress = FetchSectionProgress(SongEntry, _playerScoreRecord);
            _fetchedScoreContext = context;
            _fetchedScores = true;
        }

        /// <summary>
        /// Gets the cumulative section progress that goes with the high score being displayed.
        /// </summary>
        /// <remarks>
        /// The difficulty comes from the high score record rather than from the profile, so that
        /// the percent and the fraction always describe the same chart. There is no player score
        /// record with two or more humans, which is also when the row shows no pill, so the band
        /// case falls out as <c>null</c> on its own.
        /// </remarks>
        private static SectionProgress? FetchSectionProgress(SongEntry songEntry,
            PlayerScoreRecord playerScoreRecord)
        {
            // Slice 5 master switch. Progress earned earlier stays in the database but is not
            // shown, so the feature really is invisible everywhere while it is off. Toggling it
            // queues a partial library reload, so no already-fetched view keeps a stale fraction.
            if (!SettingsManager.Settings.TrackSectionCompletion.Value)
            {
                return null;
            }

            if (playerScoreRecord is null)
            {
                return null;
            }

            var player = PlayerContainer.Players.FirstOrDefault(p => !p.Profile.IsBot);
            if (player is null)
            {
                return null;
            }

            return ScoreContainer.GetSectionProgress(songEntry.Hash, player.Profile.Id,
                playerScoreRecord.Instrument, playerScoreRecord.Difficulty, player.Profile.HarmonyIndex);
        }

        private static void FetchHighScores(SongEntry songEntry, out PlayerScoreRecord playerScoreRecord, out GameRecord bandScoreRecord)
        {
            ScoreContainer.GetPreferredHighScoresForCurrentPlayers(
                songEntry.Hash, out playerScoreRecord, out bandScoreRecord);
        }
    }
}
