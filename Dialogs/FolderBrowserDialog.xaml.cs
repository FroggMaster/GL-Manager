using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Dialogs;

/// <summary>
/// In-app folder browser. Lists the folders and files of the directory being
/// browsed so the user can confirm they are in the right place, keeps the
/// pending selection visible in an editable path field (paste supported), and
/// confirms with a dedicated "Select this folder" button.
/// </summary>
public partial class FolderBrowserDialog
{
    private const string DriveIconCacheKeyPrefix = "::drive::";
    private const string FolderIconCacheKey = "::folder::";
    private const string NoExtensionIconCacheKey = "::noextension::";
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    private static readonly Dictionary<string, BitmapSource?> _iconCache = new();
    private bool _showingDrives;
    private string _currentDirectory = string.Empty;

    /// <summary>
    /// Creates the dialog and opens it on the initial directory when it exists.
    /// </summary>
    public FolderBrowserDialog(string title, string? initialDirectory)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => PathBox.Focus();

        var start = !string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory)
            ? initialDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start))
            start = AppContext.BaseDirectory;

        NavigateTo(start);
        ShowStatus("Double-click folders to browse, or paste a path and press Enter.", isError: false);
    }

    /// <summary>Folder that will be returned, or null when the dialog was cancelled.</summary>
    public string? SelectedFolder { get; private set; }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        DialogResult = false;
        Close();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        NavigateTo(PathBox.Text);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_showingDrives)
            return;

        try
        {
            var parent = Directory.GetParent(_currentDirectory);
            if (parent != null)
            {
                NavigateTo(parent.FullName);
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Folder browser: unable to resolve parent of '{_currentDirectory}': {ex.Message}");
        }

        ShowDrives();
    }

    private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selecting a folder makes it the pending choice. The path field always
        // mirrors exactly what "Select this folder" will return.
        if (EntriesList.SelectedItem is FolderEntry entry && entry.IsFolder)
            SetPathText(entry.FullPath);
    }

    private void EntriesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntriesList.SelectedItem is not FolderEntry entry)
            return;

        if (entry.IsFolder)
        {
            NavigateTo(entry.FullPath);
            return;
        }

        ShowStatus("Files are listed for reference. Select this folder picks the folder shown in Folder Path.", isError: false, highlight: true);
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        var candidate = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            ShowStatus("Enter or paste a folder path.", isError: true);
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            if (!Directory.Exists(fullPath))
            {
                ShowStatus(File.Exists(fullPath) ? "That path is a file, not a folder." : "Folder not found.", isError: true);
                return;
            }

            SelectedFolder = fullPath;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Folder browser: invalid path '{candidate}': {ex.Message}");
            ShowStatus("That path is not valid.", isError: true);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void NavigateTo(string path)
    {
        if (!TryResolveDirectory(path, out var fullPath))
            return;

        _showingDrives = false;
        _currentDirectory = fullPath;
        SetPathText(fullPath);
        ShowStatus(string.Empty, isError: false);
        BackButton.IsEnabled = true;
        Populate();
    }

    private void ShowDrives()
    {
        _showingDrives = true;
        _currentDirectory = string.Empty;
        SetPathText(string.Empty);
        BackButton.IsEnabled = false;
        ShowStatus("Choose a drive, or paste a folder path and press Enter.", isError: false);
        PathBox.Focus();
        Populate();
    }

    private bool TryResolveDirectory(string path, out string fullPath)
    {
        fullPath = string.Empty;

        try
        {
            var candidate = Path.GetFullPath(path);
            if (!Directory.Exists(candidate))
            {
                ShowStatus(File.Exists(candidate) ? "That path is a file, not a folder." : "Folder not found.", isError: true);
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Folder browser: unable to open '{path}': {ex.Message}");
            ShowStatus("Unable to open that folder.", isError: true);
            return false;
        }
    }

    private void Populate()
    {
        var entries = new List<FolderEntry>();

        try
        {
            if (_showingDrives)
            {
                entries.AddRange(DriveInfo.GetDrives()
                    .Where(drive => drive.IsReady)
                    .OrderBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(drive => new FolderEntry(drive.Name, DescribeDrive(drive), DateTime.MinValue, true, drive.Name)));

                EntriesList.ItemsSource = entries;
                return;
            }

            var directory = new DirectoryInfo(_currentDirectory);
            // IgnoreInaccessible keeps one unreadable subfolder from hiding
            // the rest of the listing.
            var options = new EnumerationOptions { IgnoreInaccessible = true };

            entries.AddRange(directory.EnumerateDirectories("*", options)
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new FolderEntry(entry.Name, "File folder", entry.LastWriteTime, true, entry.FullName)));

            entries.AddRange(directory.EnumerateFiles("*", options)
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new FolderEntry(entry.Name, DescribeFile(entry.Extension), entry.LastWriteTime, false, entry.FullName)));

            EntriesList.ItemsSource = entries;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            Logger.Warn($"Folder browser: unable to list '{_currentDirectory}': {ex.Message}");
            EntriesList.ItemsSource = entries;
            ShowStatus("Unable to list the contents of this folder.", isError: true);
        }
    }

    // No text selection and the caret stays at the start, so the beginning of
    // the path is visible and pasting never wipes the display unexpectedly.
    private void SetPathText(string text)
    {
        PathBox.Text = text;
        PathBox.CaretIndex = 0;
        PathBox.SelectionLength = 0;
    }

    private void ShowStatus(string message, bool isError, bool highlight = false)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, isError ? "Danger" : highlight ? "Accent" : "TextSecondary");
    }

    private static string DescribeFile(string extension)
    {
        var name = extension.TrimStart('.');
        return string.IsNullOrEmpty(name) ? "File" : $"{name.ToUpperInvariant()} file";
    }

    private static string DescribeDrive(DriveInfo drive) => drive.DriveType switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Removable => "Removable Disk",
        DriveType.Network => "Network Drive",
        DriveType.CDRom => "CD Drive",
        _ => drive.DriveType.ToString()
    };

    private static string ResolveIconCacheKey(string fullPath, bool isFolder)
    {
        if (!isFolder)
            return GetExtensionKey(fullPath);

        // Drive roots get their own icon (per letter), so listing drives in
        // the root view cannot overwrite the shared regular folder icon.
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && string.Equals(fullPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return DriveIconCacheKeyPrefix + fullPath.TrimEnd('\\').ToLowerInvariant();

        return FolderIconCacheKey;
    }

    /// <summary>
    /// Resolves the Windows shell icon (like Explorer shows) for the entry.
    /// Folders share one cached icon; files share one icon per extension.
    /// Cached misses stay null so failures are not retried per row.
    /// </summary>
    private static BitmapSource? ResolveIcon(string fullPath, bool isFolder)
    {
        var cacheKey = ResolveIconCacheKey(fullPath, isFolder);
        if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
            return cachedIcon;

        var iconSource = FetchIconSource(fullPath);
        _iconCache[cacheKey] = iconSource;
        return iconSource;
    }

    private static string GetExtensionKey(string fullPath)
    {
        var extension = Path.GetExtension(fullPath);
        return string.IsNullOrEmpty(extension) ? NoExtensionIconCacheKey : extension.ToLowerInvariant();
    }

    private static BitmapSource? FetchIconSource(string fullPath)
    {
        var info = new SHFILEINFOW();

        try
        {
            var result = SHGetFileInfo(fullPath, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFOW)), SHGFI_ICON | SHGFI_LARGEICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var iconInfo = new ICONINFO { fIcon = true };
                if (!GetIconInfo(info.hIcon, ref iconInfo))
                    return null;

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(iconInfo.hbmColor, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                    if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
                }
            }
            finally
            {
                if (info.hIcon != IntPtr.Zero)
                    DestroyIcon(info.hIcon);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Folder browser: unable to load the icon for '{fullPath}': {ex.Message}");
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        uint fileAttributes,
        ref SHFILEINFOW fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, ref ICONINFO iconInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    private sealed class FolderEntry
    {
        public FolderEntry(string name, string type, DateTime modified, bool isFolder, string fullPath)
        {
            Name = name;
            Type = type;
            IsFolder = isFolder;
            FullPath = fullPath;
            ModifiedText = modified == DateTime.MinValue ? string.Empty : modified.ToString("yyyy-MM-dd HH:mm");
            Icon = ResolveIcon(fullPath, isFolder);
        }

        public string Name { get; }
        public string Type { get; }
        public bool IsFolder { get; }
        public string FullPath { get; }
        public string ModifiedText { get; }
        public BitmapSource? Icon { get; }
    }
}
