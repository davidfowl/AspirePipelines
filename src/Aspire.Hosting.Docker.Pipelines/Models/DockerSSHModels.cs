namespace Aspire.Hosting.Docker.Pipelines.Models;

public class SSHConfiguration
{
    public string DefaultUsername { get; set; } = string.Empty;
    public string? DefaultKeyPath { get; set; }
    public string DefaultDeployPath { get; set; } = string.Empty;
    public List<string> AvailableKeyPaths { get; set; } = new();
    public List<string> KnownHosts { get; set; } = new();
}

public class EnvironmentVariable
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsSensitive { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class DockerSSHConfiguration
{
    public string? SshHost { get; set; }
    public string? SshUsername { get; set; }
    public string? SshPort { get; set; }
    public string? SshKeyPath { get; set; }
    public string? RemoteDeployPath { get; set; }
    public string? RegistryUrl { get; set; }
    public string? RegistryUsername { get; set; }
    public string? RepositoryPrefix { get; set; }
}
