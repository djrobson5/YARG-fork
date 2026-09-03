using System;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using YARG.Core.Logging;

namespace YARG.Song
{
    /// <summary>
    /// Checks the fork's GitHub Releases for a newer <c>-sectionfc</c> build.
    /// </summary>
    /// <remarks>
    /// Check only. Nothing here downloads or writes anything; see
    /// <c>docs/updater-design.md</c> for the full slice plan.
    /// </remarks>
    public static class UpdateChecker
    {
        public enum UpdateStatus
        {
            /// <summary>The installed tag is the newest release tag.</summary>
            UpToDate,

            /// <summary>A newer release exists.</summary>
            UpdateAvailable,

            /// <summary>GitHub answered, but nothing on the repo matched the fork's tag pattern.</summary>
            NoReleases,

            /// <summary>GitHub returned 403 or 429; the unauthenticated 60/hour limit was hit.</summary>
            RateLimited,

            /// <summary>Anything else went wrong (offline, timeout, malformed response).</summary>
            Failed,
        }

        public readonly struct UpdateCheckResult
        {
            public readonly UpdateStatus Status;

            /// <summary>The running build's release tag. Never null.</summary>
            public readonly string InstalledTag;

            /// <summary>The newest release tag found, or null if the check failed.</summary>
            public readonly string LatestTag;

            /// <summary>The newest release's GitHub page, or null if the check failed.</summary>
            public readonly string ReleaseUrl;

            public UpdateCheckResult(UpdateStatus status, string installedTag, string latestTag, string releaseUrl)
            {
                Status = status;
                InstalledTag = installedTag;
                LatestTag = latestTag;
                ReleaseUrl = releaseUrl;
            }
        }

        private const string RELEASES_URL = "https://api.github.com/repos/djrobson5/YARG-fork/releases";

        private const int REQUEST_TIMEOUT_SECONDS = 10;

        /// <summary>
        /// Matches the fork's release tags and captures the build number that orders them.
        /// Lexical comparison is wrong here: "sectionfc.10" sorts before "sectionfc.9".
        /// </summary>
        private static readonly Regex TagPattern =
            new(@"-sectionfc\.(\d+)$", RegexOptions.CultureInvariant);

        // The check is never retried, and a real answer is reused for the rest of the session.
        private static UpdateCheckResult? _cachedResult;

        // A request already in the air. A second button press joins it instead of opening a
        // second connection and racing to show a second dialog.
        private static UniTask<UpdateCheckResult>? _inFlight;

        /// <summary>
        /// Whether this build is a CI release build, and so has a tag worth comparing.
        /// In the editor and in local builds <see cref="Application.version"/> is the
        /// project's bundle version, which matches no release.
        /// </summary>
        public static bool IsReleaseBuild => TagPattern.IsMatch(Application.version);

        public static UniTask<UpdateCheckResult> CheckForUpdate()
        {
            if (_cachedResult.HasValue)
            {
                return UniTask.FromResult(_cachedResult.Value);
            }

            if (_inFlight.HasValue)
            {
                if (_inFlight.Value.Status == UniTaskStatus.Pending)
                {
                    return _inFlight.Value;
                }

                _inFlight = null;
            }

            // Preserve() so that more than one caller can await the same request.
            var task = FetchAndCache().Preserve();
            _inFlight = task;
            return task;
        }

        private static async UniTask<UpdateCheckResult> FetchAndCache()
        {
            var result = await Fetch();

            // Only cache answers GitHub actually gave us. A dropped connection, or a rate
            // limit whose window resets within the hour, should not stick for the session.
            if (result.Status != UpdateStatus.Failed && result.Status != UpdateStatus.RateLimited)
            {
                _cachedResult = result;
            }

            return result;
        }

        private static async UniTask<UpdateCheckResult> Fetch()
        {
            string installedTag = Application.version;

            string latestTag = null;
            string releaseUrl = null;
            long highestBuild = -1;

            try
            {
                using var request = UnityWebRequest.Get(RELEASES_URL);
                request.SetRequestHeader("User-Agent", "YARG");
                request.timeout = REQUEST_TIMEOUT_SECONDS;

                await request.SendWebRequest();

                var releases = JArray.Parse(request.downloadHandler.text);

                foreach (var release in releases)
                {
                    string tag = release["tag_name"]?.ToString();
                    if (string.IsNullOrEmpty(tag))
                    {
                        continue;
                    }

                    var match = TagPattern.Match(tag);
                    if (!match.Success ||
                        !long.TryParse(match.Groups[1].Value, out long build) ||
                        build <= highestBuild)
                    {
                        continue;
                    }

                    highestBuild = build;
                    latestTag = tag;
                    releaseUrl = release["html_url"]?.ToString();
                }
            }
            catch (UnityWebRequestException e)
            {
                // UniTask's awaiter throws on protocol, connection and data-processing errors,
                // so the request's own result and response code are never worth checking after
                // the await. Everything needed is snapshotted on the exception.
                if (e.ResponseCode is 403 or 429)
                {
                    YargLogger.LogFormatWarning("Update check was rate limited by GitHub: {0}", e.Error);
                    return new UpdateCheckResult(UpdateStatus.RateLimited, installedTag, null, null);
                }

                YargLogger.LogFormatWarning("Update check failed: {0}", $"HTTP {e.ResponseCode}, {e.Error}");
                return new UpdateCheckResult(UpdateStatus.Failed, installedTag, null, null);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to check for updates.");
                return new UpdateCheckResult(UpdateStatus.Failed, installedTag, null, null);
            }

            if (latestTag == null)
            {
                // GitHub answered fine; the repo simply has nothing tagged for this fork.
                YargLogger.LogWarning("Update check found no releases matching the fork's tag pattern.");
                return new UpdateCheckResult(UpdateStatus.NoReleases, installedTag, null, null);
            }

            // Compare by build number, not string equality, so a build newer than anything
            // published (a local CI run, say) does not read as "an update is available".
            var installedMatch = TagPattern.Match(installedTag);
            long installedBuild = installedMatch.Success && long.TryParse(installedMatch.Groups[1].Value, out long b)
                ? b
                : -1;

            var status = highestBuild > installedBuild ? UpdateStatus.UpdateAvailable : UpdateStatus.UpToDate;
            return new UpdateCheckResult(status, installedTag, latestTag, releaseUrl);
        }
    }
}
