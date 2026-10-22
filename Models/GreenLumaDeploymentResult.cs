namespace GreenLuma_Manager.Models;

public class GreenLumaDeploymentResult
{
    public bool Success { get; set; }
    public List<string> DeployedFiles { get; set; } = [];
    public bool ProxyDeployed { get; set; }
    public string? ProxyDllName { get; set; }
    public string? ProxyWarning { get; set; }
    public string? ErrorMessage { get; set; }
}
