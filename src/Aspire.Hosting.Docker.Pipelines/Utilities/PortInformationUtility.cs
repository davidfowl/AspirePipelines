using Renci.SshNet;
using System.Text.Json;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

public static class PortInformationUtility
{
    public static async Task<Dictionary<string, List<string>>> ExtractPortInformation(string deployPath, SshClient sshClient, CancellationToken cancellationToken)
    {
        if (sshClient == null || !sshClient.IsConnected)
        {
            return new Dictionary<string, List<string>>();
        }

        var host = sshClient.ConnectionInfo?.Host ?? "localhost";
        var serviceUrls = new Dictionary<string, List<string>>();

        // Method 1: Try to get port mappings from running containers using docker compose ps
        // Expected output format (JSON):
        // {"ID":"abc123","Name":"myapp-web-1","Command":"docker-entrypoint.sh","Project":"myapp","Service":"web","State":"running","Health":"","ExitCode":0,"Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
        var composePortResult = await ExecuteSSHCommandWithOutput(
            sshClient,
            $"cd {deployPath} && (docker compose ps --format json || docker-compose ps --format json) 2>/dev/null",
            cancellationToken);

        if (composePortResult.ExitCode == 0 && !string.IsNullOrEmpty(composePortResult.Output))
        {
            var containerPorts = ExtractPortsFromComposeJson(composePortResult.Output, host);
            if (containerPorts.Any())
            {
                return containerPorts;
            }
        }

        // Method 2: Try standard docker ps with port information
        // Expected output format (table):
        // NAMES                PORTS
        // myapp-web-1         0.0.0.0:8080->80/tcp, 0.0.0.0:8443->443/tcp
        // myapp-db-1          3306/tcp
        var dockerPortResult = await ExecuteSSHCommandWithOutput(
            sshClient,
            $"cd {deployPath} && docker ps --format 'table {{{{.Names}}}}\\t{{{{.Ports}}}}' --no-trunc",
            cancellationToken);

        if (dockerPortResult.ExitCode == 0 && !string.IsNullOrEmpty(dockerPortResult.Output))
        {
            var containerPorts = ExtractPortsFromDockerPs(dockerPortResult.Output, host);
            if (containerPorts.Any())
            {
                return containerPorts;
            }
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
        var composeFileResult = await ExecuteSSHCommandWithOutput(
            sshClient,
            $"cd {deployPath} && (cat docker-compose.yml 2>/dev/null || cat docker-compose.yaml 2>/dev/null)",
            cancellationToken);

        if (composeFileResult.ExitCode == 0 && !string.IsNullOrEmpty(composeFileResult.Output))
        {
            var containerPorts = ExtractPortsFromComposeFile(composeFileResult.Output, host);
            if (containerPorts.Any())
            {
                return containerPorts;
            }
        }

        // Method 4: Simple fallback - check for docker-proxy processes (these indicate exposed Docker ports)
        // Expected output format (netstat):
        // tcp        0      0 0.0.0.0:8080            0.0.0.0:*               LISTEN      1234/docker-proxy
        // tcp6       0      0 :::8443                 :::*                    LISTEN      5678/docker-proxy
        // After awk/cut processing: just the port numbers (8080, 8443, etc.)
        var netstatResult = await ExecuteSSHCommandWithOutput(
            sshClient,
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
                return serviceUrls;
            }
        }

        return serviceUrls;
    }

    private static Dictionary<string, List<string>> ExtractPortsFromComposeJson(string jsonOutput, string host)
    {
        // Parse JSON output from docker compose ps --format json
        // Example input line: {"Name":"myapp-web-1","Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
        // We want to extract container names and their published ports
        var serviceUrls = new Dictionary<string, List<string>>();

        try
        {
            var lines = jsonOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                try
                {
                    var container = JsonSerializer.Deserialize<DockerComposeContainer>(line, jsonOptions);
                    if (container?.Publishers != null && !string.IsNullOrEmpty(container.Name))
                    {
                        var urls = new List<string>();
                        foreach (var publisher in container.Publishers)
                        {
                            // Exclude invalid ports: 0, negative numbers, and ports outside valid range (1-65535)
                            if (IsValidPort(publisher.PublishedPort))
                            {
                                urls.Add($"http://{host}:{publisher.PublishedPort}");
                            }
                        }
                        
                        if (urls.Any())
                        {
                            serviceUrls[container.Name] = urls;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Skip invalid JSON lines and continue processing
                    continue;
                }
            }
        }
        catch (Exception)
        {
            // Ignore parsing errors and return empty dictionary
        }

        return serviceUrls;
    }

    private static Dictionary<string, List<string>> ExtractPortsFromDockerPs(string dockerPsOutput, string host)
    {
        // Parse docker ps table output
        // Example input: "myapp-web-1    0.0.0.0:8080->80/tcp, 0.0.0.0:8443->443/tcp"
        // We want to extract container names and their host ports (8080, 8443 in this example)
        var serviceUrls = new Dictionary<string, List<string>>();
        var lines = dockerPsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Skip(1)) // Skip header
        {
            var parts = line.Split('\t', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var containerName = parts[0].Trim();
                var portsSection = parts[1].Trim();
                
                if (portsSection.Contains("->"))
                {
                    var urls = new List<string>();
                    
                    // Extract ports from format like "0.0.0.0:8080->80/tcp"
                    var portMatches = System.Text.RegularExpressions.Regex.Matches(
                        portsSection, @"0\.0\.0\.0:(\d+)->");

                    foreach (System.Text.RegularExpressions.Match match in portMatches)
                    {
                        var portString = match.Groups[1].Value;
                        if (IsValidPortString(portString))
                        {
                            urls.Add($"http://{host}:{portString}");
                        }
                    }
                    
                    if (urls.Any())
                    {
                        serviceUrls[containerName] = urls.Distinct().ToList();
                    }
                }
            }
        }
        return serviceUrls;
    }

    private static Dictionary<string, List<string>> ExtractPortsFromComposeFile(string composeContent, string host)
    {
        // Parse docker-compose.yml/yaml content for port mappings
        // Example input lines we're looking for:
        //   services:
        //     web:
        //       ports:
        //         - "8080:80"
        //         - 3000:3000
        //     api:
        //       ports:
        //         - "8443:443"
        // We want to extract service names and their host ports (8080, 3000, 8443 in these examples)
        var serviceUrls = new Dictionary<string, List<string>>();
        var lines = composeContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        string? currentService = null;
        bool inPortsSection = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            var originalLine = line;

            // Check if we're starting a new service section
            if (originalLine.Length > 0 && !char.IsWhiteSpace(originalLine[0]) && trimmedLine.EndsWith(':') && !trimmedLine.StartsWith('#'))
            {
                // This is a top-level section
                if (trimmedLine == "services:")
                {
                    currentService = null;
                    inPortsSection = false;
                    continue;
                }
                else
                {
                    // Reset service context when we hit other top-level sections
                    currentService = null;
                    inPortsSection = false;
                }
            }
            
            // Check if we're in a service definition (indented under services)
            if (currentService == null && originalLine.StartsWith("  ") && !originalLine.StartsWith("    ") && trimmedLine.EndsWith(':'))
            {
                // This is a service name (2-space indented under services)
                currentService = trimmedLine.TrimEnd(':');
                inPortsSection = false;
                continue;
            }

            // Check if we're starting a ports section within a service
            if (currentService != null && trimmedLine == "ports:")
            {
                inPortsSection = true;
                continue;
            }

            // Reset ports section flag if we encounter another service property
            if (currentService != null && inPortsSection && originalLine.StartsWith("    ") && trimmedLine.EndsWith(':') && !trimmedLine.StartsWith('-'))
            {
                inPortsSection = false;
            }

            // Parse port mappings within the ports section
            if (currentService != null && inPortsSection)
            {
                // Match port mappings like "- 8080:80", "- "8080:80""
                var portMatch = System.Text.RegularExpressions.Regex.Match(
                    trimmedLine, @"(?:^-\s*[""']?|^[""']?)(\d+):(?:\d+)(?:[""']?\s*$|[""']?$)");

                if (portMatch.Success)
                {
                    var portString = portMatch.Groups[1].Value;
                    if (IsValidPortString(portString))
                    {
                        if (!serviceUrls.ContainsKey(currentService))
                        {
                            serviceUrls[currentService] = new List<string>();
                        }
                        serviceUrls[currentService].Add($"http://{host}:{portString}");
                    }
                }
            }
        }
        
        // Remove duplicates from each service's URL list
        foreach (var service in serviceUrls.Keys.ToList())
        {
            serviceUrls[service] = serviceUrls[service].Distinct().ToList();
        }
        
        return serviceUrls;
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteSSHCommandWithOutput(SshClient? sshClient, string command, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Executing SSH command: {command}");

        var startTime = DateTime.UtcNow;

        try
        {
            if (sshClient == null || !sshClient.IsConnected)
            {
                throw new InvalidOperationException("SSH connection not established");
            }

            using var sshCommand = sshClient.CreateCommand(command);
            await sshCommand.ExecuteAsync(cancellationToken);
            var result = sshCommand.Result ?? "";

            var endTime = DateTime.UtcNow;
            var exitCode = sshCommand.ExitStatus ?? -1;
            Console.WriteLine($"[DEBUG] SSH command completed in {(endTime - startTime).TotalSeconds:F1}s, exit code: {exitCode}");

            if (exitCode != 0)
            {
                Console.WriteLine($"[DEBUG] SSH error output: {sshCommand.Error}");
            }

            return (exitCode, result, sshCommand.Error ?? "");
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG] SSH command failed in {(endTime - startTime).TotalSeconds:F1}s: {ex.Message}");
            return (-1, "", ex.Message);
        }
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
}
