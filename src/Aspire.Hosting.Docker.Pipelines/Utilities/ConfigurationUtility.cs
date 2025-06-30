using Aspire.Hosting.Docker.Pipelines.Models;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

internal static class ConfigurationUtility
{
    public static DockerSSHConfiguration GetConfigurationDefaults(IConfiguration configuration)
    {
        var config = new DockerSSHConfiguration
        {
            // Try to read from configuration with Docker SSH specific prefix
            SshHost = configuration["DockerSSH:SshHost"],
            SshUsername = configuration["DockerSSH:SshUsername"],
            SshPort = configuration["DockerSSH:SshPort"] ?? "22",
            RemoteDeployPath = configuration["DockerSSH:RemoteDeployPath"],
            RegistryUrl = configuration["DockerSSH:RegistryUrl"] ?? "docker.io",
            RegistryUsername = configuration["DockerSSH:RegistryUsername"],
            RepositoryPrefix = configuration["DockerSSH:RepositoryPrefix"]
        };

        // Handle SSH key path with resolution
        var configuredKeyPath = configuration["DockerSSH:SshKeyPath"];
        config.SshKeyPath = ResolveSSHKeyPath(configuredKeyPath);

        return config;
    }

    /// <summary>
    /// Resolves SSH key path from configuration. If the path is just a key name (no path separators),
    /// it assumes the key is in the ~/.ssh directory. Otherwise, returns the path as-is.
    /// </summary>
    public static string? ResolveSSHKeyPath(string? configuredKeyPath)
    {
        if (string.IsNullOrEmpty(configuredKeyPath))
        {
            return null;
        }

        // If the path contains directory separators, assume it's a full path
        if (configuredKeyPath.Contains(Path.DirectorySeparatorChar) ||
            configuredKeyPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return configuredKeyPath;
        }

        // Otherwise, assume it's a key name in the ~/.ssh directory
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDir = Path.Combine(homeDir, ".ssh");
        var resolvedPath = Path.Combine(sshDir, configuredKeyPath);

        // Only return the resolved path if the file actually exists
        return File.Exists(resolvedPath) ? resolvedPath : configuredKeyPath;
    }
}
