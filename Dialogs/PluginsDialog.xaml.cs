using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using Microsoft.Win32;

namespace GreenLuma_Manager.Dialogs;

public partial class PluginsDialog
{
    public PluginsDialog()
    {
        InitializeComponent();
        LoadPlugins();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void LoadPlugins()
    {
        var plugins = PluginService.GetAllPlugins();
        Logger.Info($"Loaded {plugins.Count} plugin(s)");
        LstPlugins.ItemsSource = plugins;

        PnlEmptyPlugins.Visibility = plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Plugin DLL",
            Filter = "Plugin Files (*.dll)|*.dll|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        var error = PluginService.ImportPlugin(dialog.FileName);
        if (!string.IsNullOrEmpty(error))
        {
            Logger.Warn($"Plugin import failed: '{dialog.FileName}' - {error}");
            CustomMessageBox.Show(error, "Import Failed", icon: MessageBoxImage.Error);
            return;
        }

        Logger.Info($"Plugin imported: '{dialog.FileName}'");
        CustomMessageBox.Show(
            "Plugin imported successfully. Restart the application to load the plugin.",
            "Success",
            icon: MessageBoxImage.Asterisk);

        LoadPlugins();

        if (Owner is MainWindow mainWindow)
            mainWindow.UpdatePluginButtons();
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not PluginInfo plugin) return;

        var result = CustomMessageBox.Show(
            $"Remove plugin '{plugin.Name}'?\n\nThis will delete the plugin file and cannot be undone.",
            "Confirm Remove",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        PluginService.RemovePlugin(plugin);
        Logger.Info($"Plugin removed: '{plugin.Name}'");
        LoadPlugins();

        CustomMessageBox.Show("Plugin removed successfully.", "Success", icon: MessageBoxImage.Asterisk);

        if (Owner is MainWindow mainWindow)
            mainWindow.UpdatePluginButtons();
    }

    private void PluginToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || toggle.Tag is not PluginInfo plugin)
            return;

        var enabled = toggle.IsChecked.GetValueOrDefault();
        PluginService.TogglePlugin(plugin, enabled);

        Logger.Info($"Plugin '{plugin.Name}' {(enabled ? "enabled" : "disabled")}");

        if (enabled)
            CustomMessageBox.Show(
                $"Plugin '{plugin.Name}' enabled. Restart the application to load the plugin.",
                "Plugin Enabled",
                icon: MessageBoxImage.Asterisk);
        else
            CustomMessageBox.Show(
                $"Plugin '{plugin.Name}' disabled. Restart the application to unload the plugin.",
                "Plugin Disabled",
                icon: MessageBoxImage.Asterisk);

        if (Owner is MainWindow mainWindow)
            mainWindow.UpdatePluginButtons();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}