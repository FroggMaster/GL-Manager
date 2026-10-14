using System.Windows;
using GreenLuma_Manager.Dialogs;

namespace GreenLuma_Manager.Utilities;

/// <summary>
/// Folder picker shown as an in-app browser dialog. It lists the files inside
/// the folder being chosen so the user can confirm they are in the right place,
/// shows the pending folder path in an editable field (paste supported), and
/// confirms with a dedicated "Select this folder" button.
///
/// The native folder dialog (IFileDialog with FOS_PICKFOLDERS) always hides
/// files, and a file-mode dialog cannot both list files and reliably confirm a
/// folder, so the shell dialog cannot satisfy both requirements. This dialog
/// replaces the earlier file-dialog workarounds.
/// </summary>
public static class FolderPicker
{
    /// <summary>
    /// Shows the folder picker. Returns the selected directory path, or null if the user cancelled.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="initialDirectory">Directory to open first, when it exists.</param>
    /// <param name="owner">Window that owns the dialog, for correct modality.</param>
    public static string? Show(string title, string? initialDirectory = null, Window? owner = null)
    {
        var dialog = new FolderBrowserDialog(title, initialDirectory);
        if (owner != null)
            dialog.Owner = owner;

        var confirmed = dialog.ShowDialog();
        return confirmed == true ? dialog.SelectedFolder : null;
    }
}
