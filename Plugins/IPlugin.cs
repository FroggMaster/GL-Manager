using System.Windows;
using System.Windows.Media;

namespace GreenLuma_Manager.Plugins;

public interface IPlugin
{
    string Name { get; }
    string Version { get; }
    string Author { get; }
    string Description { get; }
    Geometry Icon { get; }

    void Initialize();
    void OnApplicationStartup();
    void OnApplicationShutdown();
    void ShowUi(Window owner);
}