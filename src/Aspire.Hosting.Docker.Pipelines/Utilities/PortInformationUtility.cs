using Renci.SshNet;
using System.Text.Json;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

public static class PortInformationUtility
{
    public static async Task<string> ExtractPortInformation(string deployPath, SshClient sshClient, CancellationToken cancellationToken)
    {
        if (sshClient == null || !sshClient.IsConnected)
        {
            return "SSH connection not available";
        }

        var host = sshClient.ConnectionInfo?.Host ?? "localhost";

        // Method 1: Try to get port mappings from running containers using docker compose ps
        // Expected output format (JSON):
        // {"ID":"abc123","Name":"myapp-web-1","Command":"docker-entrypoint.sh","Project":"myapp","Service":"web","State":"running","Health":"","ExitCode":0,"Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
        var composePortResult = await ExecuteSSHCommandWithOutput(
            sshClient,
            $"cd {deployPath} && (docker compose ps --format json || docker-compose ps --format json) 2>/dev/null",
            cancellationToken);

        if (composePortResult.ExitCode == 0 && !string.IsNullOrEmpty(composePortResult.Output))
        {
            var ports = ExtractPortsFromComposeJson(composePortResult.Output);
            if (ports.Any())
            {
                return $"Service URLs: {string.Join(", ", ports.Select(p => $"http://{host}:{p}"))}";
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
            var ports = ExtractPortsFromDockerPs(dockerPortResult.Output);
            if (ports.Any())
            {
                return $"Container URLs: {string.Join(", ", ports.Select(p => $"http://{host}:{p}"))}";
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
            var ports = ExtractPortsFromComposeFile(composeFileResult.Output);
            if (ports.Any())
            {
                return $"Configured URLs: {string.Join(", ", ports.Select(p => $"http://{host}:{p}"))}";
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
                return $"Exposed URLs: {string.Join(", ", ports.Select(p => $"http://{host}:{p}"))}";
            }
        }

        return "No exposed ports detected";
    }

    private static List<string> ExtractPortsFromComposeJson(string jsonOutput)
    {
        // Parse JSON output from docker compose ps --format json
        // Example input line: {"Publishers":[{"URL":"","TargetPort":80,"PublishedPort":8080,"Protocol":"tcp"}]}
        // We want to extract the PublishedPort values (8080 in this example)
        var ports = new List<string>();

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
                    if (container?.Publishers != null)
                    {
                        foreach (var publisher in container.Publishers)
                        {
                            // Exclude invalid ports: 0, negative numbers, and ports outside valid range (1-65535)
                            if (IsValidPort(publisher.PublishedPort))
                            {
                                ports.Add(publisher.PublishedPort.ToString());
                            }
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
            // Ignore parsing errors and return empty list
        }

        return ports.Distinct().ToList();
    }

    private static List<string> ExtractPortsFromDockerPs(string dockerPsOutput)
    {
        // Parse docker ps table output
        // Example input: "myapp-web-1    0.0.0.0:8080->80/tcp, 0.0.0.0:8443->443/tcp"
        // We want to extract the host ports (8080, 8443 in this example)
        var ports = new List<string>();
        var lines = dockerPsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines.Skip(1)) // Skip header
        {
            if (line.Contains("->"))
            {
                // Extract ports from format like "0.0.0.0:8080->80/tcp"
                var portMatches = System.Text.RegularExpressions.Regex.Matches(
                    line, @"0\.0\.0\.0:(\d+)->");

                foreach (System.Text.RegularExpressions.Match match in portMatches)
                {
                    var portString = match.Groups[1].Value;
                    if (IsValidPortString(portString))
                    {
                        ports.Add(portString);
                    }
                }
            }
        }
        return ports.Distinct().ToList();
    }

    private static List<string> ExtractPortsFromComposeFile(string composeContent)
    {
        // Parse docker-compose.yml/yaml content for port mappings
        // Example input lines we're looking for:
        //   - "8080:80"
        //   - 3000:3000
        //   ports:
        //     - "8443:443"
        // We want to extract the host ports (8080, 3000, 8443 in these examples)
        var ports = new List<string>();
        var lines = composeContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Match port mappings like "8080:80", "- 8080:80", "- "8080:80""
            var portMatch = System.Text.RegularExpressions.Regex.Match(
                trimmedLine, @"(?:^-\s*[""']?|^[""']?)(\d+):(?:\d+)(?:[""']?\s*$|[""']?$)");

            if (portMatch.Success)
            {
                var portString = portMatch.Groups[1].Value;
                if (IsValidPortString(portString))
                {
                    ports.Add(portString);
                }
            }
        }
        return ports.Distinct().ToList();
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
