using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEngine;
using YARG;

namespace Editor.Build
{
    /// <summary>
    /// Batchmode entry points used by CI (GitHub Actions / game-ci unity-builder) to produce a
    /// Windows x64 or macOS Apple silicon player. Invoked with
    /// <c>-executeMethod Editor.Build.CIBuild.BuildWindows</c> or <c>Editor.Build.CIBuild.BuildMac</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="MakeTestBuild"/> this never touches <c>BuildPlayerWindow</c> (which needs a GUI),
    /// and it explicitly builds Addressables content first, because the project's
    /// <c>AddressableAssetSettings</c> has <c>BuildAddressablesWithPlayerBuild</c> set to
    /// "use the editor preference", which is machine-local and therefore unset on a CI runner.
    /// </remarks>
    public static class CIBuild
    {
        private const string NIGHTLY_DEFINE = "YARG_NIGHTLY_BUILD";

        private const string DEFAULT_BUILD_NAME = "YARG";

        // The macOS architecture is a per-machine platform setting; these are the names Unity stores it under
        private const string MAC_PLATFORM_SETTINGS = "OSXUniversal";
        private const string MAC_ARCHITECTURE_SETTING = "Architecture";
        private const string MAC_ARCHITECTURE_ARM64 = "ARM64";

        /// <summary>
        /// Builds a StandaloneWindows64 player with the nightly define set.
        /// Exits the editor with code 0 on success and 1 on any failure.
        /// </summary>
        public static void BuildWindows()
        {
            Build(BuildTarget.StandaloneWindows64, "Win64", ".exe");
        }

        /// <summary>
        /// Builds an Apple silicon (arm64) StandaloneOSX player with the nightly define set.
        /// Exits the editor with code 0 on success and 1 on any failure.
        /// </summary>
        /// <remarks>
        /// arm64 only, not universal: the libusb dylibs (ours and HIDrogen's) ship no x86_64 slice.
        /// </remarks>
        public static void BuildMac()
        {
            EditorUserBuildSettings.SetPlatformSettings(MAC_PLATFORM_SETTINGS, MAC_ARCHITECTURE_SETTING,
                MAC_ARCHITECTURE_ARM64);
            Build(BuildTarget.StandaloneOSX, "OSXUniversal", ".app");
        }

        private static void Build(BuildTarget target, string buildTargetArgument, string extension)
        {
            try
            {
                // Addressables resolves its [BuildTarget] profile variable from
                // EditorUserBuildSettings.activeBuildTarget, not from the BuildPlayerOptions handed
                // to BuildPipeline. If the editor is not already switched to the target, the catalog
                // and bundles land in StreamingAssets/aa/<wrong platform> and the player ships with
                // no Addressables content at all. game-ci's unity-builder always passes
                // -buildTarget; a hand-rolled invocation might not.
                if (EditorUserBuildSettings.activeBuildTarget != target)
                {
                    throw new InvalidOperationException(
                        $"Active build target is {EditorUserBuildSettings.activeBuildTarget}, not " +
                        $"{target}. Pass \"-buildTarget {buildTargetArgument}\" on the command line so " +
                        "Addressables content is built for the right platform.");
                }

                var options = CreateBuildPlayerOptions(target, extension);

                Debug.Log($"[CIBuild] Building to \"{options.locationPathName}\"");
                Debug.Log($"[CIBuild] Scenes ({options.scenes.Length}): {string.Join(", ", options.scenes)}");

                WriteVersionFile();
                BuildAddressables();

                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;

                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log(
                        $"[CIBuild] Build succeeded. Output: {summary.outputPath}, " +
                        $"size: {summary.totalSize} bytes, time: {summary.totalTime}, " +
                        $"warnings: {summary.totalWarnings}.");
                    EditorApplication.Exit(0);
                    return;
                }

                Debug.LogError(
                    $"[CIBuild] Build finished with result {summary.result} " +
                    $"({summary.totalErrors} error(s), {summary.totalWarnings} warning(s)).");
                LogBuildErrors(report);
                EditorApplication.Exit(1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CIBuild] Build threw an exception: {e}");
                EditorApplication.Exit(1);
            }
        }

        private static BuildPlayerOptions CreateBuildPlayerOptions(BuildTarget target, string extension)
        {
            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled && !string.IsNullOrEmpty(scene.path))
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                throw new InvalidOperationException(
                    "No enabled scenes in EditorBuildSettings; refusing to build an empty player.");
            }

            ApplyVersionOverride();

            return new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = ResolveOutputPath(target, extension),
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int) StandaloneBuildSubtarget.Player,
                extraScriptingDefines = new[] { NIGHTLY_DEFINE },
                options = BuildOptions.None,
            };
        }

        /// <summary>
        /// Works out where the .exe or .app should land.
        /// game-ci's unity-builder passes <c>-customBuildPath</c> as a full path to the output *file*
        /// plus <c>-customBuildName</c>. It only appends the extension for Windows
        /// (<c>&lt;workspace&gt;/&lt;buildsPath&gt;/&lt;target&gt;/&lt;buildName&gt;.exe</c>); for macOS
        /// the path has no extension, so it is completed here rather than treated as a directory.
        /// When run by hand, <c>-buildPath</c> may be given as a directory instead.
        /// </summary>
        private static string ResolveOutputPath(BuildTarget target, string extension)
        {
            string name = GetArgument("-customBuildName") ?? DEFAULT_BUILD_NAME;
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name += extension;
            }

            // game-ci passes a full file path here, with the extension on Windows only.
            string customBuildPath = GetArgument("-customBuildPath");
            if (!string.IsNullOrWhiteSpace(customBuildPath))
            {
                return customBuildPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                    ? customBuildPath
                    : customBuildPath + extension;
            }

            // Manual invocation: -buildPath is a directory.
            string buildPath = GetArgument("-buildPath");
            if (string.IsNullOrWhiteSpace(buildPath))
            {
                buildPath = $"build/{target}";
            }

            return buildPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? buildPath
                : Path.Combine(buildPath, name);
        }

        /// <summary>
        /// Optional <c>-version</c> / <c>-buildVersion</c> override for <c>PlayerSettings.bundleVersion</c>.
        /// Note that the version YARG actually displays comes from Assets/Resources/version.txt, which
        /// <see cref="BuildGitCommitVersion"/> writes from git during the pre-build step.
        /// </summary>
        private static void ApplyVersionOverride()
        {
            string version = GetArgument("-version") ?? GetArgument("-buildVersion");

            // game-ci's `versioning: None` strategy literally passes the string "none".
            if (string.IsNullOrWhiteSpace(version) || version == "none")
            {
                return;
            }

            Debug.Log($"[CIBuild] Setting PlayerSettings.bundleVersion to \"{version}\".");
            PlayerSettings.bundleVersion = version;
        }

        /// <summary>
        /// Writes and imports <c>Assets/Resources/version.txt</c>.
        /// </summary>
        /// <remarks>
        /// That file is gitignored, so on a clean CI checkout it does not exist.
        /// <see cref="BuildGitCommitVersion"/> does write it, but it does so from
        /// <c>IPreprocessBuildWithReport</c> — inside the build, using a raw
        /// <c>File.WriteAllText</c> with no <c>AssetDatabase</c> import — so Unity never sees it as
        /// a <c>TextAsset</c> and the player ships without it. <c>GlobalVariables.LoadVersion</c>
        /// would then silently fall back to the hardcoded version string. Doing it here, before the
        /// build starts, means the asset is imported by the time Resources are collected.
        /// </remarks>
        private static void WriteVersionFile()
        {
            const string versionAssetPath = "Assets/Resources/version.txt";

            Directory.CreateDirectory("Assets/Resources");
            string version = GlobalVariables.LoadVersionFromGit();
            File.WriteAllText(versionAssetPath, version);
            AssetDatabase.ImportAsset(versionAssetPath, ImportAssetOptions.ForceUpdate);

            Debug.Log($"[CIBuild] Wrote {versionAssetPath}: \"{version}\"");
        }

        private static void BuildAddressables()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                throw new InvalidOperationException(
                    "No AddressableAssetSettings found. Expected Assets/Settings/AddressableAssetsData.");
            }

            Debug.Log("[CIBuild] Building Addressables player content...");
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);

            if (result != null && !string.IsNullOrEmpty(result.Error))
            {
                throw new InvalidOperationException($"Addressables build failed: {result.Error}");
            }

            Debug.Log($"[CIBuild] Addressables build finished in {result?.Duration ?? 0} seconds.");
        }

        private static void LogBuildErrors(BuildReport report)
        {
            foreach (var step in report.steps)
            {
                foreach (var message in step.messages)
                {
                    if (message.type is LogType.Error or LogType.Exception or LogType.Assert)
                    {
                        Debug.LogError($"[CIBuild] {step.name}: {message.content}");
                    }
                }
            }
        }

        private static string GetArgument(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    string value = args[i + 1];

                    // Guard against a flag with an omitted value swallowing the next flag.
                    if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    return value;
                }
            }

            return null;
        }
    }
}
