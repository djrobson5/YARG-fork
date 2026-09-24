using System;
using System.IO;
using YARG.Core.Logging;
using YARG.Settings;

#if UNITY_STANDALONE_WIN
using System.Runtime.InteropServices;
#endif

namespace YARG.Helpers
{
    /// <summary>
    /// How a path relates to the user's configured song folders.
    /// </summary>
    public enum SongPathSafety
    {
        /// <summary>The path is a strict descendant of at least one configured song folder.</summary>
        Safe,

        /// <summary>The path <i>is</i> a configured song folder. Deleting it would gut the library.</summary>
        IsLibraryRoot,

        /// <summary>
        /// The path is not under any configured song folder — a malformed entry location, a
        /// <c>..</c> in an ExCON sub-name, or a folder the user removed from settings.
        /// </summary>
        OutsideLibrary,
    }

    /// <summary>
    /// Deletes a file or directory, using the platform's trash/recycle bin where one is
    /// reachable without extra native glue.
    /// </summary>
    /// <remarks>
    /// Only Windows has a managed-free path to its trash (<c>SHFileOperationW</c> with
    /// <c>FOF_ALLOWUNDO</c>). macOS would need <c>NSFileManager trashItemAtURL:</c> and Linux
    /// has no single answer, so both delete permanently. Callers must tell the user which of
    /// the two is going to happen <i>before</i> they confirm.
    /// </remarks>
    public static class FileDeleteHelper
    {
        /// <summary>
        /// Whether <see cref="SendToTrashOrDelete"/> can put files in a recoverable trash on
        /// this platform. Used for dialog wording, so it must not lie.
        /// </summary>
        public static bool SupportsTrash =>
#if UNITY_STANDALONE_WIN
            true;
#else
            false;
#endif

        private const StringComparison PATH_COMPARISON =
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN || UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            StringComparison.OrdinalIgnoreCase;
#else
            StringComparison.Ordinal;
#endif

        /// <summary>
        /// Classifies <paramref name="path"/> against <c>SettingsManager.Settings.SongFolders</c>.
        /// </summary>
        /// <remarks>
        /// The delete flow refuses anything that is not <see cref="SongPathSafety.Safe"/>. A song
        /// entry's location is derived from scan data, and a hand-edited <c>songs.dta</c> can put
        /// a <c>..</c> in an ExCON sub-name, so "it came from the library" is not on its own a
        /// guarantee that the path sits inside the library.
        /// </remarks>
        public static SongPathSafety CheckSongPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return SongPathSafety.OutsideLibrary;
            }

            if (!TryNormalize(path, out string full))
            {
                return SongPathSafety.OutsideLibrary;
            }

            bool isDescendant = false;

            foreach (var folder in SettingsManager.Settings.SongFolders)
            {
                if (string.IsNullOrEmpty(folder) || !TryNormalize(folder, out string root))
                {
                    continue;
                }

                if (string.Equals(full, root, PATH_COMPARISON))
                {
                    // A root match is the stronger refusal, and it needs its own message.
                    return SongPathSafety.IsLibraryRoot;
                }

                if (full.StartsWith(root + Path.DirectorySeparatorChar, PATH_COMPARISON))
                {
                    // Keep scanning: a later folder could still be an exact match.
                    isDescendant = true;
                }
            }

            return isDescendant ? SongPathSafety.Safe : SongPathSafety.OutsideLibrary;
        }

        /// <summary>
        /// Resolves a path to a full path with any trailing separators removed, so that
        /// prefix and equality comparisons behave.
        /// </summary>
        private static bool TryNormalize(string path, out string normalized)
        {
            try
            {
                normalized = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // A root such as `C:\` trims down to `C:`; put the separator back so it can
                // never prefix-match `C:\Users` as if it were a sibling named `C:...`.
                if (normalized.Length == 0)
                {
                    normalized = Path.DirectorySeparatorChar.ToString();
                }

                return true;
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, $"Could not resolve the path `{path}`.");
                normalized = null;
                return false;
            }
        }

#if UNITY_STANDALONE_WIN
        private const uint FO_DELETE = 0x0003;

        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOERRORUI = 0x0400;

        // Paths at or over MAX_PATH are the classic SHFileOperation failure; worth its own log line.
        private const int MAX_PATH_WITHOUT_NUL = 259;

        // No `Pack` here on purpose. shellapi.h only applies `#pragma pack(1)` to SHFILEOPSTRUCT
        // under `#ifndef _WIN64`; on x64 the struct uses natural 8-byte alignment. Forcing
        // `Pack = 1` would misalign every field after `wFunc` and hand shell32 garbage.
        // Unity 6 Windows standalone is x64 only, so default packing is the correct one — see
        // the IntPtr.Size guard in SendToTrashOrDelete for the case where that stops being true.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint   wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool   fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpszProgressTitle;
        }

        // SHFileOperation reports its own error codes through the return value and does not
        // set the last Win32 error, so SetLastError would only cost a marshalling round trip.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCT fileOp);

        private static bool TrySendToRecycleBin(string path)
        {
            try
            {
                if (path.Length > MAX_PATH_WITHOUT_NUL)
                {
                    YargLogger.LogFormatWarning<int, int>(
                        "The path is {0} characters, over the {1}-character limit SHFileOperation " +
                        "accepts; the recycle is likely to fail.",
                        path.Length, MAX_PATH_WITHOUT_NUL);
                }

                var fileOp = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    // pFrom is a double-null-terminated list of paths
                    pFrom = path + '\0' + '\0',
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
                };

                int result = SHFileOperationW(ref fileOp);
                if (result != 0)
                {
                    YargLogger.LogFormatWarning<int, string>(
                        "SHFileOperation returned {0} for `{1}`; the song was not deleted.",
                        result, path);
                    return false;
                }

                if (fileOp.fAnyOperationsAborted)
                {
                    YargLogger.LogFormatWarning("Recycling `{0}` was aborted; the song was not deleted.",
                        path);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, $"Could not recycle `{path}`.");
                return false;
            }
        }
#endif

        private static bool DeletePermanently(string path, bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    Directory.Delete(path, true);
                }
                else
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex)
            {
                YargLogger.LogException(ex, $"Could not delete `{path}`!");
                return false;
            }
        }

        /// <summary>
        /// Deletes <paramref name="path"/>, which may be a file or a directory.
        /// </summary>
        /// <remarks>
        /// On Windows a failed recycle is <b>never</b> escalated to a permanent delete: the user
        /// agreed to a recoverable delete, so the only honest outcome of a failure is a failure.
        /// <para>
        /// Even a successful recycle is not an absolute promise of recoverability.
        /// <c>FOF_ALLOWUNDO</c> is best-effort: Windows deletes permanently when the drive has no
        /// Recycle Bin (most network shares and many removable volumes), when the bin is disabled
        /// for that drive, or when the item is larger than the bin's quota. Dialog wording must
        /// therefore stay hedged — "where possible" — rather than promise a restore.
        /// </para>
        /// </remarks>
        /// <param name="trashed">
        /// <c>true</c> if the path went to the recycle bin and can probably be restored,
        /// <c>false</c> if it was deleted permanently. Only meaningful when this returns <c>true</c>.
        /// </param>
        /// <returns><c>true</c> if the path is gone, <c>false</c> if the delete failed.</returns>
        public static bool SendToTrashOrDelete(string path, out bool trashed)
        {
            trashed = false;

            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            bool isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
            {
                // Already gone. Treat that as success so the caller still cleans up the library.
                YargLogger.LogFormatWarning("Nothing to delete at `{0}`; it is already gone.", path);
                return true;
            }

#if UNITY_STANDALONE_WIN
            // The SHFILEOPSTRUCT layout above is the x64 one. If this ever runs 32-bit, the
            // struct would need `Pack = 1` and the P/Invoke would corrupt memory as written,
            // so fall back to a permanent delete rather than call it.
            if (IntPtr.Size != 8)
            {
                YargLogger.LogWarning(
                    "Not a 64-bit process, so the SHFILEOPSTRUCT layout does not apply; " +
                    "deleting permanently instead of recycling.");
                return DeletePermanently(path, isDirectory);
            }

            if (!TrySendToRecycleBin(path))
            {
                // Deliberately not falling back to a permanent delete: the user confirmed a
                // recoverable delete, and silently upgrading it to a destructive one is worse
                // than doing nothing.
                return false;
            }

            trashed = true;
            return true;
#else
            return DeletePermanently(path, isDirectory);
#endif
        }
    }
}
