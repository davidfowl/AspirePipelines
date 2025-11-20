using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

internal static class PortInformationUtility
{
    public static async Task<Dictionary<string, List<string>>> ExtractPortInformation(string deployPath, ISSHConnectionManager sshConnectionManager, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogDebug("Extracting port information from {DeployPath}", deployPath);

        if (!sshConnectionManager.IsConnected)
        {
            logger.LogWarning("SSH connection not established, cannot extract port information");
            return new Dictionary<string, List<string>>();
        }

        var host = sshConnectionManager.SshClient?.ConnectionInfo?.Host ?? "localhost";
        var serviceUrls = new Dictionary<string, List<string>>();

        // Method 1: Try to get port mappings from running containers using docker compose ps
        // Expected output format (JSON):
        // {"ID":"abc123","Name":"myapp-web-1","Command":"docker-entrypoint.sh","Project":"myapp","Service":"web","State":"running","Health":"","ExitCode":0,"Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
        logger.LogDebug("Attempting to extract ports from docker compose ps JSON output");
        var composePortResult = await sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd {deployPath} && (docker compose ps --format json || docker-compose ps --format json) 2>/dev/null",
            cancellationToken);

        if (composePortResult.ExitCode == 0 && !string.IsNullOrEmpty(composePortResult.Output))
        {
            var containerPorts = ExtractPortsFromComposeJson(composePortResult.Output, host);
            if (containerPorts.Any())
            {
                logger.LogDebug("Successfully extracted {ServiceCount} services with ports from compose JSON", containerPorts.Count);
                return containerPorts;
            }
            logger.LogDebug("No ports found in compose JSON output, trying next method");
        }
        else
        {
            logger.LogDebug("Docker compose ps JSON command failed (exit code {ExitCode}), trying next method", composePortResult.ExitCode);
        }

        // Method 2: Try standard docker ps with port information
        // Expected output format (table):
        // NAMES                PORTS
        // myapp-web-1         0.0.0.0:8080->80/tcp, 0.0.0.0:8443->443/tcp
        // myapp-db-1          3306/tcp
        logger.LogDebug("Attempting to extract ports from docker ps table output");
        var dockerPortResult = await sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd {deployPath} && docker ps --format 'table {{{{.Names}}}}\\t{{{{.Ports}}}}' --no-trunc",
            cancellationToken);

        if (dockerPortResult.ExitCode == 0 && !string.IsNullOrEmpty(dockerPortResult.Output))
        {
            var containerPorts = ExtractPortsFromDockerPs(dockerPortResult.Output, host);
            if (containerPorts.Any())
            {
                logger.LogDebug("Successfully extracted {ServiceCount} services with ports from docker ps", containerPorts.Count);
                return containerPorts;
            }
            logger.LogDebug("No ports found in docker ps output, trying next method");
        }
        else
        {
            logger.LogDebug("Docker ps command failed (exit code {ExitCode}), trying next method", dockerPortResult.ExitCode);
        }

        // Method 3: Parse docker-compose file for configured ports
        // Expected content format (YAML):
        // services:
        //   web:
        //     ports:
        //       - "8080:80"
        //       - "8443:443"
        //     # or
        //     ports:
        //       - 3000:3000
        logger.LogDebug("Attempting to extract ports from docker-compose file");
        var composeFileResult = await sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd {deployPath} && (cat docker-compose.yml 2>/dev/null || cat docker-compose.yaml 2>/dev/null)",
            cancellationToken);

        if (composeFileResult.ExitCode == 0 && !string.IsNullOrEmpty(composeFileResult.Output))
        {
            var containerPorts = ExtractPortsFromComposeFile(composeFileResult.Output, host);
            if (containerPorts.Any())
            {
                logger.LogDebug("Successfully extracted {ServiceCount} services with ports from compose file", containerPorts.Count);
                return containerPorts;
            }
            logger.LogDebug("No ports found in compose file, trying next method");
        }
        else
        {
            logger.LogDebug("Failed to read compose file (exit code {ExitCode}), trying next method", composeFileResult.ExitCode);
        }

        // Method 4: Simple fallback - check for docker-proxy processes (these indicate exposed Docker ports)
        // Expected output format (netstat):
        // tcp        0      0 0.0.0.0:8080            0.0.0.0:*               LISTEN      1234/docker-proxy
        // tcp6       0      0 :::8443                 :::*                    LISTEN      5678/docker-proxy
        // After awk/cut processing: just the port numbers (8080, 8443, etc.)
        logger.LogDebug("Attempting to extract ports from netstat docker-proxy output");
        var netstatResult = await sshConnectionManager.ExecuteCommandWithOutputAsync(
            "netstat -tlnp 2>/dev/null | grep docker-proxy | awk '{print $4}' | sed 's/.*://' | sort -nu",
            cancellationToken);

        if (netstatResult.ExitCode == 0 && !string.IsNullOrEmpty(netstatResult.Output))
        {
            var ports = netstatResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => IsValidPortString(p.Trim()))
                .Distinct()
                .OrderBy(p => int.Parse(p));

            if (ports.Any())
            {
                // For fallback method, we don't have container names, so use a generic key
                serviceUrls["unknown-services"] = ports.Select(p => $"http://{host}:{p}").ToList();
                logger.LogDebug("Successfully extracted {PortCount} ports from netstat", ports.Count());
                return serviceUrls;
            }
            logger.LogDebug("No ports found in netstat output");
        }
        else
        {
            logger.LogDebug("Netstat command failed (exit code {ExitCode})", netstatResult.ExitCode);
        }

        logger.LogWarning("Failed to extract port information using all available methods");
        return serviceUrls;
    }

    /// <summary>
    /// Parses JSON output from 'docker compose ps --format json'.
    ///
    /// Expected format (one JSON object per line):
    /// {"ID":"abc123","Name":"myapp-web-1","Service":"web","State":"running","Publishers":[{"TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
    ///
    /// Malformed input handling:
    /// - Invalid JSON lines are skipped (caught by TryParseContainerJson)
    /// - Missing Name field → line skipped
    /// - Missing Publishers → line skipped
    /// - Invalid port numbers (0, negative, >65535) → port skipped but other ports for that service are kept
    /// </summary>
    private static Dictionary<string, List<string>> ExtractPortsFromComposeJson(string jsonOutput, string host)
    {
        var serviceUrls = new Dictionary<string, List<string>>();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var line in jsonOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseContainerJson(line, jsonOptions, out var container) || container.Publishers == null)
                continue;

            var urls = container.Publishers
                .Where(p => IsValidPort(p.PublishedPort))
                .Select(p => $"http://{host}:{p.PublishedPort}")
                .ToList();

            if (urls.Count > 0)
            {
                serviceUrls[container.Name!] = urls;
            }
        }

        return serviceUrls;
    }

    private static bool TryParseContainerJson(string line, JsonSerializerOptions options, out DockerComposeContainer container)
    {
        try
        {
            container = JsonSerializer.Deserialize<DockerComposeContainer>(line, options)!;
            return container?.Name != null;
        }
        catch (JsonException)
        {
            // Malformed JSON - skip this line
            container = null!;
            return false;
        }
    }

    /// <summary>
    /// Parses table output from 'docker ps --format "table {{.Names}}\t{{.Ports}}"'.
    ///
    /// Expected format (tab-separated):
    /// NAMES              PORTS
    /// myapp-web-1        0.0.0.0:8080->80/tcp, 0.0.0.0:8443->443/tcp
    /// myapp-db-1         3306/tcp
    ///
    /// Malformed input handling:
    /// - Lines without tabs → skipped
    /// - Lines without port mappings (no "->") → skipped (e.g., internal-only ports like "3306/tcp")
    /// - Invalid port numbers → individual port skipped but other ports for that container are kept
    /// - Duplicate ports → deduplicated with Distinct()
    /// </summary>
    private static Dictionary<string, List<string>> ExtractPortsFromDockerPs(string dockerPsOutput, string host)
    {
        var serviceUrls = new Dictionary<string, List<string>>();
        var portRegex = new System.Text.RegularExpressions.Regex(@"0\.0\.0\.0:(\d+)->");

        foreach (var line in dockerPsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)) // Skip header
        {
            var parts = line.Split('\t', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[1].Contains("->"))
                continue;

            var containerName = parts[0].Trim();
            var urls = portRegex.Matches(parts[1])
                .Select(m => m.Groups[1].Value)
                .Where(IsValidPortString)
                .Select(port => $"http://{host}:{port}")
                .Distinct()
                .ToList();

            if (urls.Count > 0)
            {
                serviceUrls[containerName] = urls;
            }
        }

        return serviceUrls;
    }

    /// <summary>
    /// Parses docker-compose.yml/yaml files to extract port mappings.
    ///
    /// Expected format (YAML with 2-space indentation):
    /// services:
    ///   web:
    ///     ports:
    ///       - "8080:80"
    ///       - 3000:3000
    ///   api:
    ///     ports:
    ///       - "8443:443"
    ///
    /// Malformed input handling:
    /// - Incorrect indentation → service/port section may not be detected (gracefully skipped)
    /// - Missing services section → returns empty dictionary
    /// - Port format not matching regex → individual port skipped
    /// - Invalid port numbers → individual port skipped but other ports for that service are kept
    /// - Duplicate ports → deduplicated at the end with Distinct()
    /// - Comments and non-port lines → ignored
    ///
    /// Note: This is a simple YAML parser for the specific port mapping use case.
    /// It does not handle all YAML features (anchors, multi-line strings, etc.).
    /// </summary>
    private static Dictionary<string, List<string>> ExtractPortsFromComposeFile(string composeContent, string host)
    {
        var serviceUrls = new Dictionary<string, List<string>>();
        var portRegex = new System.Text.RegularExpressions.Regex(@"(?:^-\s*[""']?|^[""']?)(\d+):(?:\d+)(?:[""']?\s*$|[""']?$)");

        string? currentService = null;
        bool inPortsSection = false;

        foreach (var line in composeContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();

            // Top-level section (no leading whitespace)
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && trimmed.EndsWith(':') && !trimmed.StartsWith('#'))
            {
                inPortsSection = false;
                currentService = trimmed == "services:" ? null : null; // Reset on any top-level section
                continue;
            }

            // Service definition (2-space indent)
            if (currentService == null && line.StartsWith("  ") && !line.StartsWith("    ") && trimmed.EndsWith(':'))
            {
                currentService = trimmed.TrimEnd(':');
                inPortsSection = false;
                continue;
            }

            // Ports section marker
            if (currentService != null && trimmed == "ports:")
            {
                inPortsSection = true;
                continue;
            }

            // Another service property (exits ports section)
            if (currentService != null && inPortsSection && line.StartsWith("    ") && trimmed.EndsWith(':') && !trimmed.StartsWith('-'))
            {
                inPortsSection = false;
            }

            // Parse port mapping in ports section
            if (currentService != null && inPortsSection)
            {
                var match = portRegex.Match(trimmed);
                if (match.Success && IsValidPortString(match.Groups[1].Value))
                {
                    if (!serviceUrls.ContainsKey(currentService))
                    {
                        serviceUrls[currentService] = new List<string>();
                    }
                    serviceUrls[currentService].Add($"http://{host}:{match.Groups[1].Value}");
                }
            }
        }

        // Remove duplicates
        return serviceUrls.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Distinct().ToList());
    }

    private static bool IsValidPort(int port)
    {
        // Valid TCP/UDP port range is 1-65535
        // Exclude 0 and negative numbers, and ports above 65535
        return port is > 0 and <= 65535;
    }

    private static bool IsValidPortString(string portString)
    {
        return int.TryParse(portString, out var port) && IsValidPort(port);
    }
    
    // Models for docker compose ps --format json output
    public class DockerComposeContainer
    {
        public string? ID { get; set; }
        public string? Name { get; set; }
        public string? Command { get; set; }
        public string? Project { get; set; }
        public string? Service { get; set; }
        public string? State { get; set; }
        public string? Health { get; set; }
        public int ExitCode { get; set; }
        public List<PortPublisher>? Publishers { get; set; }
    }

    public class PortPublisher
    {
        public string? URL { get; set; }
        public int TargetPort { get; set; }
        public int PublishedPort { get; set; }
        public string? Protocol { get; set; }
    }

    public static string FormatServiceUrlsAsTable(Dictionary<string, List<string>> serviceUrls)
    {
        if (serviceUrls.Count == 0)
        {
            return "No exposed ports detected";
        }

        // Remove duplicates and clean up the data
        var cleanedServiceUrls = new Dictionary<string, List<string>>();
        foreach (var (serviceName, urls) in serviceUrls)
        {
            var uniqueUrls = urls.Distinct().OrderBy(u => u).ToList();
            if (uniqueUrls.Count > 0)
            {
                cleanedServiceUrls[serviceName] = uniqueUrls;
            }
        }

        // Remove common prefix from service names if applicable
        var serviceNames = cleanedServiceUrls.Keys.ToList();
        var commonPrefix = FindCommonPrefix(serviceNames);

        // Only remove prefix if it's meaningful (at least 3 characters and applies to multiple services)
        var displayServiceUrls = cleanedServiceUrls;
        if (commonPrefix.Length >= 3 && serviceNames.Count > 1)
        {
            displayServiceUrls = new Dictionary<string, List<string>>();
            foreach (var (serviceName, urls) in cleanedServiceUrls)
            {
                var displayName = serviceName.StartsWith(commonPrefix)
                    ? serviceName[commonPrefix.Length..].TrimStart('-', '_', '.')
                    : serviceName;

                // Ensure we don't end up with empty names
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = serviceName;
                }

                displayServiceUrls[displayName] = urls;
            }
        }

        var lines = new List<string>();

        // Calculate max widths for better formatting
        var maxServiceNameWidth = Math.Max(15, displayServiceUrls.Keys.Max(k => k.Length));
        
        // Calculate max URL width accounting for emoji and formatting
        var maxUrlContentWidth = 0;
        foreach (var (serviceName, urls) in displayServiceUrls)
        {
            if (urls.Count == 0)
            {
                // Account for "⚠️ (no exposed ports)" - emoji + text
                maxUrlContentWidth = Math.Max(maxUrlContentWidth, "⚠️ (no exposed ports)".Length + 1); // +1 for emoji width
            }
            else
            {
                foreach (var url in urls)
                {
                    // Account for "✅ " prefix on first URL and "   " prefix on subsequent URLs
                    var formattedLength = url.Length + 3; // 3 spaces for "✅ " or "   " (emoji counts as ~2 chars visually)
                    maxUrlContentWidth = Math.Max(maxUrlContentWidth, formattedLength);
                }
            }
        }
        
        // Limit service name column width for readability, ensure URL column accounts for emoji formatting
        var serviceColWidth = Math.Min(maxServiceNameWidth, 35);
        var urlColWidth = Math.Max(maxUrlContentWidth, 25);

        // Add table header
        lines.Add("\n📋 Service URLs:");
        lines.Add("┌" + new string('─', serviceColWidth + 2) + "┬" + new string('─', urlColWidth + 2) + "┐");
        lines.Add($"│ {"Service".PadRight(serviceColWidth)} │ {"URL".PadRight(urlColWidth)} │");
        lines.Add("├" + new string('─', serviceColWidth + 2) + "┼" + new string('─', urlColWidth + 2) + "┤");

        var hasAnyUrls = false;

        // Add service URLs
        foreach (var (serviceName, urls) in displayServiceUrls.OrderBy(kvp => kvp.Key))
        {
            if (urls.Count == 0)
            {
                // Service with no exposed URLs
                var serviceCol = serviceName.Length > serviceColWidth
                    ? serviceName[..(serviceColWidth - 3)] + "..."
                    : serviceName.PadRight(serviceColWidth);
                
                var warningText = "⚠️ (no exposed ports)";
                var urlCol = warningText.PadRight(urlColWidth - 1); // -1 to account for emoji visual width
                lines.Add($"│ {serviceCol} │ {urlCol} │");
            }
            else
            {
                hasAnyUrls = true;
                // Service with URLs - show service name only on first row
                for (int i = 0; i < urls.Count; i++)
                {
                    var serviceCol = i == 0
                        ? (serviceName.Length > serviceColWidth
                            ? serviceName[..(serviceColWidth - 3)] + "..."
                            : serviceName.PadRight(serviceColWidth))
                        : "".PadRight(serviceColWidth);

                    var url = urls[i];

                    // Format URL with appropriate icon/spacing
                    var formattedUrl = i == 0 ? $"✅ {url}" : $"   {url}";
                    
                    // Account for emoji visual width when padding
                    var paddingAdjustment = i == 0 ? -1 : 0; // -1 for emoji visual width
                    formattedUrl = formattedUrl.PadRight(urlColWidth + paddingAdjustment);

                    lines.Add($"│ {serviceCol} │ {formattedUrl} │");
                }
            }
        }

        // Add table footer
        lines.Add("└" + new string('─', serviceColWidth + 2) + "┴" + new string('─', urlColWidth + 2) + "┘");

        // Add helpful note if there are URLs
        if (hasAnyUrls)
        {
            lines.Add("💡 Click or copy URLs above to access your deployed services!");
        }

        return string.Join("\n", lines);
    }

    private static string FindCommonPrefix(List<string> strings)
    {
        if (strings.Count == 0)
            return "";

        if (strings.Count == 1)
            return "";

        var prefix = strings[0];
        for (int i = 1; i < strings.Count; i++)
        {
            while (prefix.Length > 0 && !strings[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                prefix = prefix[..^1];
            }

            if (prefix.Length == 0)
                break;
        }

        return prefix;
    }
}
