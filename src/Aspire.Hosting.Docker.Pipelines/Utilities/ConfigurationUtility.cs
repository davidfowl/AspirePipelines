using Aspire.Hosting.Docker.Pipelines.Models;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

internal static class ConfigurationUtility
{
    public static DockerSSHConfiguration GetConfigurationDefaults(IConfiguration configuration)
    {
        var config = new DockerSSHConfiguration();

        if (configuration != null)
        {
            // Try to read from configuration with Docker SSH specific prefix
            config.SshHost = configuration["DockerSSH:Host"];
            config.SshUsername = configuration["DockerSSH:Username"];
            config.SshPort = configuration["DockerSSH:Port"] ?? "22";
            config.RemoteDeployPath = configuration["DockerSSH:DeployPath"];
            config.RegistryUrl = configuration["DockerSSH:Registry:Url"] ?? "docker.io";
            config.RegistryUsername = configuration["DockerSSH:Registry:Username"];
            config.RepositoryPrefix = configuration["DockerSSH:Registry:RepositoryPrefix"];

            // Handle SSH key path with resolution
            var configuredKeyPath = configuration["DockerSSH:KeyPath"];
            config.SshKeyPath = ResolveSSHKeyPath(configuredKeyPath);
        }

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
