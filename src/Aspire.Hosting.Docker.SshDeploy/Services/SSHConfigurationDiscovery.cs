#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

internal class SSHConfigurationDiscovery
{
    private readonly IFileSystem _fileSystem;
    private readonly ISshKeyDiscoveryService _sshKeyDiscoveryService;
    private readonly ILogger<SSHConfigurationDiscovery> _logger;

    public SSHConfigurationDiscovery(
        IFileSystem fileSystem,
        ISshKeyDiscoveryService sshKeyDiscoveryService,
        ILogger<SSHConfigurationDiscovery> logger)
    {
        _fileSystem = fileSystem;
        _sshKeyDiscoveryService = sshKeyDiscoveryService;
        _logger = logger;
    }

    public SSHConfiguration DiscoverSSHConfiguration(PipelineStepContext context)
    {
        // Get the application name from the host environment
        var appName = context.Services.GetRequiredService<IHostEnvironment>().ApplicationName.ToLowerInvariant();

        var config = new SSHConfiguration();

        // Use the SSH key discovery service to find available keys
        var discoveredKeys = _sshKeyDiscoveryService.DiscoverKeys();
        foreach (var key in discoveredKeys)
        {
            config.AvailableKeyPaths.Add(key.FullPath);
        }

        // Set default key path to the first discovered key
        if (config.AvailableKeyPaths.Count > 0)
        {
            config.DefaultKeyPath = config.AvailableKeyPaths[0];
        }

        // Look for known hosts in ~/.ssh
        var homeDir = _fileSystem.GetUserProfilePath();
        var sshDir = _fileSystem.CombinePaths(homeDir, ".ssh");

        if (_fileSystem.DirectoryExists(sshDir))
        {
            // Discover known hosts from SSH known_hosts file
            var knownHostsPath = _fileSystem.CombinePaths(sshDir, "known_hosts");
            if (_fileSystem.FileExists(knownHostsPath))
            {
                ParseKnownHostsFile(knownHostsPath, config.KnownHosts);
            }
        }

        config.DefaultDeployPath = $"$HOME/aspire/apps/{appName}";
        return config;
    }

    private void ParseKnownHostsFile(string filePath, List<string> knownHosts)
    {
        try
        {
            var lines = _fileSystem.ReadAllLines(filePath);
            var hostSet = new HashSet<string>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // Skip empty lines, comments, hashed entries, and special entries
                if (string.IsNullOrEmpty(trimmedLine) ||
                    trimmedLine.StartsWith("#") ||
                    trimmedLine.StartsWith("|1|") ||
                    trimmedLine.StartsWith("@"))
                {
                    continue;
                }

                // Split on whitespace to get the host part (first field)
                var parts = trimmedLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var hostPart = parts[0];

                // Handle multiple hostnames separated by commas
                var hosts = hostPart.Split(',');
                foreach (var host in hosts)
                {
                    var cleanHost = host.Trim();
                    if (string.IsNullOrEmpty(cleanHost)) continue;

                    // Remove port specification (e.g., [hostname]:port -> hostname)
                    if (cleanHost.StartsWith("[") && cleanHost.Contains("]:"))
                    {
                        var endBracket = cleanHost.IndexOf("]:");
                        if (endBracket > 1)
                        {
                            cleanHost = cleanHost[1..endBracket];
                        }
                    }

                    // Skip if still contains problematic characters (likely hashed or malformed)
                    if (cleanHost.Contains("|") || cleanHost.Contains("=")) continue;

                    hostSet.Add(cleanHost);
                }
            }

            // Add unique hosts to the known hosts list
            knownHosts.AddRange(hostSet.OrderBy(h => h));
        }
        catch (Exception ex)
        {
            // Log error but don't fail the entire discovery process
            _logger.LogWarning(ex, "Could not parse known_hosts file");
        }
    }

}
