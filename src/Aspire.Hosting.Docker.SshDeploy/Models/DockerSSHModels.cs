namespace Aspire.Hosting.Docker.SshDeploy.Models;

internal class SSHConfiguration
{
    public string? DefaultKeyPath { get; set; }
    public string DefaultDeployPath { get; set; } = string.Empty;
    public List<string> AvailableKeyPaths { get; set; } = [];
    public List<string> KnownHosts { get; set; } = [];
}

internal class EnvironmentVariable
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSensitive { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal class DockerSSHConfiguration
{
    public string? SshHost { get; set; }
    public string? SshUsername { get; set; }
    public string? SshPort { get; set; }
    public string? SshKeyPath { get; set; }
}

internal class SSHConnectionContext
{
    public required string TargetHost { get; set; }
    public required string SshUsername { get; set; }
    /// <summary>
    /// When SshKeyPath is set, this is the passphrase for the private key.
    /// When SshKeyPath is not set, this is the password for SSH password authentication.
    /// </summary>
    public string? SshPassword { get; set; }
    public string? SshKeyPath { get; set; }
    public required string SshPort { get; set; }
}

/// <summary>
/// Represents service information from docker compose ps --format json output.
/// </summary>
internal class ComposeServiceInfo
{
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public string Health { get; set; } = "";
    public List<ComposePortPublisher> Publishers { get; set; } = [];

    public bool IsHealthy => State.Equals("running", StringComparison.OrdinalIgnoreCase);
    public bool IsTerminal => State.Equals("exited", StringComparison.OrdinalIgnoreCase)
                           || State.Equals("dead", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Represents port publisher information from docker compose ps --format json output.
/// </summary>
internal class ComposePortPublisher
{
    public string URL { get; set; } = "";
    public int TargetPort { get; set; }
    public int PublishedPort { get; set; }
    public string Protocol { get; set; } = "";
}

/// <summary>
/// Aggregated status of all services in a docker compose deployment.
/// </summary>
internal record ComposeStatus(
    List<ComposeServiceInfo> Services,
    int TotalServices,
    int HealthyServices,
    int UnhealthyServices,
    Dictionary<string, string> ServiceUrls);
