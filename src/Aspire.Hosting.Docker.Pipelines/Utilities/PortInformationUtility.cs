using Renci.SshNet;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

public static class PortInformationUtility
{
    public static async Task<string> ExtractPortInformation(string deployPath, SshClient sshClient, CancellationToken cancellationToken)
    {
        // Try to get port mappings from running containers
        var portResult = await ExecuteSSHCommandWithOutput(
            sshClient,
            $"cd {deployPath} && docker ps --format 'table {{{{.Names}}}}\\t{{{{.Ports}}}}' --filter label=com.docker.compose.project", 
            cancellationToken);

        if (!string.IsNullOrEmpty(portResult.Output))
        {
            var portMappings = portResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1) // Skip header
                .Where(line => line.Contains("->"))
                .SelectMany(line =>
                {
                    var parts = line.Trim().Split('\t');
                    if (parts.Length >= 2)
                    {
                        var portsPart = parts[1];
                        return portsPart.Split(',')
                            .Where(p => p.Contains("->"))
                            .Select(p => p.Trim().Split("->")[0].Split(':').LastOrDefault())
                            .Where(p => !string.IsNullOrEmpty(p));
                    }
                    return [];
                })
                .Distinct()
                .Where(p => int.TryParse(p, out _));

            if (portMappings.Any())
            {
                // For port URLs, we need the host information from the SSH connection
                if (sshClient?.ConnectionInfo?.Host != null)
                {
                    return $"Accessible URLs: {string.Join(", ", portMappings.Select(p => $"http://{sshClient.ConnectionInfo.Host}:{p}"))}";
                }
                else
                {
                    return $"Accessible ports: {string.Join(", ", portMappings)}";
                }
            }
        }

        // Fallback: try to extract from docker-compose.yml or docker-compose.yaml
        var composePortResult = await ExecuteSSHCommandWithOutput(
            sshClient,
            $"cd {deployPath} && (grep -E '^\\s*-\\s*[\"']*[0-9]+:[0-9]+[\"']*' docker-compose.yml || grep -E '^\\s*-\\s*[\"']*[0-9]+:[0-9]+[\"']*' docker-compose.yaml) 2>/dev/null | sed 's/.*\"\\([0-9]*\\):.*/\\1/' | sort -u", 
            cancellationToken);

        if (!string.IsNullOrEmpty(composePortResult.Output))
        {
            var ports = composePortResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => int.TryParse(p.Trim(), out _))
                .Distinct();

            if (ports.Any())
            {
                if (sshClient?.ConnectionInfo?.Host != null)
                {
                    return $"Configured ports: {string.Join(", ", ports.Select(p => $"http://{sshClient.ConnectionInfo.Host}:{p}"))}";
                }
                else
                {
                    return $"Configured ports: {string.Join(", ", ports)}";
                }
            }
        }

        return "Port information not available";
    }

    private static async Task<(int ExitCode, string Output, string Error)> ExecuteSSHCommandWithOutput(SshClient sshClient, string command, CancellationToken cancellationToken)
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
}
