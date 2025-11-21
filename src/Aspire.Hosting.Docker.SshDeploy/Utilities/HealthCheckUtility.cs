#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Utilities;

internal static class HealthCheckUtility
{
    public static async Task CheckServiceHealth(
        string deployPath,
        ISSHConnectionManager sshConnectionManager,
        IReportingStep step,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? maxWaitTime = null,
        TimeSpan? checkInterval = null)
    {
        var maxWait = maxWaitTime ?? TimeSpan.FromMinutes(5);
        var interval = checkInterval ?? TimeSpan.FromSeconds(10);

        logger.LogDebug("Starting health check for services in {DeployPath}, timeout={MaxWait}", deployPath, maxWait);

        // Step 1: Get the initial list of services from remote
        var initialStatuses = await GetServiceStatuses(deployPath, sshConnectionManager, logger, cancellationToken);

        if (initialStatuses.Count == 0)
        {
            logger.LogWarning("No services found in {DeployPath}", deployPath);
            return;
        }

        logger.LogDebug("Monitoring {ServiceCount} services: {Services}",
            initialStatuses.Count,
            string.Join(", ", initialStatuses.Select(s => s.Name)));

        // Step 2: Poll status until all services are healthy or timeout
        var startTime = DateTime.UtcNow;
        var finalStatuses = initialStatuses;
        var pollCount = 0;

        while (DateTime.UtcNow - startTime < maxWait)
        {
            pollCount++;
            var elapsed = DateTime.UtcNow - startTime;

            // Get current status of all services
            finalStatuses = await GetServiceStatuses(deployPath, sshConnectionManager, logger, cancellationToken);

            var healthyCount = finalStatuses.Count(s => s.IsHealthy);
            var terminalCount = finalStatuses.Count(s => IsStatusTerminal(s.Status));

            logger.LogDebug("Poll #{PollCount} at {Elapsed:F1}s: {HealthyCount}/{TotalCount} healthy, {TerminalCount} terminal",
                pollCount, elapsed.TotalSeconds, healthyCount, finalStatuses.Count, terminalCount);

            // Check if all services are healthy or in terminal state
            if (finalStatuses.All(s => s.IsHealthy || IsStatusTerminal(s.Status)))
            {
                logger.LogDebug("All services reached final state after {Elapsed:F1}s ({PollCount} polls)",
                    elapsed.TotalSeconds, pollCount);
                break;
            }

            // Wait before next poll
            await Task.Delay(interval, cancellationToken);
        }

        var totalElapsed = DateTime.UtcNow - startTime;

        // Step 3: Report final status for each service
        var hasAnyFailures = false;
        foreach (var service in finalStatuses)
        {
            ReportServiceStatus(service, logger, out var hasFailure);
            hasAnyFailures = hasAnyFailures || hasFailure;
        }

        logger.LogInformation("Health check completed after {Elapsed:F1}s ({PollCount} polls)", totalElapsed.TotalSeconds, pollCount);

        // Throw if any services failed
        if (hasAnyFailures)
        {
            throw new InvalidOperationException("One or more services failed health checks");
        }
    }

    private static void ReportServiceStatus(
        ServiceStatus service,
        ILogger logger,
        out bool hasFailure)
    {
        hasFailure = false;

        try
        {
            if (service.IsHealthy)
            {
                // Service is healthy - log at debug level
                logger.LogDebug(
                    "Service {ServiceName} is healthy - Status: {Status}{Ports}",
                    service.Name,
                    service.Status,
                    string.IsNullOrEmpty(service.Ports) ? "" : $", Ports: {service.Ports}");
            }
            else if (IsStatusTerminal(service.Status))
            {
                // Service reached a terminal state
                var isSuccessfulExit = service.Status.ToLowerInvariant().Contains("exited") &&
                                     (service.Status.Contains("(0)") || service.Status.Contains("exit 0"));

                if (isSuccessfulExit)
                {
                    logger.LogDebug("Service {ServiceName} completed successfully - Status: {Status}", service.Name, service.Status);
                }
                else
                {
                    logger.LogError("Service {ServiceName} terminated unexpectedly - Status: {Status}, Details: {Details}", service.Name, service.Status, service.Details);
                    hasFailure = true;
                }
            }
            else
            {
                // Service failed to become healthy within timeout
                logger.LogError("Service {ServiceName} failed to become healthy - Status: {Status}, Details: {Details}", service.Name, service.Status, service.Details);
                hasFailure = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check status for service {ServiceName}", service.Name);
            hasFailure = true;
        }
    }

    public static async Task<List<ServiceStatus>> GetServiceStatuses(
        string deployPath,
        ISSHConnectionManager sshConnectionManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            // Try formatted output first
            logger.LogDebug("Attempting to get service statuses using formatted docker compose ps output");
            var psCommand = $"cd {deployPath} && (docker compose ps --format 'table {{{{.Name}}}}\\t{{{{.Service}}}}\\t{{{{.Status}}}}\\t{{{{.Ports}}}}' || docker-compose ps --format 'table {{{{.Name}}}}\\t{{{{.Service}}}}\\t{{{{.Status}}}}\\t{{{{.Ports}}}}' || true)";
            var result = await sshConnectionManager.ExecuteCommandWithOutputAsync(psCommand, cancellationToken);

            if (result.ExitCode == 0 && !string.IsNullOrEmpty(result.Output))
            {
                var services = ParseFormattedDockerPs(result.Output);
                if (services.Count > 0)
                {
                    logger.LogDebug("Successfully parsed {ServiceCount} services from formatted output", services.Count);
                    return services;
                }
                logger.LogDebug("Formatted output returned no services, falling back to basic ps");
            }
            else
            {
                logger.LogDebug("Formatted docker compose ps failed (exit code {ExitCode}), falling back to basic ps", result.ExitCode);
            }

            // Fallback to basic ps command
            logger.LogDebug("Attempting to get service statuses using basic docker compose ps output");
            var fallbackCommand = $"cd {deployPath} && (docker compose ps || docker-compose ps || true)";
            var fallbackResult = await sshConnectionManager.ExecuteCommandWithOutputAsync(fallbackCommand, cancellationToken);

            if (fallbackResult.ExitCode == 0 && !string.IsNullOrEmpty(fallbackResult.Output))
            {
                var services = ParseBasicDockerPs(fallbackResult.Output);
                logger.LogDebug("Successfully parsed {ServiceCount} services from basic output", services.Count);
                return services;
            }

            logger.LogWarning("Failed to get service statuses from both formatted and basic docker compose ps commands");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting service statuses from {DeployPath}", deployPath);
            return new List<ServiceStatus>
            {
                new()
                {
                    Name = "unknown",
                    ContainerName = "unknown",
                    Status = "unknown",
                    Ports = "",
                    IsHealthy = false,
                    IsTerminal = false,
                    Details = $"Failed to get service status: {ex.Message}"
                }
            };
        }

        return new List<ServiceStatus>();
    }

    /// <summary>
    /// Parses formatted output from 'docker compose ps --format "table {{.Name}}\t{{.Service}}\t{{.Status}}\t{{.Ports}}"'.
    ///
    /// Expected format (tab-separated):
    /// NAME            SERVICE    STATUS             PORTS
    /// myapp-web-1     web        Up 2 minutes       0.0.0.0:8080->80/tcp
    /// myapp-db-1      db         Up 2 minutes       3306/tcp
    ///
    /// Malformed input handling:
    /// - Header lines (starting with "NAME" or "Container") → skipped
    /// - Lines with fewer than 3 tab-separated fields → skipped (returns null)
    /// - Missing service name (2nd field) → uses container name as service name
    /// - Missing ports (4th field) → empty string used
    /// </summary>
    private static List<ServiceStatus> ParseFormattedDockerPs(string output)
    {
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !IsHeaderLine(line))
            .Select(ParseFormattedLine)
            .Where(service => service != null)
            .ToList()!;
    }

    private static bool IsHeaderLine(string line)
    {
        return line.StartsWith("NAME") || line.StartsWith("Container") || string.IsNullOrWhiteSpace(line);
    }

    private static ServiceStatus? ParseFormattedLine(string line)
    {
        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return null; // Malformed: need at least name, service, and status

        var containerName = parts[0].Trim();
        var serviceName = parts.Length > 1 ? parts[1].Trim() : containerName;
        var status = parts[2].Trim();
        var ports = parts.Length > 3 ? parts[3].Trim() : "";

        return new ServiceStatus
        {
            Name = serviceName,
            ContainerName = containerName,
            Status = status,
            Ports = ports,
            IsHealthy = IsStatusHealthy(status),
            IsTerminal = IsStatusTerminal(status),
            Details = $"Container: {containerName}, Status: {status}"
        };
    }

    /// <summary>
    /// Parses basic output from 'docker compose ps' (without formatting).
    ///
    /// Expected format (space-separated, variable columns):
    /// NAME          IMAGE      COMMAND        SERVICE    CREATED      STATUS         PORTS
    /// myapp-web-1   myapp-web  "entrypoint"   web        2 min ago    Up 2 minutes   0.0.0.0:8080->80/tcp
    /// myapp-db-1    postgres   "docker-ent"   db         2 min ago    Up 2 minutes   5432/tcp
    ///
    /// Malformed input handling:
    /// - Header lines (containing "NAME", "Container", or starting with "--") → skipped
    /// - Lines with fewer than 2 space-separated words → skipped (returns null)
    /// - Unable to extract service name from container name → uses simplified container name
    /// - Unable to find status in line → returns "unknown"
    /// - Unable to find ports in line → returns empty string
    ///
    /// Note: This parser uses heuristics to extract status and ports since column positions vary.
    /// It looks for keywords like "Up", "Exited", "Running" and port patterns.
    /// </summary>
    private static List<ServiceStatus> ParseBasicDockerPs(string output)
    {
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !IsBasicHeaderLine(line))
            .Select(ParseBasicLine)
            .Where(service => service != null)
            .ToList()!;
    }

    private static bool IsBasicHeaderLine(string line)
    {
        return line.Contains("NAME") || line.Contains("Container") || line.StartsWith("--") || string.IsNullOrWhiteSpace(line);
    }

    private static ServiceStatus? ParseBasicLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null; // Malformed: need at least container name and some status info

        var containerName = parts[0];
        var serviceName = ExtractServiceNameFromContainer(containerName);
        var status = ExtractStatusFromLine(line);

        return new ServiceStatus
        {
            Name = serviceName,
            ContainerName = containerName,
            Status = status,
            Ports = ExtractPortsFromLine(line),
            IsHealthy = IsStatusHealthy(status),
            IsTerminal = IsStatusTerminal(status),
            Details = line.Trim()
        };
    }

    private static bool IsStatusHealthy(string status)
    {
        if (string.IsNullOrEmpty(status))
            return false;

        var lowerStatus = status.ToLowerInvariant();
        
        // Consider these statuses as healthy
        return lowerStatus.Contains("up") || 
               lowerStatus.Contains("running") || 
               lowerStatus.Contains("healthy");
    }

    private static bool IsStatusTerminal(string status)
    {
        if (string.IsNullOrEmpty(status))
            return false;

        var lowerStatus = status.ToLowerInvariant();

        // Terminal states - service won't change state without intervention
        return lowerStatus.Contains("exited") ||
               lowerStatus.Contains("dead") ||
               lowerStatus.Contains("stopped") ||
               lowerStatus.Contains("killed") ||
               (lowerStatus.Contains("exit") && (lowerStatus.Contains("0") || lowerStatus.Contains("code")));
    }

    private static string ExtractServiceNameFromContainer(string containerName)
    {
        // Try to extract service name from container name
        // Common patterns: project_service_1, project-service-1, etc.
        var parts = containerName.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length >= 2)
        {
            // Usually the service name is the second-to-last part
            return parts[^2];
        }
        
        return containerName;
    }

    private static string ExtractStatusFromLine(string line)
    {
        // Look for common status indicators in the line
        var statusKeywords = new[] { "Up", "Exited", "Restarting", "Dead", "Created", "Paused" };
        
        foreach (var keyword in statusKeywords)
        {
            var index = line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                // Extract the status portion (usually includes timing info)
                var endIndex = line.IndexOf("  ", index);
                if (endIndex > index)
                {
                    return line.Substring(index, endIndex - index).Trim();
                }
                else
                {
                    return line.Substring(index).Trim();
                }
            }
        }
        
        return "unknown";
    }

    private static string ExtractPortsFromLine(string line)
    {
        // Look for port mappings in the format "0.0.0.0:8080->80/tcp"
        var portPattern = @"\d+\.\d+\.\d+\.\d+:\d+->\d+\/\w+";
        var matches = System.Text.RegularExpressions.Regex.Matches(line, portPattern);
        
        if (matches.Count > 0)
        {
            return string.Join(", ", matches.Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value));
        }
        
        return "";
    }

    public class ServiceStatus
    {
        public required string Name { get; set; }
        public required string ContainerName { get; set; }
        public required string Status { get; set; }
        public required string Ports { get; set; }
        public required bool IsHealthy { get; set; }
        public required bool IsTerminal { get; set; }
        public required string Details { get; set; }
    }
}
