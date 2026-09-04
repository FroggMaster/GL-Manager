using System.IO;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using GreenLuma_Manager.Models;
using GreenLuma_Manager.Plugins;
using GreenLuma_Manager.Services;
using GreenLuma_Manager.Utilities;

namespace GreenLuma_Manager.Services;

public class PluginService
{
    private static readonly string PluginsDir = PathDetector.PluginsDir;

    private static readonly string PluginsConfigPath = Path.Combine(PathDetector.AppDataDir, "plugins.json");

    private static readonly string PendingDeletesPath = Path.Combine(PathDetector.AppDataDir, "pending_deletes.json");

    private static readonly List<(PluginInfo Info, IPlugin? Instance, AssemblyLoadContext? Context)> LoadedPlugins = [];
    private static List<PluginInfo> _pluginInfos = [];

    public static void Initialize()
    {
        try
        {
            Logger.Info("Initializing plugin service");
            PathDetector.EnsureExists(PluginsDir);
            CleanupPendingDeletes();
            _pluginInfos = LoadPluginInfos();
            LoadPlugins();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Plugin initialization failed");
        }
    }

    private static List<PluginInfo> LoadPluginInfos() => LoadJsonList<PluginInfo>(PluginsConfigPath);

    private static void SavePluginInfos() => SaveJsonList(PluginsConfigPath, _pluginInfos);

    private static void LoadPlugins()
    {
        Logger.Info($"Loading {_pluginInfos.Count(p => p.IsEnabled)} enabled plugins");
        foreach (var pluginInfo in _pluginInfos.Where(p => p.IsEnabled))
            try
            {
                var pluginPath = Path.Combine(PluginsDir, pluginInfo.FileName);
                if (!File.Exists(pluginPath))
                {
                    Logger.Warn($"Plugin file not found: {pluginPath}");
                    continue;
                }

                var context = new AssemblyLoadContext($"Plugin_{pluginInfo.Id}", true);
                var assembly = context.LoadFromAssemblyPath(pluginPath);

                var pluginType = assembly.GetTypes()
                    .FirstOrDefault(t =>
                        typeof(IPlugin).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false });

                if (pluginType == null)
                {
                    Logger.Warn($"No IPlugin implementation found in: {pluginPath}");
                    continue;
                }

                var instance = (IPlugin?)Activator.CreateInstance(pluginType);
                if (instance == null) continue;

                instance.Initialize();
                LoadedPlugins.Add((pluginInfo, instance, context));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to load plugin: {pluginInfo.FileName}");
            }
    }

    public static void OnApplicationStartup()
    {
        Logger.Info("Notifying plugins of application startup");
        foreach (var (_, instance, _) in LoadedPlugins)
            try
            {
                instance?.OnApplicationStartup();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Plugin OnApplicationStartup failed");
            }
    }

    public static void OnApplicationShutdown()
    {
        Logger.Info("Notifying plugins of application shutdown");
        foreach (var (_, instance, context) in LoadedPlugins)
            try
            {
                instance?.OnApplicationShutdown();
                context?.Unload();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Plugin OnApplicationShutdown failed");
            }

        LoadedPlugins.Clear();
    }

    public static List<PluginInfo> GetAllPlugins()
    {
        return [.. _pluginInfos];
    }

    public static string ImportPlugin(string sourcePath)
    {
        try
        {
            Logger.Info($"Importing plugin: {sourcePath}");
            if (!File.Exists(sourcePath)) return "Plugin file not found";

            var fileName = Path.GetFileName(sourcePath);
            var pluginId = Guid.NewGuid().ToString("N");
            var targetPath = Path.Combine(PluginsDir, $"{pluginId}_{fileName}");

            var manifest = ExtractManifest(sourcePath);
            if (manifest == null) return "Invalid plugin: Missing manifest or IPlugin implementation";

            if (_pluginInfos.Any(p => string.Equals(p.Name, manifest.Name, StringComparison.OrdinalIgnoreCase)))
                return $"Plugin '{manifest.Name}' is already installed";

            File.Copy(sourcePath, targetPath, true);

            var pluginInfo = new PluginInfo
            {
                Name = manifest.Name,
                Version = manifest.Version,
                Author = manifest.Author,
                Description = manifest.Description,
                FileName = Path.GetFileName(targetPath),
                IsEnabled = true,
                Id = pluginId
            };

            _pluginInfos.Add(pluginInfo);
            SavePluginInfos();

            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Import failed for: {sourcePath}");
            return $"Import failed: {ex.Message}";
        }
    }

    private static PluginManifest? ExtractManifest(string assemblyPath)
    {
        AssemblyLoadContext? context = null;
        try
        {
            context = new AssemblyLoadContext(null, true);
            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var pluginType = assembly.GetTypes()
                .FirstOrDefault(t =>
                    typeof(IPlugin).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false });

            if (pluginType == null) return null;

            var instance = (IPlugin?)Activator.CreateInstance(pluginType);
            if (instance == null) return null;

            return new PluginManifest
            {
                Name = instance.Name,
                Version = instance.Version,
                Author = instance.Author,
                Description = instance.Description
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to extract manifest from: {assemblyPath}");
            return null;
        }
        finally
        {
            context?.Unload();
        }
    }

    public static void RemovePlugin(PluginInfo pluginInfo)
    {
        try
        {
            Logger.Info($"Removing plugin: {pluginInfo.Name}");
            var pluginPath = Path.Combine(PluginsDir, pluginInfo.FileName);

            var loaded = LoadedPlugins.FirstOrDefault(p => p.Info.Id == pluginInfo.Id);
            if (loaded.Context != null)
            {
                try
                {
                    loaded.Instance?.OnApplicationShutdown();
                    loaded.Context.Unload();
                }
                catch
                {
                    // ignored
                }

                LoadedPlugins.Remove(loaded);

                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            _pluginInfos.RemoveAll(p => p.Id == pluginInfo.Id);
            SavePluginInfos();

            if (File.Exists(pluginPath))
                try
                {
                    File.Delete(pluginPath);
                }
                catch
                {
                    MarkForDeletion(pluginPath);
                }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to remove plugin: {pluginInfo.Name}");
        }
    }

    private static void MarkForDeletion(string path)
    {
        try
        {
            var list = LoadPendingDeletes();
            if (!list.Contains(path)) list.Add(path);
            SavePendingDeletes(list);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to mark for deletion: {path}");
        }
    }

    private static List<string> LoadPendingDeletes() => LoadJsonList<string>(PendingDeletesPath);

    private static void SavePendingDeletes(List<string> paths) => SaveJsonList(PendingDeletesPath, paths);

    private static void CleanupPendingDeletes()
    {
        try
        {
            if (!File.Exists(PendingDeletesPath)) return;

            var paths = LoadPendingDeletes();
            var remaining = new List<string>();

            foreach (var path in paths)
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch
                {
                    remaining.Add(path);
                }

            if (remaining.Count > 0)
                SavePendingDeletes(remaining);
            else
                File.Delete(PendingDeletesPath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Cleanup of pending deletes failed");
        }
    }

    public static void TogglePlugin(PluginInfo pluginInfo, bool enabled)
    {
        try
        {
            var info = _pluginInfos.FirstOrDefault(p => p.Id == pluginInfo.Id);
            if (info == null) return;

            info.IsEnabled = enabled;
            SavePluginInfos();

            if (!enabled)
            {
                var loaded = LoadedPlugins.FirstOrDefault(p => p.Info.Id == pluginInfo.Id);
                if (loaded.Context != null)
                {
                    try
                    {
                        loaded.Instance?.OnApplicationShutdown();
                        loaded.Context.Unload();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"Error unloading plugin: {pluginInfo.Name}");
                    }

                    LoadedPlugins.Remove(loaded);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to toggle plugin: {pluginInfo.Name}");
        }
    }

    public static List<IPlugin> GetEnabledPlugins()
    {
        return
        [
            .. LoadedPlugins
                .Where(p => p.Instance != null)
                .Select(p => p.Instance!)
        ];
    }

    private static List<T> LoadJsonList<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to load JSON list from: {path}");
            return [];
        }
    }

    private static void SaveJsonList<T>(string path, IEnumerable<T> data)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(data), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Failed to save JSON list to: {path}");
        }
    }
}