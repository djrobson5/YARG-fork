using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YARG.Scores.Sync
{
    public enum SourceReadStatus
    {
        Ok,

        /// <summary>Written by a newer build; this PC needs updating.</summary>
        NewerFormat,

        /// <summary>Opened, but not a valid export (corrupt, missing fields, implausible values).</summary>
        Invalid,

        /// <summary>
        /// Could not be opened or read at all: offline with an on-demand file, locked, or gone.
        /// Worth retrying later.
        /// </summary>
        Unavailable,
    }

    /// <summary>
    /// The sync folder on disk (docs/score-sync-design.md, "One file per PC"): writes this PC's
    /// export atomically and lists and reads the other PCs' exports.
    /// </summary>
    /// <remarks>
    /// Only System.IO; the sync client (OneDrive, Google Drive, anything else) does the rest.
    /// Verified 2026-09-22 on this project's dev PC: File.Move, File.Replace, overwrite, listing
    /// and reading all work in a OneDrive for Business folder and in Google Drive for desktop's
    /// streamed G:\My Drive.
    /// </remarks>
    public static class ScoreSyncFolder
    {
        public const string FOLDER_NAME = "YARG Score Sync";
        public const string EXTENSION = ".yargsync";
        private const string TEMP_SUFFIX = ".tmp";

        public class SourceFile
        {
            public string   Path;
            public string   FileName;
            public long     Length;
            public DateTime LastWriteUtc;
        }

        public readonly struct SourceReadResult
        {
            public readonly SourceReadStatus Status;
            public readonly ScoreSyncFile    File;
            public readonly string           Error;

            public SourceReadResult(SourceReadStatus status, ScoreSyncFile file, string error)
            {
                Status = status;
                File = file;
                Error = error;
            }
        }

        /// <summary>
        /// The <c>YARG Score Sync</c> folder inside a sync root.
        /// </summary>
        public static string GetSyncDirectory(string syncRoot)
        {
            return Path.Combine(syncRoot, FOLDER_NAME);
        }

        /// <summary>
        /// The sync root for a folder picked with Browse. Picking the <c>YARG Score Sync</c>
        /// folder itself means its parent, so the files don't end up one level deeper.
        /// </summary>
        public static string RootFromBrowsedFolder(string folder)
        {
            string trimmed = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(trimmed), FOLDER_NAME, StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(trimmed);
                if (!string.IsNullOrEmpty(parent))
                {
                    return parent;
                }
            }

            return folder;
        }

        /// <summary>
        /// Writes this PC's export into the sync folder, replacing its previous export in one
        /// step so a sync client never uploads a half-written file.
        /// </summary>
        /// <returns>The full path of the written file.</returns>
        public static string WriteExport(string syncRoot, string exportFileName, ScoreSyncFile file)
        {
            string directory = GetSyncDirectory(syncRoot);
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, exportFileName);
            WriteAllBytesAtomic(path, ScoreSyncFileFormat.WriteToBytes(file));
            return path;
        }

        /// <summary>
        /// Lists the other PCs' export files. Listing never opens or downloads them.
        /// </summary>
        /// <param name="ownFileName">This PC's export file, which is left out.</param>
        public static List<SourceFile> ListSources(string syncRoot, string ownFileName)
        {
            string directory = GetSyncDirectory(syncRoot);
            if (!Directory.Exists(directory))
            {
                return new List<SourceFile>();
            }

            return new DirectoryInfo(directory)
                // No search pattern: on Linux it matches case-sensitively and would skip
                // "X.YARGSYNC", and on Windows it would also let "x.yargsyncfoo" through
                .EnumerateFiles()
                .Where(f => string.Equals(f.Extension, EXTENSION, StringComparison.OrdinalIgnoreCase))
                .Where(f => !string.Equals(f.Name, ownFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new SourceFile
                {
                    Path = f.FullName,
                    FileName = f.Name,
                    Length = f.Length,
                    LastWriteUtc = f.LastWriteTimeUtc,
                })
                .ToList();
        }

        /// <summary>
        /// Reads one other PC's export. Never throws.
        /// </summary>
        /// <remarks>
        /// Opening a file that is only in the cloud makes the sync client download it, which can
        /// be slow and fails offline; that comes back as <see cref="SourceReadStatus.Unavailable"/>.
        /// </remarks>
        public static SourceReadResult ReadSource(string path)
        {
            byte[] bytes;
            try
            {
                // Share everything, so a sync client updating the file never makes this fail
                // for a reason other than the file really being unavailable
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            catch (Exception e)
            {
                return new SourceReadResult(SourceReadStatus.Unavailable, null,
                    $"Couldn't open {System.IO.Path.GetFileName(path)}: {e.Message}");
            }

            var result = ScoreSyncFileFormat.ReadFromBytes(bytes);
            var status = result.Status switch
            {
                SyncReadStatus.Ok          => SourceReadStatus.Ok,
                SyncReadStatus.NewerFormat => SourceReadStatus.NewerFormat,
                _                          => SourceReadStatus.Invalid,
            };

            return new SourceReadResult(status, result.File, result.Error);
        }

        /// <summary>
        /// Writes <c>path.tmp</c>, then swaps it in with File.Replace (or a move when there is
        /// nothing to replace). A failure leaves the previous file untouched and no temp file.
        /// </summary>
        public static void WriteAllBytesAtomic(string path, byte[] bytes)
        {
            string temp = path + TEMP_SUFFIX;
            try
            {
                File.WriteAllBytes(temp, bytes);

                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                    // Best effort; the next write overwrites it
                }
            }
        }
    }
}
