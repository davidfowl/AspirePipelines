#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

internal static class SSHConfigurationDiscovery
{
    public static Task<SSHConfiguration> DiscoverSSHConfiguration(DeployingContext context)
    {
        var config = new SSHConfiguration();

        // Try to detect default SSH username from environment
        config.DefaultUsername = "root";
        // Look for SSH keys and known hosts in common locations
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDir = Path.Combine(homeDir, ".ssh");

        if (Directory.Exists(sshDir))
        {
            // Discover all SSH keys in the .ssh directory
            var commonKeyFiles = new[] { "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa" };

            // First, add common key files in preferred order
            var orderedKeyFiles = new List<string>();
            foreach (var commonKey in commonKeyFiles)
            {
                var keyPath = Path.Combine(sshDir, commonKey);
                if (File.Exists(keyPath))
                {
                    orderedKeyFiles.Add(keyPath);
                }
            }

            // Then add any other SSH-like keys that aren't in the common list
            var otherKeyFiles = Directory.GetFiles(sshDir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).EndsWith(".pub") && !Path.GetFileName(f).EndsWith(".ppk"))
                .Where(f => !commonKeyFiles.Contains(Path.GetFileName(f)) && SSHUtility.IsLikelySSHKey(f))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            orderedKeyFiles.AddRange(otherKeyFiles);

            foreach (var keyPath in orderedKeyFiles)
            {
                try
                {
                    // Just add the path if the file exists and is readable
                    if (File.Exists(keyPath))
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
            var knownHostsPath = Path.Combine(sshDir, "known_hosts");
            if (File.Exists(knownHostsPath))
            {
                ParseKnownHostsFile(knownHostsPath, config.KnownHosts);
            }
        }

        config.DefaultDeployPath = $"/home/{config.DefaultUsername}/aspire-app";
        return Task.FromResult(config);
    }

    private static void ParseKnownHostsFile(string filePath, List<string> knownHosts)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
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
            Console.WriteLine($"Warning: Could not parse known_hosts file: {ex.Message}");
        }
    }
}
