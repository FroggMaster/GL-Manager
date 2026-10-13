using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using GreenLuma_Manager.Dialogs;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Services;

namespace GreenLuma_Manager.Controllers;

/// <summary>
/// Displays the boot image while a GreenLuma launch starts, and keeps it on screen
/// until Steam appears, a DLLInjector error window shows up, or a safety timeout
/// elapses. The image is resolved from a user-supplied file when present,
/// otherwise from the embedded default, and is rendered by this application rather
/// than by DLLInjector.
/// </summary>
public class BootImageController
{
    // How often Steam and DLLInjector are checked while the splash is visible.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    // Safety cap so the splash can never become stuck if nothing is detected.
    private static readonly TimeSpan MaxDisplayDuration = TimeSpan.FromSeconds(60);

    // Processes whose visible window means Steam's own splash/UI is on screen.
    private static readonly string[] SteamWindowProcesses = ["steam", "steamwebhelper"];

    private BootImageWindow? _window;
    private DispatcherTimer? _timer;
    private DateTime _shownAtUtc;

    public void Show(Config config)
    {
        Hide();

        try
        {
            var method = GreenLumaService.DetectInstallMethod(
                config.SteamPath, config.GreenLumaPath, config.PreferredMode);

            var image = BootImageService.Load(config, method);
            if (image == null)
                return;

            // Owned by the main window so the splash stays above it, while still
            // allowing activated dialogs from other processes (for example an
            // error from DLLInjector) to appear above the splash.
            _window = new BootImageWindow(image)
            {
                Owner = Application.Current?.MainWindow
            };
            _window.Show();
            _shownAtUtc = DateTime.UtcNow;

            _timer = new DispatcherTimer { Interval = PollInterval };
            _timer.Tick += OnTick;
            _timer.Start();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Failed to show the boot image: {ex.Message}");
            _window = null;
        }
    }

    public void Hide()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        var window = _window;
        _window = null;

        if (window == null)
            return;

        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to close the boot image window: {ex.Message}");
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Hide once Steam's own window (its splash/UI) is on screen, when
        // DLLInjector is showing an error window, or once the safety timeout is
        // reached. Waiting for Steam's window rather than just its process keeps
        // the boot image up until Steam is actually taking over the screen.
        if (IsSteamWindowVisible() ||
            IsInjectorErrorVisible() ||
            DateTime.UtcNow - _shownAtUtc >= MaxDisplayDuration)
        {
            Hide();
        }
    }

    private static bool IsSteamWindowVisible()
    {
        foreach (var processName in SteamWindowProcesses)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                            return true;
                    }
                    catch
                    {
                        // The process may exit between enumeration and query.
                    }
                }
            }
        }

        return false;
    }

    private static bool IsInjectorErrorVisible()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("DLLInjector"))
            {
                using (process)
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
            }
        }
        catch
        {
            // The process may exit or deny access between enumeration and query;
            // treat that as no error.
        }

        return false;
    }
}
