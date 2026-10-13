using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace GreenLuma_Manager.Utilities;

/// <summary>
/// Directory picker built on the modern Windows common item dialog so it keeps
/// full Explorer-style navigation (address bar, back/forward, breadcrumbs) while
/// still listing files, which lets the user verify they are choosing the correct
/// Steam/GreenLuma folder.
///
/// The dedicated folder picker (IFileDialog with FOS_PICKFOLDERS, surfaced as
/// <see cref="OpenFolderDialog"/>) cannot display files, and the legacy
/// SHBrowseForFolder can display files but has no address bar or back navigation.
/// This uses the standard technique of opening a directory through the file
/// dialog: the file name box is pre-filled with a placeholder, the user navigates
/// into the wanted folder and confirms, and the containing directory is returned.
/// </summary>
public static class FolderPicker
{
    private const string FolderPlaceholder = "Select this folder";

    /// <summary>
    /// Shows a picker that supports full filesystem navigation and also displays
    /// files. Returns the selected directory path, or null if the user cancelled.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="initialDirectory">Directory to open first, when it exists.</param>
    /// <param name="owner">Window that owns the dialog, for correct modality.</param>
    public static string? Show(string title, string? initialDirectory = null, Window? owner = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            // Allow confirming with the placeholder / a directory in the name box.
            ValidateNames = false,
            CheckFileExists = false,
            CheckPathExists = true,
            AddExtension = false,
            DereferenceLinks = false,
            Filter = "All files (*.*)|*.*",
            FilterIndex = 1,
            FileName = FolderPlaceholder
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        var confirmed = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (confirmed != true)
            return null;

        var path = dialog.FileName;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // The user may have navigated into the target folder (leaving the
        // placeholder name), typed a directory path, or clicked a file. Resolve
        // whatever the dialog returned to a directory.
        if (Directory.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : null;
    }
}
