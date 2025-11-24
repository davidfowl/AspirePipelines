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
    private readonly ILogger<SSHConfigurationDiscovery> _logger;

    public SSHConfigurationDiscovery(IFileSystem fileSystem, ILogger<SSHConfigurationDiscovery> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public SSHConfiguration DiscoverSSHConfiguration(PipelineStepContext context)
    {
        // Get the application name from the host environment
        var appName = context.Services.GetRequiredService<IHostEnvironment>().ApplicationName.ToLowerInvariant();

        var config = new SSHConfiguration();

        // Look for SSH keys and known hosts in common locations
        var homeDir = _fileSystem.GetUserProfilePath();
        var sshDir = _fileSystem.CombinePaths(homeDir, ".ssh");

        if (_fileSystem.DirectoryExists(sshDir))
        {
            // Discover all SSH keys in the .ssh directory
            var commonKeyFiles = new[] { "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa" };

            // First, add common key files in preferred order
            var orderedKeyFiles = new List<string>();
            foreach (var commonKey in commonKeyFiles)
            {
                var keyPath = _fileSystem.CombinePaths(sshDir, commonKey);
                if (_fileSystem.FileExists(keyPath))
                {
                    orderedKeyFiles.Add(keyPath);
                }
            }

            // Then add any other SSH-like keys that aren't in the common list
            var otherKeyFiles = _fileSystem.GetFiles(sshDir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !_fileSystem.GetFileName(f).EndsWith(".pub") && !_fileSystem.GetFileName(f).EndsWith(".ppk"))
                .Where(f => !commonKeyFiles.Contains(_fileSystem.GetFileName(f)) && IsLikelySSHKey(f))
                .OrderBy(f => _fileSystem.GetFileName(f))
                .ToList();

            orderedKeyFiles.AddRange(otherKeyFiles);

            foreach (var keyPath in orderedKeyFiles)
            {
                try
                {
                    // Just add the path if the file exists and is readable
                    if (_fileSystem.FileExists(keyPath))
                    {
                        config.AvailableKeyPaths.Add(keyPath);
                    }
                }
                catch
                {
                    // Skip keys that can't be read
                    continue;
                }
            }

            // Set default key path to the first discovered key
            if (config.AvailableKeyPaths.Any())
            {
                config.DefaultKeyPath = config.AvailableKeyPaths.First();
            }

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

    private bool IsLikelySSHKey(string filePath)
    {
        try
        {
            // Check file size (SSH keys are typically between 100 bytes and 10KB)
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length is < 100 or > 10240)
                return false;

            // Read first few lines to check for SSH key headers
            var firstLines = _fileSystem.ReadAllLines(filePath).Take(3).ToArray();
            if (firstLines.Length == 0)
                return false;

            var firstLine = firstLines[0].Trim();

            // Check for common SSH key headers
            var sshKeyHeaders = new[]
            {
                "-----BEGIN OPENSSH PRIVATE KEY-----",
                "-----BEGIN RSA PRIVATE KEY-----",
                "-----BEGIN DSA PRIVATE KEY-----",
                "-----BEGIN EC PRIVATE KEY-----",
                "-----BEGIN PRIVATE KEY-----"
            };

            return sshKeyHeaders.Any(header => firstLine.StartsWith(header, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
