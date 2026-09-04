using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;
using GreenLuma_Manager.Utilities;
using Microsoft.Win32;

namespace GreenLuma_Manager.Dialogs;

public partial class SettingsDialog
{
    private readonly Config _config;
    private bool _loaded;
    private bool _syncingMode;
    /// <summary>
    /// True while the GreenLuma path textbox is showing SteamPath as a
    /// display-only indicator (Normal/User32 mode).  RestoreUserPath
    /// will skip restoring when this is false — the textbox has a real
    /// user-entered value.
    /// </summary>
    private bool _inDisplayMode;
    /// <summary>
    /// Remembers the path the user actually typed / browsed to, so it can be
    /// restored when the display is temporarily overwritten (e.g. with SteamPath
    /// for Normal/User32 mode).
    /// </summary>
    private string? _userGreenLumaPath;
    /// <summary>
    /// Snapshot of <see cref="Config.PreferredMode"/> taken at dialog open,
    /// restored on Cancel so the shared <see cref="_config"/> object isn't
    /// left with an unsaved mode change.
    /// </summary>
    private readonly string? _originalPreferredMode;

    public SettingsDialog(Config config, bool openToSystemTab = false)
    {
        InitializeComponent();
        Logger.Debug("SettingsDialog opened");
        _config = config;
        _originalPreferredMode = config.PreferredMode;

        LoadSettings();
        UpdateAutoUpdateVisibility();

        _loaded = true;

        if (openToSystemTab)
            NavSystem.IsChecked = true;

        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void LoadSettings()
    {
        Logger.Debug("Loading settings into dialog UI");
        TxtSteamPath.Text = _config.SteamPath;
        TxtGreenLumaPath.Text = _config.GreenLumaPath;
        ChkReplaceSteamAutostart.IsChecked = _config.ReplaceSteamAutostart;
        ChkPrefetchAppList.IsChecked = _config.PrefetchAppList;
        ChkDisableUpdateCheck.IsChecked = _config.DisableUpdateCheck;
        ChkAutoUpdate.IsChecked = _config.AutoUpdate;
        ChkCheckGreenLumaUpdates.IsChecked = _config.CheckGreenLumaUpdates;

        UpdateGreenLumaPathUI();
        LoadGreenLumaModeSelection();

        // Set credential fields AFTER all other settings so the auto-save
        // handler (which fires on TextChanged/PasswordChanged) won't save
        // a half-loaded state to disk.
        TxtGreenLumaUsername.Text = _config.GreenLumaUsername;
        PwdGreenLumaPassword.Password = _config.GreenLumaPassword;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel_Click(this, new RoutedEventArgs());
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewGeneral == null || ViewSystem == null || ViewAdvanced == null) return;

        ViewGeneral.Visibility = Visibility.Collapsed;
        ViewSystem.Visibility = Visibility.Collapsed;
        ViewAdvanced.Visibility = Visibility.Collapsed;

        if (NavGeneral.IsChecked == true) ShowView(ViewGeneral);
        else if (NavSystem.IsChecked == true) ShowView(ViewSystem);
        else if (NavAdvanced.IsChecked == true) ShowView(ViewAdvanced);
    }

    private void ShowView(UIElement view)
    {
        view.Visibility = Visibility.Visible;
    }

    private void BrowseSteam_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Steam folder"
        };

        if (PathDetector.IsValidDirectory(TxtSteamPath.Text))
            dialog.InitialDirectory = TxtSteamPath.Text;

        if (dialog.ShowDialog() == true)
        {
            Logger.Info($"Steam folder browsed: '{dialog.FolderName}'");
            TxtSteamPath.Text = dialog.FolderName;
            UpdateGreenLumaPathUI();
            CheckMixedInstallState();
        }
    }

    private void BrowseGreenLuma_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select GreenLuma folder"
        };

        if (PathDetector.IsValidDirectory(TxtGreenLumaPath.Text))
            dialog.InitialDirectory = TxtGreenLumaPath.Text;

        if (dialog.ShowDialog() == true)
        {
            Logger.Info($"GreenLuma folder browsed: '{dialog.FolderName}'");
            TxtGreenLumaPath.Text = dialog.FolderName;

            // When the user explicitly browses to a folder, exit display
            // mode so UpdateGreenLumaPathUI doesn't overwrite their choice.
            _inDisplayMode = false;
            _userGreenLumaPath = null;

            UpdateGreenLumaPathUI();
            CheckMixedInstallState();
        }
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var (steamPath, greenLumaPath) = PathDetector.DetectPaths();

        // If standard detection didn't find a distinct StealthAny folder,
        // check if we've seen one before preserves custom paths that
        // aren't in PathDetector's limited set of common locations.
        if (!GreenLumaService.PathsAreDistinct(greenLumaPath, steamPath))
        {
            var last = _config.LastStealthAnyPath?.Trim();
            if (!string.IsNullOrWhiteSpace(last) &&
                GreenLumaService.PathsAreDistinct(last, steamPath) &&
                GreenLumaService.HasStealthAnyFiles(last))
            {
                greenLumaPath = last;
            }
        }

        TxtSteamPath.Text = steamPath;
        TxtGreenLumaPath.Text = greenLumaPath;
        _config.PreferredMode = null;

        // Check for mixed installs BEFORE UpdateGreenLumaPathUI overwrites
        // the GL textbox (Normal mode fills it with SteamPath).  The
        // fallback path above is still in the textbox at this point, so
        // CheckMixedInstallState sees both distinct paths.
        CheckMixedInstallState();
        UpdateGreenLumaPathUI();

        var finalSteamPath = NormalizePath(TxtSteamPath.Text);
        var finalGLPath = NormalizePath(TxtGreenLumaPath.Text);
        var method = GreenLumaService.DetectInstallMethod(
            finalSteamPath, finalGLPath, _config.PreferredMode);

        if (method == GreenLumaInstallMethod.None)
        {
            Logger.Info($"Auto-detect failed (steamPath='{finalSteamPath}', glPath='{finalGLPath}')");
            CustomMessageBox.Show(
                "Could not detect GreenLuma automatically.\n\n" +
                "Make sure GreenLuma is installed in the Steam folder (Normal mode) " +
                "or browse for the folder manually (StealthAny mode).",
                "Detection",
                icon: MessageBoxImage.Exclamation);
        }
        else
        {
            Logger.Info($"Auto-detect succeeded: mode={method}, steamPath='{finalSteamPath}', glPath='{finalGLPath}'");
            var glFolder = !string.IsNullOrWhiteSpace(finalGLPath) &&
                           !string.Equals(finalGLPath.TrimEnd('\\'), finalSteamPath?.TrimEnd('\\'),
                               StringComparison.OrdinalIgnoreCase)
                ? finalGLPath
                : finalSteamPath;

            CustomMessageBox.ShowFormatted("Detection",
            [
                new Run("GreenLuma detected!"),
                new LineBreak(),
                new LineBreak(),
                new Bold(new Run("Mode: ")),
                new Run(method.ToString()),
                new LineBreak(),
                new Bold(new Run("GreenLuma Folder: ")),
                new Run(glFolder)
            ], MessageBoxImage.Asterisk);
        }
    }

    /// <summary>
    /// Updates the description text and tooltip on the GreenLuma Directory field
    /// to reflect the currently detected install method. The field stays interactive
    /// so the user can always switch to a StealthAny setup.
    /// </summary>
    private void UpdateGreenLumaPathUI()
    {
        Logger.Debug("Updating GreenLuma path UI");
        var steamPath = NormalizePath(TxtSteamPath.Text);
        var greenLumaPath = NormalizePath(TxtGreenLumaPath.Text);

        // When the user explicitly selected a mode from the dropdown,
        // honour it for display purposes Normal/User32 shows SteamPath,
        // StealthAny shows the folder they picked.
        var method = GreenLumaInstallMethod.None;

        if (!string.IsNullOrWhiteSpace(_config.PreferredMode))
        {
            Enum.TryParse(_config.PreferredMode, ignoreCase: true, out method);
        }

        if (method == GreenLumaInstallMethod.None)
        {
            _inDisplayMode = false;
            _userGreenLumaPath = null;

            TxtGreenLumaDesc.Text = "\"The GreenLuma directory is auto-detected. If the detected location changes, this path will be updated after you save your settings.\"";
            TxtGreenLumaPath.ToolTip = null;

            PanelGreenLumaDir.Opacity = GreenLumaService.PathsAreDistinct(_config.GreenLumaPath, steamPath)
                ? 1.0
                : 0.45;

            LoadGreenLumaModeSelection();
            return;
        }

        // Explicit mode (Normal / User32 / StealthAny selected from dropdown):
        // update path display so the user sees the consequence of their choice.
        switch (method)
        {
            case GreenLumaInstallMethod.User32:
                TxtGreenLumaDesc.Text = "GreenLuma is loaded via user32.dll in the Steam folder. Browse to a separate folder to switch to StealthAny mode.";
                TxtGreenLumaPath.ToolTip =
                    "GreenLuma is loaded via user32.dll in the Steam folder. " +
                    "Select a different folder to set up a StealthAny installation.";
                FillDisplayPath(steamPath);
                break;

            case GreenLumaInstallMethod.Normal:
                TxtGreenLumaDesc.Text = "GreenLuma is installed directly in the Steam folder. Browse to a separate folder to switch to StealthAny mode.";
                TxtGreenLumaPath.ToolTip =
                    "GreenLuma is installed directly in the Steam folder (Normal mode). " +
                    "Select a different folder to set up a StealthAny installation.";
                FillDisplayPath(steamPath);
                break;

            default: // StealthAny, None
                TxtGreenLumaDesc.Text = "The folder where DLLInjector.exe and GreenLuma files are located.";
                TxtGreenLumaPath.ToolTip = null;
                RestoreUserPath(greenLumaPath);
                break;
        }

        // Dim the GL directory section for Normal/User32 modes (where GL
        // lives inside the Steam folder) but keep it fully active when the
        // user explicitly chose StealthAny.
        PanelGreenLumaDir.Opacity = method == GreenLumaInstallMethod.StealthAny
            ? 1.0
            : 0.45;

        LoadGreenLumaModeSelection();
    }

    /// <summary>
    /// Stores the user's real GL path (if not already captured) then fills
    /// the textbox with <paramref name="displayPath"/> so the user can see
    /// where the files live for the current mode.
    /// </summary>
    private void FillDisplayPath(string displayPath)
    {
        _inDisplayMode = true;

        // First time switching to a mode that fills the path — capture what
        // the user had entered so we can restore it later.
        if (_userGreenLumaPath == null)
        {
            var current = NormalizePath(TxtGreenLumaPath.Text);
            _userGreenLumaPath = string.IsNullOrWhiteSpace(current) ? null : current;
        }

        TxtGreenLumaPath.Text = displayPath;
    }

    /// <summary>
    /// Restores the GreenLuma path textbox after leaving a display-only mode
    /// (Normal/User32).  Uses the captured user path if one was set, otherwise
    /// falls back to the config value or empty.  When not in display mode the
    /// current textbox value is kept as-is (it was set directly by the user).
    /// </summary>
    private void RestoreUserPath(string currentPath)
    {
        if (!_inDisplayMode)
            return;

        var steamPath = NormalizePath(TxtSteamPath.Text);

        // 1. User's explicit path captured before display mode took over
        if (_userGreenLumaPath != null &&
            GreenLumaService.PathsAreDistinct(_userGreenLumaPath, steamPath))
        {
            TxtGreenLumaPath.Text = _userGreenLumaPath;
        }
        // 2. Saved config path that actually points to a distinct GL folder
        else if (!string.IsNullOrWhiteSpace(_config.GreenLumaPath) &&
                 GreenLumaService.PathsAreDistinct(_config.GreenLumaPath, steamPath))
        {
            TxtGreenLumaPath.Text = _config.GreenLumaPath;
        }
        // 3. Last known StealthAny path (remembered for auto-detect fallback)
        else if (!string.IsNullOrWhiteSpace(_config.LastStealthAnyPath) &&
                 GreenLumaService.PathsAreDistinct(_config.LastStealthAnyPath, steamPath))
        {
            TxtGreenLumaPath.Text = _config.LastStealthAnyPath;
        }
        // 4. Nothing useful clear the field so the user can pick a path
        else
        {
            TxtGreenLumaPath.Text = string.Empty;
        }

        _inDisplayMode = false;
        _userGreenLumaPath = null;
    }

    /// <summary>
    /// Synchronises the mode ComboBox with <see cref="Config.PreferredMode"/>.
    /// Safe to call while <c>_loaded</c> is still false (the SelectionChanged
    /// handler will exit early during initialisation).
    /// </summary>
    private void LoadGreenLumaModeSelection()
    {
        if (_syncingMode) return;
        _syncingMode = true;

        try
        {
            if (string.IsNullOrWhiteSpace(_config.PreferredMode))
            {
                // Select the "Auto-Detect" item (empty Tag)
                foreach (ComboBoxItem item in CmbGreenLumaMode.Items)
                {
                    if (string.IsNullOrEmpty(item.Tag as string))
                    {
                        item.IsSelected = true;
                        return;
                    }
                }
                CmbGreenLumaMode.SelectedIndex = -1;
                return;
            }

            foreach (ComboBoxItem item in CmbGreenLumaMode.Items)
            {
                if (string.Equals(item.Tag as string, _config.PreferredMode, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                    return;
                }
            }

            // Saved preference no longer available → nothing selected
            CmbGreenLumaMode.SelectedIndex = -1;
        }
        finally
        {
            _syncingMode = false;
        }
    }

    private void GreenLumaMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _syncingMode) return;

        if (CmbGreenLumaMode.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag as string;
            _config.PreferredMode = string.IsNullOrEmpty(tag) ? null : tag;

            var steamPath = NormalizePath(TxtSteamPath.Text);

            // When the user explicitly picks Normal or StealthAny, disable
            // User32 if it exists — otherwise it auto-injects and overrides
            // the chosen mode on next Steam launch.
            if (!string.IsNullOrEmpty(tag) &&
                !string.Equals(tag, GreenLumaInstallMethod.User32.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(steamPath) &&
                    File.Exists(Path.Combine(steamPath, "user32.dll")))
                {
                    GreenLumaService.DisableUser32(steamPath);
                }
            }

            // NOTE: Config is intentionally NOT saved here.  The mode change
            // stays in memory so the UI can preview what would happen, but
            // nothing is persisted until Ok_Click confirms the dialog.

            // Directly set the GL path based on the selected mode instead of
            // relying on UpdateGreenLumaPathUI's detection-based logic.  This
            // guarantees Normal/User32 → SteamPath, StealthAny → saved path.
            if (string.IsNullOrEmpty(tag))
            {
                // Auto-Detect: reset display-mode tracking before detection
                // so stale flags from a previous explicit mode don't leak
                // into the save path.
                _inDisplayMode = false;
                _userGreenLumaPath = null;
                UpdateGreenLumaPathUI();
            }
            else if (tag.Equals(GreenLumaInstallMethod.StealthAny.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                TxtGreenLumaDesc.Text = "The folder where DLLInjector.exe and GreenLuma files are located.";
                TxtGreenLumaPath.ToolTip = null;
                PanelGreenLumaDir.Opacity = 1.0;

                // Ensure RestoreUserPath can run.  When coming from
                // Auto-Detect, _inDisplayMode is false (the Auto-Detect
                // branch never calls FillDisplayPath), causing RestoreUserPath
                // to bail early and leave the textbox unchanged.
                _inDisplayMode = true;
                RestoreUserPath(NormalizePath(TxtGreenLumaPath.Text));
            }
            else
            {
                // Normal or User32: show SteamPath
                var isUser32 = tag.Equals(GreenLumaInstallMethod.User32.ToString(), StringComparison.OrdinalIgnoreCase);
                TxtGreenLumaDesc.Text = isUser32
                    ? "GreenLuma is loaded via user32.dll in the Steam folder. Browse to a separate folder to switch to StealthAny mode."
                    : "GreenLuma is installed directly in the Steam folder. Browse to a separate folder to switch to StealthAny mode.";
                TxtGreenLumaPath.ToolTip = isUser32
                    ? "GreenLuma is loaded via user32.dll in the Steam folder. Select a different folder to set up a StealthAny installation."
                    : "GreenLuma is installed directly in the Steam folder (Normal mode). Select a different folder to set up a StealthAny installation.";
                PanelGreenLumaDir.Opacity = 0.45;
                FillDisplayPath(steamPath);
            }
        }
    }

    /// <summary>
    /// When multiple GreenLuma installations are detected (e.g. User32 + Normal,
    /// Normal + StealthAny, User32 + StealthAny), prompts the user to pick one.
    /// The choice is only persisted if the user checks "Remember my preference"
    /// in the prompt.  The explicit dropdown selection in Settings is the other
    /// way to permanently save a preference.
    /// </summary>
    private void CheckMixedInstallState()
    {
        var steamPath = NormalizePath(TxtSteamPath.Text);
        var greenLumaPath = NormalizePath(TxtGreenLumaPath.Text);

        // Capture the saved preference before the prompt so we can restore
        // it after a session-only choice — the ComboBox should show what's
        // actually persisted, not the in-memory choice.
        var savedMode = _config.PreferredMode;

        if (GreenLumaService.CheckMixedInstallState(
                steamPath, greenLumaPath, _config, out var persisted, persistPreference: false))
        {
            Logger.Warn($"Mixed install state detected (steamPath='{steamPath}', glPath='{greenLumaPath}')");
            // Update display while the chosen mode is still in PreferredMode,
            // so FillDisplayPath/RestoreUserPath reflect the user's pick.
            UpdateGreenLumaPathUI();

            // Session-only choice: restore the saved preference so the
            // ComboBox reflects what's on disk, not the in-memory choice.
            if (!persisted)
            {
                _config.PreferredMode = savedMode;
                LoadGreenLumaModeSelection();
            }
        }
    }

    private void GreenLumaCredential_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _config.GreenLumaUsername = TxtGreenLumaUsername.Text;
        _config.GreenLumaPassword = PwdGreenLumaPassword.Password;
        ConfigService.Save(_config);
    }

    private void DisableUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAutoUpdateVisibility();
    }

    private void UpdateAutoUpdateVisibility()
    {
        if (ChkAutoUpdate == null || ChkDisableUpdateCheck == null)
            return;

        var isEnabled = !ChkDisableUpdateCheck.IsChecked.GetValueOrDefault();
        ChkAutoUpdate.IsEnabled = isEnabled;

        if (!isEnabled) ChkAutoUpdate.IsChecked = false;
    }

    private void WipeData_Click(object sender, RoutedEventArgs e)
    {
        if (CustomMessageBox.Show("This will delete all profiles and settings. Continue?", "Wipe Data",
                MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
            return;

        if (CustomMessageBox.Show("Are you absolutely sure? This cannot be undone.", "Confirm Wipe",
                MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
            return;

        ConfigService.WipeData();
        CustomMessageBox.Show("All data has been wiped. The application will now close.", "Complete",
            icon: MessageBoxImage.Asterisk);
        Application.Current.Shutdown();
    }

    private async void CheckGreenLumaUpdates_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckGreenLumaUpdates.IsEnabled = false;
        BtnCheckGreenLumaUpdates.Content = "Checking...";

        try
        {
            var result = await GreenLumaUpdateService.CheckForGreenLumaUpdatesAsync(_config);

            if (result == null || !result.CheckSucceeded)
            {
                CustomMessageBox.Show(
                    "Could not check GreenLuma version. The forum may be unreachable.",
                    "GreenLuma Update",
                    icon: MessageBoxImage.Exclamation);
            }
            else if (result.UpdateAvailable)
            {
                CustomMessageBox.Show(
                    $"GreenLuma v{result.LatestSemanticVersion} is available. You have v{result.InstalledVersion}. Visit cs.rin.ru to download.",
                    "GreenLuma Update",
                    icon: MessageBoxImage.Asterisk);
            }
            else
            {
                CustomMessageBox.Show(
                    $"GreenLuma is up to date. (v{result.InstalledVersion} installed)",
                    "GreenLuma Update",
                    icon: MessageBoxImage.Asterisk);
            }
        }
        catch
        {
            CustomMessageBox.Show(
                "Could not check GreenLuma version. The forum may be unreachable.",
                "GreenLuma Update",
                icon: MessageBoxImage.Exclamation);
        }
        finally
        {
            BtnCheckGreenLumaUpdates.IsEnabled = true;
            BtnCheckGreenLumaUpdates.Content = "Check Now";
        }
    }

    private async void DownloadGreenLuma_Click(object sender, RoutedEventArgs e)
    {
        BtnDownloadGreenLuma.IsEnabled = false;
        PnlDownloadProgress.Visibility = Visibility.Visible;

        // Tracks overall progress across all phases (0.0 to 1.0)
        IProgress<double> overallProgress = new Progress<double>(pct =>
        {
            DownloadProgressBar.Value = Math.Min(pct * 100, 100);
        });

        void SetStatus(string status)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => TxtDownloadStatus.Text = status);
                return;
            }
            TxtDownloadStatus.Text = status;
        }

        try
        {
            // Read credentials from config (required for all paths)
            var username = _config.GreenLumaUsername;
            var password = _config.GreenLumaPassword;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                CustomMessageBox.Show(
                    "Please enter your cs.rin.ru username and password in Settings first.",
                    "Credentials Required",
                    icon: MessageBoxImage.Exclamation);
                return;
            }

            var steamPath = NormalizePath(_config.SteamPath);
            var greenLumaPath = NormalizePath(_config.GreenLumaPath);
            GreenLumaInstallMethod? forcedMethod = null;
            User32DeployMode? user32DeployMode = null;
            var hadExistingInstall = false;

            // Check if an existing installation is detected
            var installMethod = GreenLumaService.DetectInstallMethod(
                steamPath, greenLumaPath, _config.PreferredMode);

            if (installMethod != GreenLumaInstallMethod.None)
            {
                hadExistingInstall = true;

                // Existing install detected — ask if user wants to update it
                var updateChoice = CustomMessageBox.Show(
                    "Existing installation detected.\n\n" +
                    "Update and deploy to the current installation?",
                    "Installation Detected",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (updateChoice != MessageBoxResult.Yes)
                {
                    // User chose to pick a different install method instead
                    installMethod = GreenLumaInstallMethod.None;
                }
            }

            if (installMethod == GreenLumaInstallMethod.None)
            {
                // No install detected (or user declined to update) —
                // prompt user to choose a deployment method
                var choice = CustomMessageBox.Show(
                    hadExistingInstall
                        ? "Choose how you want to deploy:"
                        : "No GreenLuma installation detected.\n\nChoose how you want to deploy:",
                    "Install GreenLuma",
                    "Normal", "User32", "StealthAny");

                switch (choice)
                {
                    case 0: // Normal
                        if (string.IsNullOrWhiteSpace(steamPath) ||
                            !File.Exists(Path.Combine(steamPath, "Steam.exe")))
                        {
                            CustomMessageBox.Show(
                                "Steam path is not set or invalid.\nPlease configure a valid Steam path in Settings first.",
                                "Validation",
                                icon: MessageBoxImage.Exclamation);
                            return;
                        }
                        greenLumaPath = steamPath;
                        forcedMethod = GreenLumaInstallMethod.Normal;
                        break;

                    case 1: // User32
                        if (string.IsNullOrWhiteSpace(steamPath) ||
                            !File.Exists(Path.Combine(steamPath, "Steam.exe")))
                        {
                            CustomMessageBox.Show(
                                "Steam path is not set or invalid.\nPlease configure a valid Steam path in Settings first.",
                                "Validation",
                                icon: MessageBoxImage.Exclamation);
                            return;
                        }

                        // Ask user which User32 variant to deploy
                        var user32Choice = CustomMessageBox.Show(
                            "Choose the User32 deployment variant:\n\n" +
                            "User32 — deploys user32.dll directly from the archive.\n" +
                            "User32SF (Recommended) — deploys user32SF.dll renamed to user32.dll.",
                            "User32 Deployment",
                            "User32", "User32SF (Recommended)");

                        user32DeployMode = user32Choice == 1
                            ? User32DeployMode.User32SF
                            : User32DeployMode.User32;

                        greenLumaPath = steamPath;
                        forcedMethod = GreenLumaInstallMethod.User32;
                        break;

                    case 2: // StealthAny
                        var folderDialog = new OpenFolderDialog
                        {
                            Title = "Select GreenLuma installation folder"
                        };
                        if (folderDialog.ShowDialog() != true)
                            return; // User cancelled

                        greenLumaPath = folderDialog.FolderName;
                        _config.LastStealthAnyPath = greenLumaPath;
                        forcedMethod = GreenLumaInstallMethod.StealthAny;
                        break;
                }

                // Sync both the config AND the UI text box so Ok_Click's
                // validation (which reads from the text box) sees the new path.
                _config.PreferredMode = forcedMethod.ToString();
                TxtGreenLumaPath.Text = greenLumaPath;

                // Reset display-mode tracking so UpdateGreenLumaPathUI
                // starts fresh — it will set _inDisplayMode / _userGreenLumaPath
                // correctly based on the chosen mode.
                _inDisplayMode = false;
                _userGreenLumaPath = null;
                UpdateGreenLumaPathUI();
                LoadGreenLumaModeSelection();
            }

            // Validate target path — for Normal/User32 mode the GL path should
            // fall back to the Steam directory when the config field is empty.
            if (!PathDetector.IsValidDirectory(greenLumaPath))
            {
                if (PathDetector.IsValidDirectory(steamPath) &&
                    (installMethod == GreenLumaInstallMethod.Normal ||
                     installMethod == GreenLumaInstallMethod.User32))
                {
                    greenLumaPath = steamPath;
                }
                else
                {
                    CustomMessageBox.Show(
                        "Target directory does not exist or is invalid:\n" + greenLumaPath,
                        "Download Error",
                        icon: MessageBoxImage.Exclamation);
                    return;
                }
            }

            // ── Phase 1: Version check (0% → 10%) ─────────────────
            SetStatus("Checking for updates...");
            overallProgress.Report(0.05);

            // We temporarily enable the setting so the check runs even if the
            // user has auto-checking disabled; the original value is restored
            // immediately after since we only need the version comparison.
            var originalCheckSetting = _config.CheckGreenLumaUpdates;
            _config.CheckGreenLumaUpdates = true;
            var versionInfo = await GreenLumaUpdateService.CheckForGreenLumaUpdatesAsync(_config);
            _config.CheckGreenLumaUpdates = originalCheckSetting;

            overallProgress.Report(0.10);

            var (installedVersion, _) = GreenLumaService.DetectInstalledVersion(greenLumaPath);
            var latestVersion = versionInfo?.LatestSemanticVersion;

            if (installedVersion != null && latestVersion != null && installedVersion >= latestVersion)
            {
                var confirm = CustomMessageBox.Show(
                    $"GreenLuma v{latestVersion} is already installed.\n\n" +
                    "Do you want to download and deploy anyway?",
                    "Already Up to Date",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Asterisk);

                if (confirm != MessageBoxResult.Yes)
                    return;
            }
            else if (versionInfo == null || !versionInfo.CheckSucceeded)
            {
                // Couldn't determine the latest version — proceed with download anyway
            }

            // ── Phase 2: Download (10% → 85%) ─────────────────────
            var downloadFolder = Path.Combine(greenLumaPath, "TMP");
            Directory.CreateDirectory(downloadFolder);

            // Download service reports 0.0-1.0 internally → mapped to 0.10-0.85
            IProgress<double> downloadProgress = new Progress<double>(pct =>
            {
                overallProgress.Report(0.10 + pct * 0.75);
            });

            var result = await GreenLumaDownloadService.DownloadLatestGreenLumaAsync(
                username,
                password,
                downloadFolder,
                _config,
                statusCallback: SetStatus,
                progressCallback: downloadProgress);

            if (result.Success)
            {
                overallProgress.Report(0.85);

                // ── Phase 3: Deploy (85% → 100%) ──────────────────
                SetStatus("Extracting and deploying...");

                // Deploy service reports 0.0-1.0 internally → mapped to 0.85-1.0
                IProgress<double> deployProgress = new Progress<double>(pct =>
                {
                    overallProgress.Report(0.85 + pct * 0.15);
                });

                var deployResult = await Task.Run(async () =>
                    await GreenLumaDeploymentService.DeployAsync(
                        result.FilePath!,
                        greenLumaPath,
                        steamPath,
                        forcedMethod: forcedMethod,
                        user32DeployMode: user32DeployMode,
                        progressCallback: SetStatus,
                        numericProgress: deployProgress));

                if (deployResult.Success)
                {
                    overallProgress.Report(1.0);
                    TxtDownloadStatus.Text = "Complete!";

                    var fileList = deployResult.DeployedFiles.Count > 0
                        ? "\n" + string.Join("\n", deployResult.DeployedFiles.Select(f => $"  • {f}"))
                        : "";
                    var summary = $"GreenLuma updated successfully!\n\nDownloaded: {result.FileName}";
                    if (fileList.Length > 0)
                        summary += $"\nFiles updated:{fileList}";
                    if (deployResult.User32Deployed)
                        summary += "\n  • user32.dll (Steam folder)";

                    CustomMessageBox.Show(summary, "Update Complete", icon: MessageBoxImage.Asterisk);
                }
                else
                {
                    CustomMessageBox.Show(
                        $"Download succeeded but deployment failed:\n{deployResult.ErrorMessage}",
                        "Deployment Error",
                        icon: MessageBoxImage.Exclamation);
                }
            }
            else
            {
                CustomMessageBox.Show(
                    $"Download failed: {result.ErrorMessage}",
                    "Download Error",
                    icon: MessageBoxImage.Exclamation);
            }
        }
        catch (Exception ex)
        {
            CustomMessageBox.Show(
                $"Download error: {ex.Message}",
                "Download Error",
                icon: MessageBoxImage.Exclamation);
        }
        finally
        {
            BtnDownloadGreenLuma.IsEnabled = true;
            PnlDownloadProgress.Visibility = Visibility.Collapsed;
            DownloadProgressBar.Value = 0;
            TxtDownloadStatus.Text = "Starting...";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Logger.Debug("Settings dialog cancelled");
        // Restore the original PreferredMode so the shared _config object
        // isn't left with an unsaved mode change after Cancel.
        _config.PreferredMode = _originalPreferredMode;
        DialogResult = false;
        Close();
    }

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().TrimEnd('\\', '/');
    }

    private static bool ValidatePaths(string steamPath, string greenLumaPath, string? preferredMode = null)
    {
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            CustomMessageBox.Show("Steam path cannot be empty.", "Validation", icon: MessageBoxImage.Exclamation);
            return false;
        }

        if (!Directory.Exists(steamPath))
        {
            CustomMessageBox.Show("Steam path does not exist.", "Validation", icon: MessageBoxImage.Exclamation);
            return false;
        }

        var steamExePath = Path.Combine(steamPath, "Steam.exe");
        if (!File.Exists(steamExePath))
        {
            CustomMessageBox.Show($"Steam.exe not found at:\n{steamExePath}", "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        // When the user has an explicit preference, honour it first.
        // This prevents disk-detection from bypassing the GL path check
        // when, for example, user32.dll still exists on disk but the
        // user has already chosen StealthAny mode.
        if (!string.IsNullOrWhiteSpace(preferredMode))
        {
            if (string.Equals(preferredMode, GreenLumaInstallMethod.User32.ToString(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(preferredMode, GreenLumaInstallMethod.Normal.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // PreferredMode is StealthAny → GL path is required
            if (string.IsNullOrWhiteSpace(greenLumaPath))
            {
                CustomMessageBox.Show("GreenLuma path cannot be empty when StealthAny mode is selected.",
                    "Validation", icon: MessageBoxImage.Exclamation);
                return false;
            }

            if (!Directory.Exists(greenLumaPath))
            {
                CustomMessageBox.Show($"GreenLuma path does not exist:\n{greenLumaPath}", "Validation",
                    icon: MessageBoxImage.Exclamation);
                return false;
            }

            // Skip the Steam-dir warning for StealthAny (the path is already different)
            return true;
        }

        // No explicit preference → fall back to disk detection
        // User32 mode: user32.dll in Steam dir → no separate GL path needed
        if (File.Exists(Path.Combine(steamPath, "user32.dll")))
            return true;

        // Normal mode: GL files in Steam dir → no separate GL path needed
        if (File.Exists(Path.Combine(steamPath, "DLLInjector.exe")) && GreenLumaService.HasGreenLumaDll(steamPath))
            return true;

        // For DLLInjector-based installs, GL path is required
        if (string.IsNullOrWhiteSpace(greenLumaPath))
        {
            CustomMessageBox.Show("GreenLuma path cannot be empty.", "Validation", icon: MessageBoxImage.Exclamation);
            return false;
        }

        if (!Directory.Exists(greenLumaPath))
        {
            CustomMessageBox.Show($"GreenLuma path does not exist:\n{greenLumaPath}", "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        if (string.Equals(Path.GetFullPath(steamPath), Path.GetFullPath(greenLumaPath),
                StringComparison.OrdinalIgnoreCase))
        {
            var result = CustomMessageBox.Show(
                "Installing GreenLuma in the Steam directory is not recommended. Some games scan this location for GreenLuma files, which may result in detection.\n\n" +
                "Do you want to continue anyway?",
                "Security Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return false;
        }

        if (IsPathReadOnly(greenLumaPath))
        {
            CustomMessageBox.Show(
                $"The GreenLuma path is read-only.\nPlease ensure the folder is writable and not marked as Read-Only.\nPath: {greenLumaPath}",
                "Validation",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        var method = GreenLumaService.DetectInstallMethod(steamPath, greenLumaPath);
        if (method == GreenLumaInstallMethod.None)
        {
            CustomMessageBox.Show(
                "No GreenLuma installation detected at the specified path.\n\n" +
                "Ensure the folder contains GreenLuma DLL and DLLInjector.exe, " +
                "or place user32.dll in the Steam directory for User32 mode.",
                "Detection",
                icon: MessageBoxImage.Exclamation);
            return false;
        }

        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var steamPath = NormalizePath(TxtSteamPath.Text);
        var greenLumaPath = NormalizePath(TxtGreenLumaPath.Text);

        if (!ValidatePaths(steamPath, greenLumaPath, _config.PreferredMode)) return;

        var method = GreenLumaService.DetectInstallMethod(steamPath, greenLumaPath);
        if (method == GreenLumaInstallMethod.StealthAny)
        {
            CustomMessageBox.Show(
                "Only files required for Stealth Mode were detected.\n" +
                "The application will be locked to Stealth Mode.\n\n" +
                "Warning: Some GreenLuma features may not work without the full installation.",
                "Stealth Mode Only",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            _config.NoHook = true;
        }

        _config.SteamPath = steamPath;

        if (string.IsNullOrWhiteSpace(_config.PreferredMode))
        {
            // Auto-Detect mode: save the correct path corresponding to the
            // mode the user is actually using.  If multiple installations
            // exist (e.g. Normal + StealthAny), prompt the user to pick one
            // rather than silently picking the highest-priority mode — that
            // would overwrite a StealthAny path with steamPath and silently
            // switch the user out of their chosen mode on Save.

            // Resolve the effective GL path for path-assignment after the
            // prompt (CheckMixedInstallState resolves it internally too).
            var effectiveGLPath = GreenLumaService.ResolveGreenLumaPath(
                greenLumaPath, steamPath, _config.LastStealthAnyPath);

            var savedMode = _config.PreferredMode;
            var wasMixed = GreenLumaService.CheckMixedInstallState(
                steamPath, greenLumaPath, _config, out var persisted,
                persistPreference: false);

            if (wasMixed && !string.IsNullOrWhiteSpace(_config.PreferredMode))
            {
                // User chose a mode in the prompt — save the corresponding path.
                var chosen = (GreenLumaInstallMethod)Enum.Parse(
                    typeof(GreenLumaInstallMethod), _config.PreferredMode);
                _config.GreenLumaPath = chosen is GreenLumaInstallMethod.Normal
                                                            or GreenLumaInstallMethod.User32
                    ? steamPath
                    : effectiveGLPath;

                // Session-only choice: restore PreferredMode so the
                // ComboBox stays on Auto-Detect next time the dialog opens.
                if (!persisted)
                    _config.PreferredMode = savedMode;
            }
            else
            {
                // Single installation — detect automatically.
                var detected = GreenLumaService.DetectInstallMethod(steamPath, effectiveGLPath);
                _config.GreenLumaPath = detected is GreenLumaInstallMethod.Normal
                                                            or GreenLumaInstallMethod.User32
                    ? steamPath
                    : effectiveGLPath;
            }
        }
        else
        {
            // In display-only mode (Normal/User32) the textbox shows SteamPath.
            // Save the captured user path if one was set, otherwise use the
            // current textbox value (which is the user's real path when not
            // in display mode).
            _config.GreenLumaPath = _userGreenLumaPath ?? greenLumaPath;
        }

        // Remember this path for future Auto-Detect runs, so a custom
        // StealthAny folder isn't lost when Normal mode overwrites the
        // main GreenLumaPath field on the next display-mode save.
        if (!_inDisplayMode &&
            GreenLumaService.PathsAreDistinct(_config.GreenLumaPath, _config.SteamPath) &&
            GreenLumaService.HasStealthAnyFiles(_config.GreenLumaPath))
        {
            _config.LastStealthAnyPath = _config.GreenLumaPath;
        }

        _config.ReplaceSteamAutostart = ChkReplaceSteamAutostart.IsChecked.GetValueOrDefault();
        _config.PrefetchAppList = ChkPrefetchAppList.IsChecked.GetValueOrDefault();
        _config.DisableUpdateCheck = ChkDisableUpdateCheck.IsChecked.GetValueOrDefault();
        _config.AutoUpdate = ChkAutoUpdate.IsChecked.GetValueOrDefault();
        _config.CheckGreenLumaUpdates = ChkCheckGreenLumaUpdates.IsChecked.GetValueOrDefault();
        _config.GreenLumaUsername = TxtGreenLumaUsername.Text.Trim();
        _config.GreenLumaPassword = PwdGreenLumaPassword.Password;

        ConfigService.Save(_config);
        AutostartManager.ManageAutostart(_config.ReplaceSteamAutostart, _config);

        Logger.Info($"Settings saved (steamPath='{_config.SteamPath}', mode='{_config.PreferredMode ?? "auto"}')");
        DialogResult = true;
        Close();
    }

    private static bool IsPathReadOnly(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                return true;

            var tempFile = Path.Combine(path, Path.GetRandomFileName());
            using (File.Create(tempFile, 1, FileOptions.DeleteOnClose))
            {
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

}