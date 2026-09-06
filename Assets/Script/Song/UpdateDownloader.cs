using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Song
{
    /// <summary>
    /// Downloads the release asset <see cref="UpdateChecker"/> found, verifies it, and extracts
    /// it to a staging folder.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the install directory. Staging only; applying a staged build is
    /// slice 4. See <c>docs/updater-design.md</c>. This is the C# port of the download/verify/
    /// stage half of <c>tools/update-yarg.ps1</c>.
    /// </remarks>
    public static class UpdateDownloader
    {
        public enum StageStatus
        {
            /// <summary>The build was downloaded, verified, extracted and looks like a YARG build.</summary>
            Staged,

            /// <summary>The release carried no Windows .zip asset to download.</summary>
            NoAsset,

            /// <summary>The download never completed (offline, timeout, 404 on the asset).</summary>
            DownloadFailed,

            /// <summary>The downloaded file's length does not match the release's declared size.</summary>
            SizeMismatch,

            /// <summary>The .zip's contents could not be read, so the download is corrupt.</summary>
            ExtractFailed,

            /// <summary>
            /// The updates folder could not be written to — out of disk, denied, or something
            /// is holding a handle on the staging tree. Nothing is wrong with the download.
            /// </summary>
            StageIoError,

            /// <summary>The extracted tree is not a YARG build (no YARG.exe or no YARG_Data).</summary>
            InvalidLayout,

            /// <summary>The user closed the progress dialog. Nothing to report.</summary>
            Cancelled,
        }

        public readonly struct UpdateStageResult
        {
            public readonly StageStatus Status;

            /// <summary>Where the staged build lives, or null unless <see cref="Status"/> is Staged.</summary>
            public readonly string StagingPath;

            public UpdateStageResult(StageStatus status, string stagingPath = null)
            {
                Status = status;
                StagingPath = stagingPath;
            }
        }

        /// <summary>
        /// Everything the updater writes lives under here — deliberately *not* the install
        /// directory, so a failed update cannot corrupt a working install.
        /// </summary>
        public static string UpdatesRoot => Path.Combine(PathHelper.PersistentDataPath, "updates");

        public static string StagingRoot => Path.Combine(UpdatesRoot, "staging");

        /// <summary>The staging folder for a given release tag.</summary>
        public static string StagingPathFor(string tag) => Path.Combine(StagingRoot, SanitizeFileName(tag, "unknown"));

        // A download already running. A second press joins it rather than opening a second
        // connection and racing the first one for the same files on disk.
        private static UniTask<UpdateStageResult>? _inFlight;

        /// <summary>Whether a download/stage is already running.</summary>
        public static bool IsDownloading => _inFlight.HasValue && _inFlight.Value.Status == UniTaskStatus.Pending;

        /// <summary>
        /// Downloads the release asset, checks its length against the release metadata, and
        /// extracts it into <c>updates/staging/&lt;tag&gt;</c>.
        /// </summary>
        /// <param name="progress">
        /// Reports download progress in 0..1. It is reported as exactly 1 once the download is
        /// done and extraction begins, which is what the UI uses to switch its wording.
        /// </param>
        /// <remarks>
        /// Only one download runs at a time; a call made while one is in flight joins that one
        /// and ignores its own <paramref name="progress"/> and <paramref name="cancellationToken"/>.
        /// </remarks>
        public static UniTask<UpdateStageResult> DownloadAndStage(
            UpdateChecker.UpdateCheckResult result, IProgress<float> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (_inFlight.HasValue)
            {
                if (_inFlight.Value.Status == UniTaskStatus.Pending)
                {
                    YargLogger.LogWarning("An update download is already running; joining it.");
                    return _inFlight.Value;
                }

                _inFlight = null;
            }

            // Preserve() so that more than one caller can await the same download.
            var task = DownloadAndStageCore(result, progress, cancellationToken).Preserve();
            _inFlight = task;
            return task;
        }

        private static async UniTask<UpdateStageResult> DownloadAndStageCore(
            UpdateChecker.UpdateCheckResult result, IProgress<float> progress,
            CancellationToken cancellationToken)
        {
            if (!result.HasDownloadableAsset)
            {
                YargLogger.LogWarning("Asked to download an update with no downloadable asset.");
                return new UpdateStageResult(StageStatus.NoAsset);
            }

            // The asset name comes off the GitHub response, so it is never trusted as a path
            // component. Anything that is not a plain file name is refused outright.
            string assetName = SanitizeFileName(result.AssetName, null);
            if (assetName == null)
            {
                YargLogger.LogFormatWarning("The release asset name {0} is not usable as a file name.",
                    result.AssetName);
                return new UpdateStageResult(StageStatus.NoAsset);
            }

            string zipPath = Path.Combine(UpdatesRoot, assetName);
            string stagingPath = StagingPathFor(result.LatestTag);

            // Extraction goes to a sibling folder and is renamed into place only once it has
            // been verified, so a failed extract leaves any previous good staging alone.
            string tempPath = stagingPath + ".tmp";

            try
            {
                Directory.CreateDirectory(UpdatesRoot);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Could not create the updates folder at {UpdatesRoot}.");
                return new UpdateStageResult(StageStatus.StageIoError);
            }

            var downloadStatus = await Download(result, zipPath, progress, cancellationToken);
            if (downloadStatus != StageStatus.Staged)
            {
                return new UpdateStageResult(downloadStatus);
            }

            // Extraction of a ~130 MB archive takes seconds and would otherwise freeze the
            // menu, so it runs off the main thread. Nothing it touches is a Unity object.
            progress?.Report(1f);

            // ZipFile.ExtractToDirectory cannot be interrupted, so the token is not handed to
            // it. Instead the main thread polls: on a cancel the extraction is left to finish
            // in the background and its output is thrown away when it does.
            var extraction = UniTask.RunOnThreadPool(() => Extract(zipPath, tempPath)).Preserve();

            while (extraction.Status == UniTaskStatus.Pending)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    YargLogger.LogInfo("The update was cancelled during extraction; discarding the staging tree.");
                    DiscardWhenFinished(extraction, tempPath).Forget();
                    return new UpdateStageResult(StageStatus.Cancelled);
                }

                await UniTask.Yield();
            }

            try
            {
                await extraction;
            }
            catch (InvalidDataException e)
            {
                // Only a bad archive gets the "corrupt, try again" wording, and only a bad
                // archive is worth deleting — a right-sized but broken .zip would otherwise be
                // reused by every later attempt forever.
                YargLogger.LogException(e, $"The archive at {zipPath} could not be read; deleting it.");
                DeleteDirectoryQuietly(tempPath);
                DeleteQuietly(zipPath);
                return new UpdateStageResult(StageStatus.ExtractFailed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Out of disk, denied, or something is holding a handle. The download is fine;
                // deleting it would only mean re-fetching 130 MB for nothing.
                YargLogger.LogException(e, $"Could not write the staging tree at {tempPath}.");
                DeleteDirectoryQuietly(tempPath);
                return new UpdateStageResult(StageStatus.StageIoError);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Failed to extract {zipPath} to {tempPath}.");
                DeleteDirectoryQuietly(tempPath);
                return new UpdateStageResult(StageStatus.ExtractFailed);
            }

            // The workflow zips from inside build/StandaloneWindows64, so YARG.exe and YARG_Data
            // are at the archive root. If that ever changes, catch it here rather than after the
            // apply step has already clobbered an install.
            if (!File.Exists(Path.Combine(tempPath, "YARG.exe")) ||
                !Directory.Exists(Path.Combine(tempPath, "YARG_Data")))
            {
                YargLogger.LogFormatWarning(
                    "The staged build at {0} has no YARG.exe and/or YARG_Data at its root; refusing it.",
                    tempPath);
                DeleteDirectoryQuietly(tempPath);
                return new UpdateStageResult(StageStatus.InvalidLayout);
            }

            // Only now is the previous staging tree for this tag worth losing.
            try
            {
                if (Directory.Exists(stagingPath))
                {
                    Directory.Delete(stagingPath, true);
                }

                Directory.Move(tempPath, stagingPath);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Could not move the staged build into {stagingPath}.");
                DeleteDirectoryQuietly(tempPath);
                return new UpdateStageResult(StageStatus.StageIoError);
            }

            // Explicit type arguments: with two string parameters the call is otherwise ambiguous
            // against the one-argument overload's [CallerMemberName] string.
            YargLogger.LogFormatInfo<string, string>("Staged update {0} at {1}.", result.LatestTag, stagingPath);
            return new UpdateStageResult(StageStatus.Staged, stagingPath);
        }

        /// <summary>
        /// Fetches the asset to <paramref name="zipPath"/> and verifies its length.
        /// Returns <see cref="StageStatus.Staged"/> on success; the file is otherwise deleted.
        /// </summary>
        private static async UniTask<StageStatus> Download(UpdateChecker.UpdateCheckResult result, string zipPath,
            IProgress<float> progress, CancellationToken cancellationToken)
        {
            // A previous run may already have fetched this exact asset. Trust it only if the byte
            // count matches, which is the same check a fresh download gets.
            try
            {
                var existing = new FileInfo(zipPath);
                if (existing.Exists)
                {
                    if (existing.Length == result.AssetSize)
                    {
                        YargLogger.LogFormatInfo("Reusing the already-downloaded {0}; its size matches.",
                            result.AssetName);
                        progress?.Report(1f);
                        return StageStatus.Staged;
                    }

                    YargLogger.LogFormatInfo("Discarding a partial or stale copy of {0}.", result.AssetName);
                    File.Delete(zipPath);
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Could not inspect the existing download at {zipPath}.");
                return StageStatus.DownloadFailed;
            }

            try
            {
                using var request = new UnityWebRequest(result.AssetUrl, UnityWebRequest.kHttpVerbGET);

                // DownloadHandlerFile streams straight to disk. downloadHandler.data would buffer
                // the whole ~130 MB in managed memory first.
                request.downloadHandler = new DownloadHandlerFile(zipPath)
                {
                    removeFileOnAbort = true,
                };
                request.SetRequestHeader("User-Agent", "YARG");

                // No timeout: this is a large file on an unknown connection, and UnityWebRequest's
                // timeout is a wall clock on the whole transfer, not an idle timeout.
                request.timeout = 0;

                await request.SendWebRequest().ToUniTask(progress, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                YargLogger.LogInfo("The update download was cancelled.");
                return StageStatus.Cancelled;
            }
            catch (UnityWebRequestException e)
            {
                // UniTask's awaiter throws on protocol, connection and data-processing errors, so
                // the failure branch lives here rather than in a check of request.result.
                YargLogger.LogFormatWarning("The update download failed: {0}",
                    $"HTTP {e.ResponseCode}, {e.Error}");
                DeleteQuietly(zipPath);
                return StageStatus.DownloadFailed;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "The update download failed.");
                DeleteQuietly(zipPath);
                return StageStatus.DownloadFailed;
            }

            // The release publishes no checksum, so the asset's declared size is the only
            // integrity signal there is. It at least catches a truncated download.
            long actual;
            try
            {
                actual = new FileInfo(zipPath).Length;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Could not measure the downloaded file at {zipPath}.");
                DeleteQuietly(zipPath);
                return StageStatus.DownloadFailed;
            }

            if (actual != result.AssetSize)
            {
                YargLogger.LogFormatWarning(
                    "Downloaded {0} bytes but the release says the asset is {1} bytes; discarding it.",
                    actual, result.AssetSize);
                DeleteQuietly(zipPath);
                return StageStatus.SizeMismatch;
            }

            YargLogger.LogFormatInfo("Downloaded {0} bytes; the size matches the release metadata.", actual);
            return StageStatus.Staged;
        }

        /// <summary>
        /// Clears any leftover temporary tree and extracts the archive into it.
        /// Runs on a thread pool thread; must touch no Unity API.
        /// </summary>
        private static void Extract(string zipPath, string tempPath)
        {
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, true);
            }

            Directory.CreateDirectory(tempPath);
            ZipFile.ExtractToDirectory(zipPath, tempPath);
        }

        /// <summary>
        /// Waits for an extraction that outlived its cancellation and throws its output away.
        /// The files cannot be deleted while the extractor still has them open.
        /// </summary>
        private static async UniTaskVoid DiscardWhenFinished(UniTask extraction, string tempPath)
        {
            try
            {
                await extraction;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "The cancelled extraction ended in an error.");
            }

            DeleteDirectoryQuietly(tempPath);
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Could not delete {path}.");
            }
        }

        private static void DeleteDirectoryQuietly(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Could not delete the folder {path}.");
            }
        }

        /// <summary>
        /// Reduces a tag or asset name to a single safe path component. Release tags and asset
        /// names never need this today; it is what keeps a hostile GitHub response from writing
        /// outside the updates folder.
        /// </summary>
        /// <param name="fallback">What to return when nothing usable is left. Null means "refuse".</param>
        private static string SanitizeFileName(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            // GetFileName strips any directory part, including the "../" of a traversal.
            string cleaned = Path.GetFileName(name);
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                cleaned = cleaned.Replace(invalid, '_');
            }

            if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "." || cleaned == "..")
            {
                return fallback;
            }

            return cleaned;
        }
    }
}
