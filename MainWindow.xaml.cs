using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Reflection;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GreenLuma_Manager.Controllers;
using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Plugins;
using GreenLuma_Manager.Services;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager;

public partial class MainWindow
{
    /// <summary>
    /// Reads the assembly version from the auto-generated
    /// <see cref="AssemblyInformationalVersionAttribute"/>, which is sourced
    /// from the <c>Version</c> element in the project file at build
    /// time.  Falls back to <c>"0.0.0"</c> if the attribute is missing.
    /// Strips any git commit hash suffix that .NET SDK may append.
    /// </summary>
    public static string Version
    {
        get
        {
            var raw = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "0.0.0";
            var plus = raw.IndexOf('+');
            return plus > 0 ? raw[..plus] : raw;
        }
    }

    // Controllers
    private readonly SearchController _searchController;
    private readonly ProfileController _profileController;
    private readonly GameListController _gameListController;
    private readonly AppListController _appListController;
    private readonly GreenLumaLauncher _launcher;
    private readonly NotificationManager _notificationManager;

    // UI state
    private readonly ObservableCollection<string> _profiles;
    private Config? _config;
    private CancellationTokenSource? _profileLoadCts;
    private GreenLumaVersionInfo? _lastGreenLumaVersion;
    /// <summary>
    /// Tracks the mode the user picked at a session-only prompt (no "Remember"
    /// checkbox).  Used by <see cref="UpdateStatus"/> so the status reflects
    /// the session choice without polluting <c>_config.PreferredMode</c> —
    /// which would otherwise leak to disk through SettingsDialog.Ok_Click.
    /// Cleared whenever the config is reloaded from disk.
    /// </summary>
    private string? _sessionPreferredMode;

    public MainWindow()
    {
        Logger.Debug("MainWindow constructor started");
        InitializeComponent();

        // Always open on the primary monitor — CenterScreen alone only
        // centers on whichever screen the window happens to land on.
        WindowStartupLocation = WindowStartupLocation.Manual;
        var workArea = SystemParameters.WorkArea;
        Left = (workArea.Width - Width) / 2 + workArea.Left;
        Top = (workArea.Height - Height) / 2 + workArea.Top;

        _profiles = [];
        CmbProfile.ItemsSource = _profiles;

        // Create notification manager (no deps)
        _notificationManager = new NotificationManager(
            Toast, ToastMessage, ToastIcon,
            StatusIndicator, TxtStatus, TxtGameCount,
            TxtLoadingDots);

        // Create launcher (no deps)
        _launcher = new GreenLumaLauncher();

        // Create game list controller (depends on NotificationManager)
        _gameListController = new GameListController(LstGames, TxtGameCount, PnlEmptyGames, _notificationManager);

        // Create search controller (depends on NotificationManager)
        _searchController = new SearchController(
            DgResults, PnlSearchLoading, TxtResultCount, PnlEmptyResults,
            TxtLoadingDots, _notificationManager);

        // Create profile controller (depends on GameListController, Launcher, NotificationManager)
        _profileController = new ProfileController(CmbProfile, _profiles, _gameListController, _launcher, _notificationManager);

        // Create app list controller (depends on ProfileController, GameListController, Launcher, NotificationManager)
        _appListController = new AppListController(_profileController, _gameListController, _launcher, _notificationManager);

        // Wire cross-controller events
        _searchController.GameSelected += OnSearchResultSelected;

        // Configure search result columns if needed
        ConfigureSearchResultColumns();

        // Commands
        FocusSearchCommand = new RelayCommand(_ => TxtSearchTextBox.Focus());
        GenerateApplistCommand = new RelayCommand(_ => GenerateApplistButton_Click(BtnGenerateApplist, new RoutedEventArgs()));
        LaunchGreenlumaCommand = new RelayCommand(_ => LaunchGreenlumaButton_Click(BtnLaunchGreenluma, new RoutedEventArgs()));
        ToggleStealthCommand = new RelayCommand(_ => TglStealthMode.IsChecked = !TglStealthMode.IsChecked.GetValueOrDefault());

        DataContext = this;

        // Startup initialization
        _config = ConfigService.Load();
        _profileController.Config = _config;
        _profileController.LoadProfileList();

        UpdatePluginButtons();
        CheckPathsOnStartup();
        CheckForUpdates();
        CheckForGreenLumaUpdates();
        UpdateStatus();
        _gameListController.UpdateGameListState();
        Loaded += (_, _) => CheckMixedInstallOnStartup();
        Logger.Debug("MainWindow constructor completed");
    }

    public ICommand FocusSearchCommand { get; }
    public ICommand GenerateApplistCommand { get; }
    public ICommand LaunchGreenlumaCommand { get; }
    public ICommand ToggleStealthCommand { get; }

    private void ConfigureSearchResultColumns()
    {
        // Columns are defined in XAML — no additional setup needed.
    }

    // ─── Search ───────────────────────────────────────────────────────

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
            _ = _searchController.ExecuteSearchAsync(TxtSearchTextBox.Text.Trim());
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await _searchController.ExecuteSearchAsync(TxtSearchTextBox.Text.Trim());
    }

    private void SearchTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (TxtSearchPlaceholder.Visibility != Visibility.Visible)
            return;
        AnimatePlaceholder(0.5, 0.0, () => TxtSearchPlaceholder.Visibility = Visibility.Collapsed);
    }

    private void SearchTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TxtSearchTextBox.Text))
            return;
        TxtSearchPlaceholder.Visibility = Visibility.Visible;
        TxtSearchPlaceholder.Opacity = 0.0;
        AnimatePlaceholder(0.0, 0.5);
    }

    private void AnimatePlaceholder(double from, double to, Action? onComplete = null)
    {
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(150));
        Storyboard.SetTarget(animation, TxtSearchPlaceholder);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Children.Add(animation);
        if (onComplete != null) storyboard.Completed += (_, _) => onComplete();
        storyboard.Begin();
    }

    private void SearchGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        TxtSearchTextBox.Focus();
    }

    // ─── Game List Search ───────────────────────────────────────────

    private void TxtGameSearch_GotFocus(object sender, RoutedEventArgs e)
    {
        TxtGameSearchPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void TxtGameSearch_LostFocus(object sender, RoutedEventArgs e)
    {
        TxtGameSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtGameSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void TxtGameSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Update placeholder visibility
        TxtGameSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtGameSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Apply real-time filter
        _gameListController.SetSearchFilter(TxtGameSearch.Text);
    }

    private void CmbGameTypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbGameTypeFilter.SelectedItem is ComboBoxItem item && item.Content is string type)
            _gameListController.SetTypeFilter(type);
    }

    private void SearchResult_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row && row.DataContext is Game game)
            _searchController.OnSearchResultDoubleClick(game);
    }

    private void OnSearchResultSelected(Game game)
    {
        if (_profileController.CurrentProfile == null)
        {
            _notificationManager.ShowToast("No profile selected", false);
            return;
        }

        // Check if already in game list
        if (_gameListController.Games.Any(g => g.AppId == game.AppId))
        {
            _notificationManager.ShowToast($"{game.Name} is already in your profile", false);
            return;
        }

        _gameListController.AddGame(game);
        _profileController.CurrentProfile.Games.Add(game);
        _profileController.SaveCurrentProfile();

        _ = Task.Run(async () =>
        {
            try
            {
                var tempGame = new Game { AppId = game.AppId, Name = string.Empty, Type = game.Type };
                await SearchService.PopulateGameDetailsAsync(tempGame);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var existingGame = _gameListController.Games.FirstOrDefault(g => g.AppId == game.AppId);
                    if (existingGame == null) return;

                    if (!string.IsNullOrEmpty(tempGame.Name))
                        existingGame.Name = tempGame.Name;

                    existingGame.Type = tempGame.Type;

                    if (!string.IsNullOrEmpty(tempGame.IconUrl))
                    {
                        existingGame.IconUrl = tempGame.IconUrl;
                        _profileController.SaveCurrentProfile();
                    }
                });
            }
            catch
            {
                // ignored
            }
        });
    }

    // ─── Profile ──────────────────────────────────────────────────────

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbProfile.SelectedItem == null)
            return;

        var profileName = CmbProfile.SelectedItem.ToString();

        if (profileName == "__empty__")
        {
            RestorePreviousProfile(e);
            return;
        }

        if (profileName != null)
        {
            Logger.Info($"Profile switched to: {profileName}");
            // Clear game search and type filter when switching profiles
            TxtGameSearch.Text = string.Empty;
            if (CmbGameTypeFilter.SelectedIndex != 0)
                CmbGameTypeFilter.SelectedIndex = 0;

            CancelPendingProfileLoad();
            _profileController.SelectProfile(profileName);
            ScheduleGameDetailLoad();
        }
    }

    private void RestorePreviousProfile(SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is string removedItem && removedItem != "__empty__")
            CmbProfile.SelectedItem = removedItem;
        else
            foreach (var profile in _profiles)
                if (profile != "__empty__")
                {
                    CmbProfile.SelectedItem = profile;
                    break;
                }
    }

    private void CreateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _profileController.CreateProfile();
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _profileController.DeleteProfile(CmbProfile.SelectedItem?.ToString());
    }

    private void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _profileController.ImportProfile();
    }

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        _profileController.ExportProfile();
    }

    private void ProfileOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.ContextMenu == null)
            return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
        button.ContextMenu.Closed += (_, _) => button.IsChecked = false;
    }

    private void ClearProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_profileController.CurrentProfile == null)
        {
            _notificationManager.ShowToast("No profile selected", false);
            return;
        }

        if (_gameListController.Games.Count == 0)
        {
            _notificationManager.ShowToast("Profile is already empty");
            return;
        }

        var result = CustomMessageBox.Show(
            $"Remove all {_gameListController.Games.Count} game(s) from '{_profileController.CurrentProfile.Name}'?",
            "Clear Profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Exclamation);

        if (result != MessageBoxResult.Yes) return;

        TxtGameSearch.Text = string.Empty;
        _gameListController.ClearGames();
        _profileController.SaveCurrentProfile();
        _notificationManager.ShowToast($"Profile '{_profileController.CurrentProfile.Name}' cleared");
    }

    // ─── Game List ────────────────────────────────────────────────────

    private void CancelPendingProfileLoad()
    {
        if (_profileLoadCts != null)
        {
            _profileLoadCts.Cancel();

            var oldCts = _profileLoadCts;
            _ = Task.Run(async () =>
            {
                await Task.Delay(100, oldCts.Token);
                oldCts.Dispose();
            }, oldCts.Token);
        }

        _profileLoadCts = new CancellationTokenSource();
    }

    private void ScheduleGameDetailLoad()
    {
        if (_profileLoadCts == null) return;
        var token = _profileLoadCts.Token;

        _ = Task.Run(async () =>
        {
            await Task.Delay(100, token);
            if (token.IsCancellationRequested) return;

            var gamesToProcess = _gameListController.Games.ToList();
            var semaphore = new SemaphoreSlim(6);

            var tasks = gamesToProcess.Select(async game =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;

                    if (!string.IsNullOrWhiteSpace(game.IconUrl))
                        return;

                    var tempGame = new Game { AppId = game.AppId, Name = string.Empty, Type = "Game" };
                    await SearchService.PopulateGameDetailsAsync(tempGame);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        if (!string.IsNullOrEmpty(tempGame.Name))
                            game.Name = tempGame.Name;

                        game.Type = tempGame.Type;

                        if (!string.IsNullOrEmpty(tempGame.IconUrl))
                        {
                            game.IconUrl = tempGame.IconUrl;
                            _profileController.SaveCurrentProfile();
                        }
                    }, DispatcherPriority.Background);
                }
                catch
                {
                    // ignored
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }, token);
    }

    private void GameName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not TextBlock textBlock || textBlock.DataContext is not Game game)
            return;

        _gameListController.StartRename(game);
        e.Handled = true;
    }

    private void GameNameEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Visibility == Visibility.Visible)
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }));
    }

    private void GameNameEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not Game game)
            return;

        if (e.Key == Key.Return)
        {
            _gameListController.CommitRename(game, textBox.Text);
            _profileController.SaveCurrentProfile();
            _notificationManager.ShowToast("Game renamed");
        }
        else if (e.Key == Key.Escape)
        {
            _gameListController.CancelRename(game);
        }
    }

    private void GameNameEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.DataContext is not Game game)
            return;

        if (!game.IsEditing) return;
        _gameListController.CommitRename(game, textBox.Text);
        _profileController.SaveCurrentProfile();
    }

    private void AddGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Game game })
            OnSearchResultSelected(game);
    }

    private void RemoveGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not Game game)
            return;

        _gameListController.RemoveGame(game);
        _profileController.CurrentProfile?.Games.Remove(game);
        _profileController.SaveCurrentProfile();
        _notificationManager.ShowToast($"Removed '{game.Name}'");
    }

    // ─── Import AppList ───────────────────────────────────────────────

    private async void LoadAppListButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_config == null) return;

            var importResult = await _appListController.ImportExistingAppListAsync(_config);

            if (!importResult.FoundAppList)
            {
                _notificationManager.ShowToast("No existing AppList found", false);
                return;
            }

            var targetProfile = _profileController.CurrentProfile
                ?? ProfileService.Load("default")
                ?? new Profile { Name = "default" };

            var profileName = targetProfile.Name;
            var result = CustomMessageBox.Show(
                $"Found {importResult.AppIds.Count} items in existing AppList.\n\n" +
                $"Would you like to import them into '{profileName}' profile?",
                "Import AppList",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            var progress = new Progress<AppListProgressReport>(report =>
            {
                Dispatcher.Invoke(() =>
                {
                    TxtAppListProgress.Text = report.Status;
                    AppListProgressBar.IsIndeterminate = report.IsIndeterminate;
                    if (!report.IsIndeterminate && report.Total > 0)
                        AppListProgressBar.Value = report.Percentage;
                });
            });

            ShowAppListProgress();

            try
            {
                await _appListController.ResolveAndImportAppsAsync(importResult.AppIds, targetProfile, progress);

                if (_profileController.CurrentProfile?.Name == targetProfile.Name)
                    _profileController.LoadProfile(targetProfile.Name);

                if (importResult.HasSteamWarning)
                    CustomMessageBox.Show(
                        "WARNING: AppList was found in your Steam folder.\n\n" +
                        "For better stealth, you should uninstall GreenLuma from the Steam folder " +
                        "and use it from a separate location instead.",
                        "Stealth Warning",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
            }
            finally
            {
                HideAppListProgress();
            }
        }
        catch
        {
            HideAppListProgress();
        }
    }

    private void ShowAppListProgress()
    {
        TxtAppListProgress.Text = "Starting...";
        AppListProgressBar.IsIndeterminate = true;
        AppListProgressBar.Value = 0;
        PnlAppListProgress.Visibility = Visibility.Visible;
    }

    private void HideAppListProgress()
    {
        PnlAppListProgress.Visibility = Visibility.Collapsed;
    }

    // ─── AppList Generation & Launch ──────────────────────────────────

    private async void GenerateApplistButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Generate AppList button clicked");
        try
        {
            if (_config == null) return;

            if (!_launcher.ValidatePaths(_config))
            {
                _notificationManager.ShowToast("GreenLuma path not configured", false);
                return;
            }

            if (_profileController.CurrentProfile == null)
            {
                _notificationManager.ShowToast("No profile selected", false);
                return;
            }

            if (_gameListController.Games.Count == 0)
            {
                var clearResult = CustomMessageBox.Show(
                    "This profile contains no games. Clear the existing AppList?",
                    "Clear AppList",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (clearResult != MessageBoxResult.Yes)
                    return;
            }

            BtnGenerateApplist.IsEnabled = false;

            try
            {
                _profileController.SaveCurrentProfile();

                var totalAppIds = await _appListController.GenerateAsync(_config, _profileController.CurrentProfile);

                if (totalAppIds >= 0)
                {
                    var generatedCount = Math.Min(totalAppIds, GreenLumaService.AppListLimit);

                    if (generatedCount > 0)
                    {
                        var itemWord = generatedCount == 1 ? "item" : "items";
                        _notificationManager.ShowToast($"Generated AppList with {generatedCount} {itemWord}");
                    }
                    else
                    {
                        _notificationManager.ShowToast("AppList cleared successfully");
                    }

                    if (totalAppIds > GreenLumaService.AppListLimit)
                    {
                        var droppedCount = totalAppIds - GreenLumaService.AppListLimit;
                        CustomMessageBox.Show(
                            $"Warning: Your profile lists {totalAppIds} item(s), but GreenLuma is limited to {GreenLumaService.AppListLimit} entries.\n\n" +
                            $"{droppedCount} item(s) were excluded from the generated AppList.\n\n" +
                            "Consider creating a smaller profile for the games you intend to launch.",
                            "AppList Truncated",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                else
                {
                    _notificationManager.ShowToast("Failed to generate AppList", false);
                }
            }
            finally
            {
                BtnGenerateApplist.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error generating AppList");
        }
    }

    private async void LaunchGreenlumaButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_config == null)
            {
                Logger.Error("Launch clicked but _config is null");
                return;
            }

            Logger.Info("Launch button clicked");

            if (!_launcher.ValidatePaths(_config))
            {
                Logger.Warn("Path validation failed, prompting user to configure");
                var result = CustomMessageBox.Show(
                    "GreenLuma path is not configured. Open settings?",
                    "Launch GreenLuma",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Logger.Info("User chose to open settings");
                    SettingsButton_Click(null, null!);
                }

                return;
            }

            // Check for legacy AppList that needs conversion (GreenLuma 1.8.0+)
            if (GreenLumaService.HasLegacyAppList(_config))
            {
                Logger.Warn("Legacy AppList folder detected, prompting for conversion");
                var (installedVersion, _) = GreenLumaService.DetectInstalledVersion(_config.GreenLumaPath);
                var versionStr = installedVersion != null ? $"v{installedVersion}" : "unknown version";
                
                var convertResult = CustomMessageBox.Show(
                    $"Legacy AppList format detected (AppList folder with .txt files).\n\n" +
                    $"You are running GreenLuma {versionStr} which uses the new AppList.ini format.\n\n" +
                    $"Would you like to automatically convert your existing AppList to the new format?",
                    "Legacy AppList Detected",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (convertResult == MessageBoxResult.Yes)
                {
                    Logger.Info("User chose to convert legacy AppList");
                    if (GreenLumaService.ConvertAppListFolderToIni(_config.GreenLumaPath!, out var convertError))
                    {
                        _notificationManager.ShowToast("AppList converted to new format successfully");
                        Logger.Info("Legacy AppList conversion successful");
                    }
                    else
                    {
                        Logger.Warn($"Legacy AppList conversion failed: {convertError}");
                        _notificationManager.ShowToast($"AppList conversion failed: {convertError}", false);
                    }
                }
                else
                {
                    Logger.Info("User declined legacy AppList conversion");
                }
            }

            if (!GreenLumaService.IsAppListGenerated(_config))
            {
                Logger.Warn("No AppList found, prompting user to generate");
                var generateResult = CustomMessageBox.Show(
                    "No AppList found. Generate one now?",
                    "Generate AppList",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (generateResult == MessageBoxResult.No)
                {
                    Logger.Info("User declined AppList generation, aborting launch");
                    return;
                }

                Logger.Info("Generating AppList...");
                _profileController.SaveCurrentProfile();
                await _appListController.GenerateAsync(_config, _profileController.CurrentProfile);
                await Task.Delay(500);
            }

            _profileController.SaveCurrentProfile();

            if (_launcher.ValidatePaths(_config) && await _launcher.LaunchAsync(_config))
            {
                Logger.Info("Launch completed successfully");
                _notificationManager.ShowToast("GreenLuma injected into the Steam process. Please wait a moment while Steam launches.");
            }
            else
            {
                Logger.Error("Launch failed");
                _notificationManager.ShowToast("Failed to launch GreenLuma", false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unhandled exception in LaunchGreenlumaButton_Click");
        }
    }

    // ─── Settings & Status ────────────────────────────────────────────

    private void Toast_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_config == null) return;

        // If the toast shows a GreenLuma update notification, open Settings to the System tab
        if (!ToastMessage.Text.Contains("GreenLuma update", StringComparison.OrdinalIgnoreCase))
            return;

        var dialog = new SettingsDialog(_config, openToSystemTab: true);
        dialog.Owner = this;

        if (dialog.ShowDialog() != true)
            return;

        _config = ConfigService.Load();
        _profileController.Config = _config;
        _sessionPreferredMode = null;
        UpdateStatus();

        // Re-detect installed GreenLuma version — deployment may have updated it
        if (_lastGreenLumaVersion == null || string.IsNullOrWhiteSpace(_config.GreenLumaPath))
            return;

        var (installedVersion, _) = GreenLumaService.DetectInstalledVersion(_config.GreenLumaPath);
        var updatedInfo = new GreenLumaVersionInfo
        {
            LatestVersionTag = _lastGreenLumaVersion.LatestVersionTag,
            LatestSemanticVersion = _lastGreenLumaVersion.LatestSemanticVersion,
            InstalledVersion = installedVersion,
            CheckSucceeded = _lastGreenLumaVersion.CheckSucceeded,
            CheckedAt = DateTime.UtcNow
        };
        UpdateGreenLumaVersionStatus(updatedInfo);
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs? e)
    {
        Logger.Info("Settings button clicked");
        try
        {
            if (_config == null) return;

            var hadGreenLumaPath = !string.IsNullOrWhiteSpace(_config.GreenLumaPath);

            var dialog = new SettingsDialog(_config);

            if (dialog.ShowDialog() == true)
            {
                _config = ConfigService.Load();
                _profileController.Config = _config;
                _sessionPreferredMode = null;
                UpdateStatus();

                var nowHasGreenLumaPath = !string.IsNullOrWhiteSpace(_config.GreenLumaPath);

                if (!hadGreenLumaPath && nowHasGreenLumaPath)
                    _ = ImportExistingAppListAfterSettings();

                // Re-detect installed GreenLuma version — deployment may have updated it
                if (_lastGreenLumaVersion != null && nowHasGreenLumaPath)
                {
                    var (installedVersion, _) = GreenLumaService.DetectInstalledVersion(_config.GreenLumaPath);
                    var updatedInfo = new GreenLumaVersionInfo
                    {
                        LatestVersionTag = _lastGreenLumaVersion.LatestVersionTag,
                        LatestSemanticVersion = _lastGreenLumaVersion.LatestSemanticVersion,
                        InstalledVersion = installedVersion,
                        CheckSucceeded = _lastGreenLumaVersion.CheckSucceeded,
                        CheckedAt = DateTime.UtcNow
                    };
                    UpdateGreenLumaVersionStatus(updatedInfo);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error in SettingsButton_Click");
        }
    }

    private async Task ImportExistingAppListAfterSettings()
    {
        var importResult = await _appListController.ImportExistingAppListAsync(_config!);
        if (!importResult.FoundAppList || importResult.AppIds.Count == 0) return;

        var targetProfile = _profileController.CurrentProfile
            ?? ProfileService.Load("default")
            ?? new Profile { Name = "default" };
        await _appListController.ResolveAndImportAppsAsync(importResult.AppIds, targetProfile);

        if (_profileController.CurrentProfile?.Name == targetProfile.Name)
            _profileController.LoadProfile(targetProfile.Name);
    }

    /// <summary>
    /// Called once at startup to detect and prompt on mixed installations.
    /// Updates the status after any mode changes so the UI stays in sync.
    /// </summary>
    private void CheckMixedInstallOnStartup()
    {
        if (_config == null)
            return;

        var steamPath = _config.SteamPath?.Trim();
        var greenLumaPath = GreenLumaService.ResolveGreenLumaPath(
            _config.GreenLumaPath?.Trim(), steamPath, _config.LastStealthAnyPath);

        var savedMode = _config.PreferredMode;

        if (GreenLumaService.CheckMixedInstallState(
                steamPath, greenLumaPath, _config, out var persisted, persistPreference: false))
        {
            if (persisted)
            {
                // Choice was saved to disk — PreferredMode is correctly set.
                _sessionPreferredMode = null;
            }
            else
            {
                // Session-only choice — capture for display, then restore
                // so SettingsDialog doesn't inherit it and persist via Ok_Click.
                _sessionPreferredMode = _config.PreferredMode;
                _config.PreferredMode = savedMode;

                // Also update GreenLumaPath in memory to reflect the choice,
                // so the Settings dialog (which has no knowledge of the
                // session-only preference) loads the correct path.
                if (string.Equals(_sessionPreferredMode, GreenLumaInstallMethod.User32.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_sessionPreferredMode, GreenLumaInstallMethod.Normal.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    _config.GreenLumaPath = steamPath ?? string.Empty;
                }
                else if (string.Equals(_sessionPreferredMode, GreenLumaInstallMethod.StealthAny.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    _config.GreenLumaPath = greenLumaPath ?? string.Empty;
                }
            }

            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        if (_config == null)
        {
            _notificationManager.SetStatusIndicator(
                Resources["Danger"] as Brush ?? Brushes.Red, "Not Configured");
            return;
        }

        var steamPath = _config.SteamPath.Trim();
        var greenLumaPath = _config.GreenLumaPath.Trim();

        var installMethod = GreenLumaService.DetectInstallMethod(
            steamPath, greenLumaPath, _sessionPreferredMode ?? _config.PreferredMode);
        var successBrush = Resources["Success"] as Brush ?? Brushes.Green;

        // Determine User32 DLL state from disk — the toggle is only shown
        // when the DLL exists in either enabled or disabled form.
        var user32DllPath = !string.IsNullOrWhiteSpace(steamPath)
            ? Path.Combine(steamPath, "user32.dll")
            : null;
        var user32DisabledDllPath = !string.IsNullOrWhiteSpace(steamPath)
            ? Path.Combine(steamPath, "user32_DISABLED.dll")
            : null;

        var hasUser32Dll = user32DllPath != null && File.Exists(user32DllPath);
        var hasUser32DisabledDll = user32DisabledDllPath != null && File.Exists(user32DisabledDllPath);
        var user32DllExists = hasUser32Dll || hasUser32DisabledDll;

        // Show the User32 toggle only when the DLL is present in either state
        BorderUser32Mode.Visibility = user32DllExists ? Visibility.Visible : Visibility.Collapsed;

        if (hasUser32Dll)
        {
            // User32 mode is active — no injection process, so stealth is irrelevant
            _syncingUser32Mode = true;
            TglUser32Mode.IsChecked = true;
            _syncingUser32Mode = false;

            TglStealthMode.IsChecked = false;
            TglStealthMode.IsEnabled = false;
            BorderStealthMode.Opacity = 0.5;
            _notificationManager.SetStatusIndicator(successBrush, "Ready  •  User32 Mode");
            return;
        }

        // User32 DLL is either disabled or absent
        BorderStealthMode.Opacity = 1.0;

        if (hasUser32DisabledDll)
        {
            _syncingUser32Mode = true;
            TglUser32Mode.IsChecked = false;
            _syncingUser32Mode = false;
            // Don't return — fall through to show the real install method
        }

        switch (installMethod)
        {
            case GreenLumaInstallMethod.Normal:
                TglStealthMode.IsEnabled = true;
                _notificationManager.SetStatusIndicator(successBrush, "Ready  •  Normal Mode");
                break;

            case GreenLumaInstallMethod.StealthAny:
                TglStealthMode.IsChecked = true;
                TglStealthMode.IsEnabled = false;

                _config.NoHook = true;

                var stealthLabel = _config.NoHook
                    ? "Ready  •  Stealth Mode (Forced)"
                    : "Ready  •  Stealth Mode";
                _notificationManager.SetStatusIndicator(successBrush, stealthLabel);
                break;

            default:
                TglStealthMode.IsEnabled = true;
                _notificationManager.SetStatusIndicator(
                    Resources["Danger"] as Brush ?? Brushes.Red, "Not Configured");
                break;
        }
    }

    private bool _syncingUser32Mode;

    private void User32Mode_Toggled(object sender, RoutedEventArgs e)
    {
        if (_config == null || _syncingUser32Mode || sender is not ToggleButton toggleButton)
            return;

        var enable = toggleButton.IsChecked.GetValueOrDefault();
        var steamPath = _config.SteamPath?.Trim();

        if (string.IsNullOrWhiteSpace(steamPath))
            return;

        if (enable)
        {
            // Enable User32: rename user32_DISABLED.dll → user32.dll
            GreenLumaService.EnableUser32(steamPath);
        }
        else
        {
            // Disable User32: rename user32.dll → user32_DISABLED.dll
            GreenLumaService.DisableUser32(steamPath);
        }

        ConfigService.Save(_config);
        UpdateStatus();
    }

    private void NoHook_Toggled(object sender, RoutedEventArgs e)
    {
        if (_config == null || sender is not ToggleButton toggleButton)
            return;

        // Ignore stealth toggles when User32 mode is active on disk
        var steamPath = _config.SteamPath?.Trim();
        if (!string.IsNullOrWhiteSpace(steamPath) &&
            File.Exists(Path.Combine(steamPath, "user32.dll")))
            return;

        _config.NoHook = toggleButton.IsChecked.GetValueOrDefault();
        ConfigService.Save(_config);
        UpdateStatus();
    }

    // ─── Update ───────────────────────────────────────────────────────

    private async void CheckForUpdates()
    {
        Logger.Debug("Checking for application updates");
        try
        {
            if (_config?.DisableUpdateCheck == true)
            {
                Logger.Debug("Update check disabled in config");
                return;
            }

            var updateInfo = await UpdateService.CheckForUpdatesAsync();
            if (updateInfo?.UpdateAvailable == true)
            {
                Logger.Info("Update available: {CurrentVersion} -> {LatestVersion}", updateInfo.CurrentVersion, updateInfo.LatestVersion);
                await Dispatcher.InvokeAsync(() => HandleUpdateAvailable(updateInfo));
            }
            else
            {
                Logger.Debug("No updates available");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error checking for updates");
        }
    }

    private async void CheckForGreenLumaUpdates()
    {
        Logger.Debug("Checking for GreenLuma updates");
        try
        {
            if (_config == null)
            {
                Logger.Debug("Config is null, skipping GreenLuma update check");
                return;
            }

            var result = await GreenLumaUpdateService.CheckForGreenLumaUpdatesAsync(_config);
            if (result == null)
            {
                Logger.Debug("GreenLuma update check returned null");
                return;
            }

            _lastGreenLumaVersion = result;

            await Dispatcher.InvokeAsync(() =>
            {
                if (result.UpdateAvailable)
                {
                    Logger.Info($"GreenLuma update available: {result.InstalledVersion} -> {result.LatestSemanticVersion}");
                    _notificationManager.ShowToast($"GreenLuma update available: v{result.LatestSemanticVersion}", true);
                    UpdateGreenLumaVersionStatus(result);
                }
                else if (result.CheckSucceeded)
                {
                    Logger.Debug($"GreenLuma is up to date: {result.InstalledVersion}");
                    UpdateGreenLumaVersionStatus(result);
                }
                else
                {
                    Logger.Warn("GreenLuma update check failed");
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error checking for GreenLuma updates");
        }
    }

    private void UpdateGreenLumaVersionStatus(GreenLumaVersionInfo info)
    {
        if (TxtGreenLumaVersion == null)
            return;

        var installed = info.InstalledVersion != null ? $"v{info.InstalledVersion}" : "GL ?";
        var latest = info.LatestSemanticVersion != null
            ? $"v{info.LatestSemanticVersion}"
            : "?";

        TxtGreenLumaVersion.Text = $"Current: {installed}  Latest: {latest}";
        TxtGreenLumaVersion.Visibility = Visibility.Visible;
    }

    private async Task HandleUpdateAvailable(UpdateInfo updateInfo)
    {
        if (_config?.AutoUpdate == true && !string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
        {
            var result = CustomMessageBox.Show(
                $"Current Version: {updateInfo.CurrentVersion}\nLatest Version: {updateInfo.LatestVersion}\n\n" +
                "Auto-update is enabled. The update will be downloaded and installed automatically.\n\n" +
                "The application will restart to complete the update.",
                "Update Available",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Asterisk);

            if (result == MessageBoxResult.OK)
            {
                if (await UpdateService.PerformAutoUpdateAsync(updateInfo.DownloadUrl))
                    Application.Current.Shutdown();
                else
                {
                    _notificationManager.ShowToast("Auto-update failed. Please download manually.", false);
                    LaunchBrowser(updateInfo.DownloadUrl);
                }
            }
        }
        else
        {
            var result = CustomMessageBox.Show(
                $"Current Version: {updateInfo.CurrentVersion}\nLatest Version: {updateInfo.LatestVersion}\n\n" +
                "Would you like to download the update now?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Asterisk);

            if (result == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
                LaunchBrowser(updateInfo.DownloadUrl);
        }
    }

    // ─── Startup ──────────────────────────────────────────────────────

    private void CheckPathsOnStartup()
    {
        if (_config == null)
            return;

        if (!_config.FirstRun ||
            (!string.IsNullOrWhiteSpace(_config.SteamPath) && !string.IsNullOrWhiteSpace(_config.GreenLumaPath)))
            return;

        _config.FirstRun = false;
        ConfigService.Save(_config);

        Dispatcher.BeginInvoke((Action)(() =>
        {
            var result = CustomMessageBox.Show(
                "Steam and GreenLuma paths could not be detected automatically.\n\n" +
                "Please configure them in Settings to use all features.",
                "Setup Required",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Asterisk);

            if (result == MessageBoxResult.OK) SettingsButton_Click(null, null!);
        }), DispatcherPriority.Loaded);
    }

    // ─── Window Chrome ────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void GitHubButton_Click(object sender, RoutedEventArgs e) => LaunchBrowser("https://github.com/FroggMaster/GreenLuma-Manager");
    private static void LaunchBrowser(string url) => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    // ─── Plugins ──────────────────────────────────────────────────────

    private void ManagePluginsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PluginsDialog { Owner = this };
        dialog.ShowDialog();
        UpdatePluginButtons();
    }

    public void UpdatePluginButtons()
    {
        PnlPluginButtons.Children.Clear();

        var plugins = PluginService.GetEnabledPlugins();

        foreach (var plugin in plugins)
        {
            var button = new Button
            {
                Style = (Style)FindResource("IconButton"),
                ToolTip = plugin.Name,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = plugin
            };

            var path = new System.Windows.Shapes.Path
            {
                Width = 18,
                Height = 18,
                Data = plugin.Icon,
                Fill = (SolidColorBrush)FindResource("TextSecondary"),
                Stretch = Stretch.Uniform
            };

            button.Content = path;
            button.Click += PluginButton_Click;
            PnlPluginButtons.Children.Add(button);
        }
    }

    private void PluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not IPlugin plugin) return;

        Logger.Info("Plugin button clicked: {PluginName}", plugin.Name);
        try
        {
            plugin.ShowUi(this);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error showing plugin UI: {PluginName}", plugin.Name);
        }
    }
}
