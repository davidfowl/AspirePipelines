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
    // SSH connection configuration only
    public required string TargetHost { get; set; }
    public required string SshUsername { get; set; }
    public string? SshPassword { get; set; }
    public string? SshKeyPath { get; set; }
    public required string SshPort { get; set; }
}
