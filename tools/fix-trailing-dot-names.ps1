<#
.SYNOPSIS
    Rename song folders (and files) whose names end in a period or a space, so a
    Mac can read them.

.DESCRIPTION
    Windows tools can leave names like "H.E.R." or "What If..." on a drive. Windows
    itself hides the trailing dot by trimming it from every path it is handed, and
    macOS's exFAT driver lists such a folder but cannot open or rename it, so YARG
    on a Mac skips every song inside. This script walks a folder tree with
    "\\?\" paths, which turn that trimming off, and renames each such entry to
    its name without the trailing periods and spaces ("H.E.R." -> "H.E.R").

    Deepest entries are renamed first, so renaming a folder never invalidates a
    path still to be processed. An entry is skipped when the new name already
    exists beside it. Junctions and symlinks are listed but not followed.

    YARG identifies songs by chart hash, not folder path, so scores, playlists and
    favorites are unaffected; the next song scan picks up the new paths.

    Runs as a dry run unless -Apply is given. Windows only.

.PARAMETER Root
    The folder to scan, e.g. a song folder on the external drive.

.PARAMETER Apply
    Perform the renames. Without it the script only prints what it would do.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\fix-trailing-dot-names.ps1 -Root "E:\Songs"

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\fix-trailing-dot-names.ps1 -Root "E:\Songs" -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root,

    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

# Win32 directly, because .NET in Windows PowerShell 5.1 may still normalize "\\?\" paths
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class TrailingDotNames
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileW(string lpExistingFileName, string lpNewFileName);

    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    /// <summary>Every entry under root, as full "\\?\" paths. Reparse points are not entered.</summary>
    public static List<string> Walk(string root)
    {
        var entries = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            WIN32_FIND_DATA data;
            IntPtr handle = FindFirstFileW(dir + "\\*", out data);
            if (handle == INVALID_HANDLE_VALUE)
            {
                continue;
            }

            try
            {
                do
                {
                    if (data.cFileName == "." || data.cFileName == "..")
                    {
                        continue;
                    }

                    string path = dir + "\\" + data.cFileName;
                    entries.Add(path);

                    if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 &&
                        (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
                    {
                        pending.Push(path);
                    }
                } while (FindNextFileW(handle, out data));
            }
            finally
            {
                FindClose(handle);
            }
        }

        return entries;
    }

    public static bool Exists(string path)
    {
        return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
    }

    /// <summary>Renames, returning 0 on success or the Win32 error code.</summary>
    public static int Move(string from, string to)
    {
        return MoveFileW(from, to) ? 0 : Marshal.GetLastWin32Error();
    }
}
'@

$full = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
if ($full.StartsWith('\\')) {
    $longRoot = '\\?\UNC\' + $full.Substring(2)
} else {
    $longRoot = '\\?\' + $full
}

if (-not [TrailingDotNames]::Exists($longRoot)) {
    throw "Folder not found: $full"
}

Write-Host "Scanning $full ..."
$entries = [TrailingDotNames]::Walk($longRoot)

# Deepest first, so a folder is renamed only after everything inside it
$bad = $entries |
    Where-Object { $name = $_.Substring($_.LastIndexOf('\') + 1); $name -ne $name.TrimEnd('.', ' ') } |
    Sort-Object { ($_ -split '\\').Count } -Descending

Write-Host "$($entries.Count) entries scanned, $(@($bad).Count) with a trailing period or space."

$renamed = 0
$skipped = 0
$failed = 0
foreach ($path in $bad) {
    $parent = $path.Substring(0, $path.LastIndexOf('\'))
    $name = $path.Substring($path.LastIndexOf('\') + 1)
    $newName = $name.TrimEnd('.', ' ')
    $shown = $path.Substring($longRoot.Length + 1)

    if ($newName -eq '') {
        Write-Warning "Skipped (nothing left after trimming): $shown"
        $skipped++
        continue
    }

    $target = "$parent\$newName"
    if ([TrailingDotNames]::Exists($target)) {
        Write-Warning "Skipped ('$newName' already exists): $shown"
        $skipped++
        continue
    }

    if (-not $Apply) {
        Write-Host "Would rename: $shown -> $newName"
        continue
    }

    $err = [TrailingDotNames]::Move($path, $target)
    if ($err -eq 0) {
        Write-Host "Renamed: $shown -> $newName"
        $renamed++
    } else {
        Write-Warning "Failed (Win32 error $err): $shown"
        $failed++
    }
}

if ($Apply) {
    Write-Host "Done: $renamed renamed, $skipped skipped, $failed failed."
} else {
    Write-Host "Dry run: nothing was renamed. $skipped would be skipped. Run again with -Apply to rename."
}
