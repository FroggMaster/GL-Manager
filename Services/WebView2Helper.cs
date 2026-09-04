using System.IO;
using GreenLuma_Manager.Utilities;
using Microsoft.Web.WebView2.Core;

namespace GreenLuma_Manager.Services;

public static class WebView2Helper
{
    private static readonly string UserDataDir = PathDetector.WebView2Dir;

    private static CoreWebView2Environment? _cachedEnvironment;

    /// <summary>
    /// Creates (or returns a cached) <see cref="CoreWebView2Environment"/> with an isolated
    /// user data directory, suitable for invisible / off-screen WebView2 instances.
    /// </summary>
    public static async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_cachedEnvironment is not null)
        {
            Logger.Info("Returning cached WebView2 environment");
            return _cachedEnvironment;
        }

        Logger.Debug("Creating new WebView2 environment");
        PathDetector.EnsureExists(UserDataDir);

        _cachedEnvironment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: UserDataDir,
            options: null).ConfigureAwait(false);

        return _cachedEnvironment;
    }
}
