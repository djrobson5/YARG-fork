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
    /// Nothing here downloads or writes anything — it only reports what the newest release is
    /// and where its Windows asset lives. <see cref="UpdateDownloader"/> does the fetching.
    /// See <c>docs/updater-design.md</c> for the full slice plan.
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

            /// <summary>
            /// The Windows .zip asset's file name, or null when there is nothing downloadable
            /// (a failed check, or a platform this updater cannot install on).
            /// </summary>
            public readonly string AssetName;

            /// <summary>The asset's direct download URL, or null. Redirects to a CDN host.</summary>
            public readonly string AssetUrl;

            /// <summary>
            /// The asset's declared byte count. The release publishes no checksum, so this is
            /// the only integrity signal there is. Zero when there is no asset.
            /// </summary>
            public readonly long AssetSize;

            /// <summary>Whether this result carries an asset the downloader could fetch.</summary>
            public bool HasDownloadableAsset =>
                !string.IsNullOrEmpty(AssetUrl) && !string.IsNullOrEmpty(AssetName) && AssetSize > 0;

            public UpdateCheckResult(UpdateStatus status, string installedTag, string latestTag, string releaseUrl,
                string assetName = null, string assetUrl = null, long assetSize = 0)
            {
                Status = status;
                InstalledTag = installedTag;
                LatestTag = latestTag;
                ReleaseUrl = releaseUrl;
                AssetName = assetName;
                AssetUrl = assetUrl;
                AssetSize = assetSize;
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

        /// <summary>
        /// The name the release workflow gives the Windows build
        /// (<c>.github/workflows/build-windows.yml</c>, "[Setup] Resolve version").
        /// </summary>
        private static readonly Regex WindowsAssetPattern =
            new(@"^YARG-SectionFC_.*-Windows-x64\.zip$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
            JToken latestRelease = null;

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
                    latestRelease = release;
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

            string assetName = null;
            string assetUrl = null;
            long assetSize = 0;
#if UNITY_STANDALONE_WIN
            // Only Windows builds have something this updater could ever install, so only
            // Windows builds bother reading the asset list. Everywhere else the flow degrades
            // to the Open Release Page button.
            TryFindWindowsAsset(latestRelease, out assetName, out assetUrl, out assetSize);
#else
            // Nothing reads the release body off Windows, and an assigned-but-unread local is
            // a CS0219 warning.
            _ = latestRelease;
#endif

            return new UpdateCheckResult(status, installedTag, latestTag, releaseUrl,
                assetName, assetUrl, assetSize);
        }

        /// <summary>
        /// Picks the Windows .zip out of a release's asset list.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>Get-WindowsAsset</c> in <c>tools/update-yarg.ps1</c>: match the workflow's
        /// naming first, and fall back to a lone .zip so a rename of the workflow's asset does
        /// not silently break the updater.
        /// </remarks>
        private static bool TryFindWindowsAsset(JToken release, out string name, out string url, out long size)
        {
            name = null;
            url = null;
            size = 0;

            if (release?["assets"] is not JArray assets)
            {
                return false;
            }

            JToken chosen = null;
            JToken onlyZip = null;
            int zipCount = 0;

            foreach (var asset in assets)
            {
                string assetName = asset["name"]?.ToString();
                if (string.IsNullOrEmpty(assetName))
                {
                    continue;
                }

                if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipCount++;
                    onlyZip = asset;
                }

                if (chosen == null && WindowsAssetPattern.IsMatch(assetName))
                {
                    chosen = asset;
                }
            }

            chosen ??= zipCount == 1 ? onlyZip : null;
            if (chosen == null)
            {
                YargLogger.LogWarning("The latest release has no Windows .zip asset.");
                return false;
            }

            name = chosen["name"]?.ToString();
            url = chosen["browser_download_url"]?.ToString();
            size = chosen["size"]?.Value<long>() ?? 0;

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url) || size <= 0)
            {
                YargLogger.LogWarning("The latest release's Windows asset is missing a name, URL or size.");
                name = null;
                url = null;
                size = 0;
                return false;
            }

            return true;
        }
    }
}
