using System.IO;
using System.Runtime.InteropServices;
using WinOptimizer.Helpers;

namespace WinOptimizer.Services;

public sealed class JunkCategory : ObservableObject
{
    private bool _isSelected;
    private long _sizeBytes = -1;
    private int _fileCount;

    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool IsRecycleBin { get; init; }

    /// <summary>Folders whose contents get cleaned (the folders themselves are kept).</summary>
    public Func<IEnumerable<string>> Folders { get; init; } = () => [];
    public string SearchPattern { get; init; } = "*";
    public bool Recursive { get; init; } = true;

    /// <summary>Files newer than this are left alone (they may still be in use).</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;

    public bool CanSelect => !RequiresAdmin || AdminHelper.IsAdmin;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value)) OnPropertyChanged(nameof(SizeText));
        }
    }

    public int FileCount
    {
        get => _fileCount;
        set
        {
            if (SetProperty(ref _fileCount, value)) OnPropertyChanged(nameof(SizeText));
        }
    }

    public string SizeText =>
        !CanSelect ? "Requires admin"
        : SizeBytes < 0 ? "Not scanned"
        : IsRecycleBin ? $"{Format.Bytes(SizeBytes)} · {FileCount:N0} items"
        : $"{Format.Bytes(SizeBytes)} · {FileCount:N0} files";
}

public static class JunkCleanerService
{
    /// <summary>Total junk found by the most recent scan in this session (null = not scanned yet).</summary>
    public static long? LastScanBytes { get; set; }

    private static readonly EnumerationOptions FileOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        // Don't skip hidden/system files, but never follow junctions/symlinks out of the folder.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static List<JunkCategory> CreateCategories()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        return
        [
            new JunkCategory
            {
                Name = "User temporary files",
                Description = "Files apps left in your Temp folder. Files changed in the last 24 hours are kept.",
                Folders = () => [Path.GetTempPath()],
                MinAge = TimeSpan.FromHours(24),
                IsSelected = true,
            },
            new JunkCategory
            {
                Name = "Windows temporary files",
                Description = @"Files in C:\Windows\Temp. Files changed in the last 24 hours are kept.",
                Folders = () => [Path.Combine(windows, "Temp")],
                MinAge = TimeSpan.FromHours(24),
                RequiresAdmin = true,
                IsSelected = true,
            },
            new JunkCategory
            {
                Name = "Windows Update download cache",
                Description = "Update packages that were already downloaded. Windows downloads them again if needed.",
                Folders = () => [Path.Combine(windows, "SoftwareDistribution", "Download")],
                MinAge = TimeSpan.FromHours(24),
                RequiresAdmin = true,
                IsSelected = true,
            },
            new JunkCategory
            {
                Name = "Browser cache",
                Description = "Cached web pages and images for Chrome, Edge, Brave and Firefox. Close browsers for best results.",
                Folders = () => BrowserCacheFolders(local),
                IsSelected = true,
            },
            new JunkCategory
            {
                Name = "Shader cache",
                Description = "DirectX and NVIDIA shader caches. Games rebuild them when needed.",
                Folders = () => [Path.Combine(local, "D3DSCache"), Path.Combine(local, "NVIDIA", "DXCache"), Path.Combine(local, "NVIDIA", "GLCache")],
                IsSelected = true,
            },
            new JunkCategory
            {
                Name = "Thumbnail cache",
                Description = "Picture and video previews in File Explorer. Rebuilt automatically.",
                Folders = () => [Path.Combine(local, "Microsoft", "Windows", "Explorer")],
                SearchPattern = "thumbcache_*.db",
                Recursive = false,
                IsSelected = true,
            },
            new JunkCategory
            {
                Name = "Crash dumps and error reports",
                Description = "Memory dumps and Windows Error Reporting files from crashed apps.",
                Folders = () =>
                [
                    Path.Combine(local, "CrashDumps"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                    Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
                ],
                IsSelected = true,
            },
            new JunkCategory
            {
                Name = "Recycle Bin",
                Description = "Permanently deletes everything in the Recycle Bin on all drives.",
                IsRecycleBin = true,
            },
        ];
    }

    private static IEnumerable<string> BrowserCacheFolders(string local)
    {
        string[] chromiumRoots =
        [
            Path.Combine(local, "Google", "Chrome", "User Data"),
            Path.Combine(local, "Microsoft", "Edge", "User Data"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
        ];

        foreach (string root in chromiumRoots.Where(Directory.Exists))
        {
            foreach (string profile in SafeDirectories(root))
            {
                string name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                yield return Path.Combine(profile, "Cache");
                yield return Path.Combine(profile, "Code Cache");
                yield return Path.Combine(profile, "GPUCache");
            }
        }

        string firefox = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        foreach (string profile in SafeDirectories(firefox))
            yield return Path.Combine(profile, "cache2");
    }

    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<FileInfo> EnumerateFiles(JunkCategory category)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = FileOptions.IgnoreInaccessible,
            RecurseSubdirectories = category.Recursive,
            AttributesToSkip = FileOptions.AttributesToSkip,
        };
        DateTime cutoff = DateTime.UtcNow - category.MinAge;

        foreach (string folder in category.Folders().Where(Directory.Exists))
        {
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles(category.SearchPattern, options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (category.MinAge > TimeSpan.Zero && file.LastWriteTimeUtc > cutoff) continue;
                yield return file;
            }
        }
    }

    /// <summary>Measures how much space a category takes. Updates the category's size properties.</summary>
    public static (long Bytes, int Count) Scan(JunkCategory category)
    {
        if (category.IsRecycleBin) return QueryRecycleBin();

        long bytes = 0;
        int count = 0;
        foreach (var file in EnumerateFiles(category))
        {
            try
            {
                bytes += file.Length;
                count++;
            }
            catch (IOException)
            {
                // Deleted while scanning.
            }
        }
        return (bytes, count);
    }

    /// <summary>Deletes the category's files. Locked or protected files are skipped. Returns bytes freed.</summary>
    public static long Clean(JunkCategory category)
    {
        if (category.IsRecycleBin)
        {
            var (size, items) = QueryRecycleBin();
            if (items == 0) return 0;
            SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return size - QueryRecycleBin().Bytes;
        }

        long freed = 0;
        foreach (var file in EnumerateFiles(category).ToList())
        {
            try
            {
                long length = file.Length;
                if (file.IsReadOnly) file.IsReadOnly = false;
                file.Delete();
                freed += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or protected – skip it.
            }
        }

        if (category.Recursive) DeleteEmptySubfolders(category);
        return freed;
    }

    private static void DeleteEmptySubfolders(JunkCategory category)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (string folder in category.Folders().Where(Directory.Exists))
        {
            List<string> subfolders;
            try
            {
                subfolders = Directory.EnumerateDirectories(folder, "*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Deepest first so parents become empty before we try them.
            foreach (string dir in subfolders.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip.
                }
            }
        }
    }

    private static (long Bytes, int Count) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, (int)info.i64NumItems) : (0, 0);
    }

    private const uint SHERB_NOCONFIRMATION = 0x1;
    private const uint SHERB_NOPROGRESSUI = 0x2;
    private const uint SHERB_NOSOUND = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);
}
