using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using YARG.Core.Logging;
using YARG.Helpers;

namespace YARG.Song
{
    /// <summary>
    /// Applies a build that <see cref="UpdateDownloader"/> already staged, by handing the job to
    /// a small helper <c>.cmd</c> that runs after the game has quit.
    /// </summary>
    /// <remarks>
    /// Windows cannot overwrite a running process's executable, so the install cannot be
    /// replaced in-process. The helper waits for this process to exit, moves the current install
    /// aside into a backup, copies the staged build over it, relaunches and deletes itself — the
    /// same dance as the "Apply" half of <c>tools/update-yarg.ps1</c>. See
    /// <c>docs/updater-design.md</c>, slice 4.
    /// <para>
    /// The helper is never elevated. If the install directory is not writable, the flow stops
    /// before anything is moved and the user is told to move the install instead.
    /// </para>
    /// </remarks>
    public static class UpdateInstaller
    {
        public enum InstallStatus
        {
            /// <summary>The helper was written and started; the caller should quit now.</summary>
            Launched,

            /// <summary>This platform (or the editor) has no in-place install path.</summary>
            NotSupported,

            /// <summary>The staged build is gone, or no longer looks like a YARG build.</summary>
            NotStaged,

            /// <summary>The install directory could not be located.</summary>
            NoInstallDirectory,

            /// <summary>The install directory refused a write probe. Nothing was touched.</summary>
            NotWritable,

            /// <summary>The helper could not be written or started. Nothing was touched.</summary>
            HelperFailed,
        }

        /// <summary>
        /// Whether applying an update from inside the game is possible at all.
        /// </summary>
        /// <remarks>
        /// Windows only, and never in the editor — where <see cref="PathHelper.ExecutablePath"/>
        /// is the Unity project folder and copying a player build over it would be a disaster.
        /// The Settings row is already hidden outside a CI release build
        /// (<see cref="UpdateChecker.IsReleaseBuild"/>); this is the belt to that's braces.
        /// </remarks>
        public static bool IsSupported
        {
            get
            {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>The folder holding <c>YARG.exe</c>, i.e. the install to be replaced.</summary>
        public static string InstallDirectory => PathHelper.ExecutablePath;

        /// <summary>
        /// Where the previous install is kept. A sibling of the install directory, so replacing
        /// the install cannot touch it. Exactly one backup is kept: applying an update deletes
        /// this whole folder before making the new one.
        /// </summary>
        public static string BackupRoot
        {
            get
            {
                string install = InstallDirectory;
                if (string.IsNullOrEmpty(install))
                {
                    return null;
                }

                string parent = Directory.GetParent(install)?.FullName;
                return parent == null ? null : Path.Combine(parent, "backup");
            }
        }

        /// <summary>
        /// Probes writability by actually creating and deleting a file, because inspecting ACLs
        /// lies (virtualisation, inherited denies, read-only media).
        /// </summary>
        /// <remarks>
        /// This is the check that keeps an install under <c>C:\Program Files</c> from being
        /// half-moved and then failing. The updater never elevates.
        /// </remarks>
        public static bool IsInstallWritable()
        {
            string install = InstallDirectory;
            if (string.IsNullOrEmpty(install) || !Directory.Exists(install))
            {
                return false;
            }

            string probe = Path.Combine(install, ".yarg-update-write-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
                return true;
            }
            catch (Exception e)
            {
                YargLogger.LogFormatWarning("The install directory is not writable: {0}", e.Message);

                try
                {
                    if (File.Exists(probe))
                    {
                        File.Delete(probe);
                    }
                }
                catch (Exception)
                {
                    // A probe we could not delete is not worth a second error.
                }

                return false;
            }
        }

        /// <summary>
        /// Whether <paramref name="stagingPath"/> still holds something worth installing. Slice 3
        /// leaves an already-extracted tree there (the downloaded <c>.zip</c> stays beside it in
        /// the updates folder and is not what gets installed), so this is a layout check, not an
        /// archive check.
        /// </summary>
        public static bool IsStagedBuildValid(string stagingPath)
        {
            return !string.IsNullOrEmpty(stagingPath) &&
                Directory.Exists(stagingPath) &&
                File.Exists(Path.Combine(stagingPath, "YARG.exe")) &&
                Directory.Exists(Path.Combine(stagingPath, "YARG_Data"));
        }

        /// <summary>
        /// Writes the helper and starts it. On <see cref="InstallStatus.Launched"/> the caller
        /// must quit promptly — the helper is already polling for this process to exit.
        /// </summary>
        /// <param name="newTag">The release tag being installed.</param>
        /// <param name="stagingPath">The staged build, from <see cref="UpdateDownloader"/>.</param>
        public static InstallStatus Apply(string newTag, string stagingPath)
        {
            if (!IsSupported)
            {
                return InstallStatus.NotSupported;
            }

            string install = InstallDirectory;
            string backupRoot = BackupRoot;
            if (string.IsNullOrEmpty(install) || !Directory.Exists(install) || string.IsNullOrEmpty(backupRoot))
            {
                YargLogger.LogWarning("Could not work out where YARG is installed; refusing to apply the update.");
                return InstallStatus.NoInstallDirectory;
            }

            if (!IsStagedBuildValid(stagingPath))
            {
                YargLogger.LogFormatWarning("There is no usable staged build at {0}.", stagingPath);
                return InstallStatus.NotStaged;
            }

            // Checked before a single file moves. An install the user cannot write to is a
            // "move your install" problem, never an "ask for administrator" one.
            if (!IsInstallWritable())
            {
                return InstallStatus.NotWritable;
            }

            // The backup is named after the build being replaced. Application.version is the
            // release tag in a CI build (CIBuild.ApplyVersionOverride), which is exactly what
            // tools/update-yarg.ps1 reads back out of YARG_Data\globalgamemanagers.
            string oldTag = SanitizeTag(Application.version, "unknown");
            string newTagSafe = SanitizeTag(newTag, "update");

            string backupPath = Path.Combine(backupRoot, oldTag);
            string helperPath = Path.Combine(UpdateDownloader.UpdatesRoot, $"apply-{newTagSafe}.cmd");
            string logPath = Path.Combine(UpdateDownloader.UpdatesRoot, $"apply-{newTagSafe}.log");

            int pid;
            try
            {
                pid = Process.GetCurrentProcess().Id;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Could not read this process's PID.");
                return InstallStatus.HelperFailed;
            }

            string script = HELPER_TEMPLATE
                .Replace("@PID@", pid.ToString())
                .Replace("@INSTALL@", install)
                .Replace("@BACKUP_ROOT@", backupRoot)
                .Replace("@BACKUP@", backupPath)
                .Replace("@STAGING@", stagingPath)
                // Sanitized, not raw: only the "-sectionfc.<n>" suffix of a tag is checked, so
                // everything before it is whatever GitHub said, and a quote or a caret in it
                // would break out of the helper's `set "TAG=..."` line.
                .Replace("@TAG@", newTagSafe)
                .Replace("@LOG@", logPath);

            try
            {
                Directory.CreateDirectory(UpdateDownloader.UpdatesRoot);

                // CRLF and ASCII: a .cmd with LF endings or a BOM misbehaves in cmd.exe.
                File.WriteAllText(helperPath, script.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                    System.Text.Encoding.ASCII);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Could not write the update helper to {helperPath}.");
                return InstallStatus.HelperFailed;
            }

            try
            {
                // cmd.exe /c, because CreateProcess cannot run a .cmd directly with
                // UseShellExecute = false. The doubled quotes are cmd's own quirk: with a quoted
                // command it strips the outermost pair, so a path with spaces needs two.
                var startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{helperPath}\"\"",

                    // Deliberately *not* the install directory: a working directory there would
                    // hold a handle on the folder the helper is about to move.
                    WorkingDirectory = UpdateDownloader.UpdatesRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var helper = Process.Start(startInfo);
                if (helper == null)
                {
                    YargLogger.LogWarning("The update helper did not start.");
                    return InstallStatus.HelperFailed;
                }
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Could not start the update helper.");
                return InstallStatus.HelperFailed;
            }

            YargLogger.LogFormatInfo("Update helper started; it is waiting for PID {0} to exit.", pid);
            return InstallStatus.Launched;
        }

        /// <summary>
        /// Reduces a release tag to a single safe path component, so a hostile or malformed tag
        /// cannot aim the backup folder somewhere else.
        /// </summary>
        private static string SanitizeTag(string tag, string fallback)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return fallback;
            }

            string cleaned = Path.GetFileName(tag);
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                cleaned = cleaned.Replace(invalid, '_');
            }

            // Quotes and percent signs would break out of the helper's `set "VAR=..."` lines.
            cleaned = cleaned.Replace('"', '_').Replace('%', '_').Replace('&', '_').Replace('^', '_');

            if (string.IsNullOrWhiteSpace(cleaned) || cleaned == "." || cleaned == "..")
            {
                return fallback;
            }

            return cleaned;
        }

        /// <summary>
        /// The helper script, as a constant rather than a StreamingAssets file: it is one screen
        /// long, it is only meaningful next to the C# that fills in its paths, and keeping it
        /// here means it cannot be missing from, or edited inside, a shipped install.
        /// </summary>
        /// <remarks>
        /// Every path is written with <c>set "VAR=value"</c> and used quoted, so install
        /// directories with spaces are fine. <c>ping</c> rather than <c>timeout</c> is the sleep,
        /// because <c>timeout</c> refuses to run without a console to read from and the helper is
        /// started with no window.
        /// </remarks>
        private const string HELPER_TEMPLATE = @"@echo off
setlocal enableextensions
title YARG update

rem ---------------------------------------------------------------------------
rem  Generated by YARG.Song.UpdateInstaller. It deletes itself when it is done.
rem  Do not edit; it is rewritten on every update.
rem ---------------------------------------------------------------------------

set ""YARG_PID=@PID@""
set ""INSTALL=@INSTALL@""
set ""BACKUP_ROOT=@BACKUP_ROOT@""
set ""BACKUP=@BACKUP@""
set ""STAGING=@STAGING@""
set ""TAG=@TAG@""
set ""LOG=@LOG@""

rem The external tools below are called by absolute path. A ""find"" or ""tasklist""
rem earlier on PATH (git's, for one) has different exit codes, and mistaking ""still running""
rem for ""exited"" here would move an install out from under a live game.
set ""SYS=%SystemRoot%\System32""

rem Set once the whole install has been moved into the backup, so the restore path knows the
rem backup is complete and may safely purge the install.
set ""MOVED=0""

>""%LOG%"" echo [%DATE% %TIME%] Installing %TAG% into ""%INSTALL%"".
>>""%LOG%"" echo Waiting for PID %YARG_PID% to exit.

rem 1. Wait for the game to close. Two minutes, then give up without touching anything.
set /a TRIES=0
:waitloop
""%SYS%\tasklist.exe"" /FI ""PID eq %YARG_PID%"" /NH 2>nul | ""%SYS%\find.exe"" ""%YARG_PID%"" >nul
if errorlevel 1 goto exited
set /a TRIES+=1
if %TRIES% GEQ 120 (
    >>""%LOG%"" echo ERROR: timed out waiting for PID %YARG_PID%; nothing was changed.
    rem Straight to cleanup, not :abort -- the game is still running, and relaunching it here
    rem would leave the user with two copies of it.
    goto cleanup
)
>nul ""%SYS%\ping.exe"" -n 2 127.0.0.1
goto waitloop
:exited
>>""%LOG%"" echo The game has exited.

rem 2. Exactly one backup is kept, so the previous one goes first.
if exist ""%BACKUP_ROOT%"" rd /s /q ""%BACKUP_ROOT%""
if exist ""%BACKUP%"" (
    >>""%LOG%"" echo ERROR: could not clear ""%BACKUP_ROOT%""; nothing was changed.
    goto abort
)
md ""%BACKUP%"" 2>nul
if not exist ""%BACKUP%"" (
    >>""%LOG%"" echo ERROR: could not create ""%BACKUP%""; nothing was changed.
    goto abort
)

rem 3. Move the current install aside. Moving rather than copying keeps this cheap and
rem    leaves a complete, working build behind if anything below fails.
rem
rem    robocopy, not a `dir /b /a` + `move` loop: `move` refuses hidden and system files
rem    (""The system cannot find the file specified.""), and `for /f` over `dir /b` silently
rem    skips any name starting with "";"" -- either one would leave part of the old build in
rem    place and then get it deleted by the copy below. robocopy exit codes under 8 are
rem    success. /MOVE removes the source directory itself, so it is recreated afterwards.
""%SYS%\robocopy.exe"" ""%INSTALL%"" ""%BACKUP%"" /E /MOVE /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >>""%LOG%"" 2>&1
if errorlevel 8 (
    >>""%LOG%"" echo ERROR: could not move the install into the backup.
    goto restore
)
set ""MOVED=1""
md ""%INSTALL%"" 2>nul
>>""%LOG%"" echo Previous build moved to ""%BACKUP%"".

rem 4. Copy the staged build over the install.
""%SYS%\xcopy.exe"" ""%STAGING%\*"" ""%INSTALL%\"" /E /I /H /Y >>""%LOG%"" 2>&1
if errorlevel 1 (
    >>""%LOG%"" echo ERROR: the copy failed.
    goto restore
)
if not exist ""%INSTALL%\YARG.exe"" (
    >>""%LOG%"" echo ERROR: YARG.exe is missing after the copy.
    goto restore
)
if not exist ""%INSTALL%\YARG_Data"" (
    >>""%LOG%"" echo ERROR: YARG_Data is missing after the copy.
    goto restore
)

rem 5. Record what was installed, matching tools\update-yarg.ps1's marker.
>""%INSTALL%\.yarg-update-tag"" echo %TAG%

rem 6. The staged copy has served its purpose. The .zip beside it is kept.
if exist ""%STAGING%"" rd /s /q ""%STAGING%""

>>""%LOG%"" echo Installed %TAG%. The previous build is at ""%BACKUP%"".
start """" /D ""%INSTALL%"" ""%INSTALL%\YARG.exe""
goto cleanup

:restore
>>""%LOG%"" echo Restoring the previous build from ""%BACKUP%"".
md ""%INSTALL%"" 2>nul

rem /PURGE (delete whatever is in the install but not in the backup, i.e. the half-copied new
rem build) is only safe once the whole old install is known to have reached the backup. If the
rem move above failed part way, the install still holds originals that were never copied
rem anywhere, and purging would destroy them outright.
set ""RESTORE_FLAGS=/E /MOVE""
if ""%MOVED%""==""1"" if exist ""%BACKUP%\YARG.exe"" set ""RESTORE_FLAGS=/E /MOVE /PURGE""
""%SYS%\robocopy.exe"" ""%BACKUP%"" ""%INSTALL%"" %RESTORE_FLAGS% /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >>""%LOG%"" 2>&1

if exist ""%INSTALL%\YARG.exe"" (
    >>""%LOG%"" echo The previous build was restored.
    if exist ""%BACKUP_ROOT%"" rd /s /q ""%BACKUP_ROOT%""
) else (
    >>""%LOG%"" echo ERROR: the restore failed. The previous build is still at ""%BACKUP%"".
)

:abort
if exist ""%INSTALL%\YARG.exe"" start """" /D ""%INSTALL%"" ""%INSTALL%\YARG.exe""

:cleanup
(goto) 2>nul & del ""%~f0""
";
    }
}
