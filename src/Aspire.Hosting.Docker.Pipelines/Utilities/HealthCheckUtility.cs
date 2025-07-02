#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Publishing;
using Renci.SshNet;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

internal static class HealthCheckUtility
{
    public static async Task CheckServiceHealth(
        string deployPath,
        SshClient sshClient,
        IPublishingStep step,
        CancellationToken cancellationToken,
        TimeSpan? maxWaitTime = null,
        TimeSpan? checkInterval = null)
    {
        var maxWait = maxWaitTime ?? TimeSpan.FromMinutes(5);
        var interval = checkInterval ?? TimeSpan.FromSeconds(10);

        // Step 1: Get the initial list of services from remote
        var initialStatuses = await GetServiceStatuses(deployPath, sshClient, cancellationToken);
        
        if (initialStatuses.Count == 0)
        {
            // No services found
            return;
        }

        // Step 2: Create tasks for each service
        var serviceTasks = new Dictionary<string, IPublishingTask>();
        var serviceHealthy = new Dictionary<string, bool>();

        foreach (var service in initialStatuses)
        {
            var serviceTask = await step.CreateTaskAsync($"Health check: {service.Name}", cancellationToken);
            serviceTasks[service.Name] = serviceTask;
            serviceHealthy[service.Name] = false;

            // Initial status update
            await serviceTask.UpdateAsync($"Starting health check for {service.Name} - Status: {service.Status}", cancellationToken);
        }

        // Step 3: Poll status and update tasks concurrently
        var startTime = DateTime.UtcNow;
        
        try
        {
            while (DateTime.UtcNow - startTime < maxWait)
            {
                // Get current status of all services (single SSH call)
                var currentStatuses = await GetServiceStatuses(deployPath, sshClient, cancellationToken);
                var elapsedSeconds = (int)(DateTime.UtcNow - startTime).TotalSeconds;

                // Create concurrent tasks to update each service status
                var updateTasks = new List<Task>();

                foreach (var service in currentStatuses)
                {
                    if (serviceTasks.TryGetValue(service.Name, out var task) && !serviceHealthy[service.Name])
                    {
                        // Create a concurrent task to update this service's status
                        var updateTask = UpdateServiceTaskStatus(service, task, serviceHealthy, elapsedSeconds, cancellationToken);
                        updateTasks.Add(updateTask);
                    }
                }

                // Wait for all status updates to complete concurrently
                if (updateTasks.Count > 0)
                {
                    await Task.WhenAll(updateTasks);
                }

                // Check if all services are in a final state (healthy or terminal)
                if (serviceHealthy.Values.All(isProcessed => isProcessed))
                {
                    break;
                }

                // Wait before next poll
                await Task.Delay(interval, cancellationToken);
            }

            // Final check for any remaining unhealthy services
            var finalStatuses = await GetServiceStatuses(deployPath, sshClient, cancellationToken);
            var finalUpdateTasks = new List<Task>();

            foreach (var service in finalStatuses)
            {
                if (serviceTasks.TryGetValue(service.Name, out var task) && !serviceHealthy[service.Name])
                {
                    var finalUpdateTask = FinalizeServiceTaskStatus(service, task, cancellationToken);
                    finalUpdateTasks.Add(finalUpdateTask);
                }
            }

            if (finalUpdateTasks.Count > 0)
            {
                await Task.WhenAll(finalUpdateTasks);
            }
        }
        catch (Exception ex)
        {
            // Handle any polling errors by failing remaining unhealthy services
            var errorTasks = new List<Task>();
            foreach (var kvp in serviceTasks)
            {
                if (!serviceHealthy[kvp.Key])
                {
                    var errorTask = kvp.Value.FailAsync(
                        $"Health check for service {kvp.Key} failed due to polling error: {ex.Message}",
                        cancellationToken);
                    errorTasks.Add(errorTask);
                }
            }

            if (errorTasks.Count > 0)
            {
                await Task.WhenAll(errorTasks);
            }
        }

        // Dispose all tasks
        var disposeTasks = serviceTasks.Values.Select(task => task.DisposeAsync().AsTask());
        await Task.WhenAll(disposeTasks);
    }

    private static async Task UpdateServiceTaskStatus(
        ServiceStatus service,
        IPublishingTask serviceTask,
        Dictionary<string, bool> serviceHealthy,
        int elapsedSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            if (service.IsHealthy)
            {
                // Service became healthy
                await serviceTask.SucceedAsync(
                    $"Service {service.Name} is healthy - Status: {service.Status}" + 
                    (string.IsNullOrEmpty(service.Ports) ? "" : $", Ports: {service.Ports}"),
                    cancellationToken: cancellationToken);
                
                lock (serviceHealthy)
                {
                    serviceHealthy[service.Name] = true;
                }
            }
            else if (IsStatusTerminal(service.Status))
            {
                // Service reached a terminal state (exited, dead, etc.)
                var isSuccessfulExit = service.Status.ToLowerInvariant().Contains("exited") && 
                                     (service.Status.Contains("(0)") || service.Status.Contains("exit 0"));

                if (isSuccessfulExit)
                {
                    await serviceTask.SucceedAsync(
                        $"Service {service.Name} completed successfully - Status: {service.Status}",
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await serviceTask.FailAsync(
                        $"Service {service.Name} terminated unexpectedly - Status: {service.Status}, Details: {service.Details}",
                        cancellationToken);
                }
                
                lock (serviceHealthy)
                {
                    serviceHealthy[service.Name] = true; // Mark as processed since it's in terminal state
                }
            }
            else
            {
                // Service still not healthy and not terminal, update status
                await serviceTask.UpdateAsync(
                    $"Waiting for {service.Name} to become healthy - Status: {service.Status} (checking for {elapsedSeconds}s)",
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            await serviceTask.FailAsync(
                $"Failed to update status for service {service.Name}: {ex.Message}",
                cancellationToken);
            
            lock (serviceHealthy)
            {
                serviceHealthy[service.Name] = true; // Mark as "processed" to avoid further updates
            }
        }
    }

    private static async Task FinalizeServiceTaskStatus(
        ServiceStatus service,
        IPublishingTask serviceTask,
        CancellationToken cancellationToken)
    {
        try
        {
            if (service.IsHealthy)
            {
                await serviceTask.SucceedAsync(
                    $"Service {service.Name} is healthy - Status: {service.Status}" +
                    (string.IsNullOrEmpty(service.Ports) ? "" : $", Ports: {service.Ports}"),
                    cancellationToken: cancellationToken);
            }
            else if (IsStatusTerminal(service.Status))
            {
                // Service reached a terminal state
                var isSuccessfulExit = service.Status.ToLowerInvariant().Contains("exited") && 
                                     (service.Status.Contains("(0)") || service.Status.Contains("exit 0"));

                if (isSuccessfulExit)
                {
                    await serviceTask.SucceedAsync(
                        $"Service {service.Name} completed successfully - Status: {service.Status}",
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await serviceTask.FailAsync(
                        $"Service {service.Name} terminated unexpectedly - Status: {service.Status}, Details: {service.Details}",
                        cancellationToken);
                }
            }
            else
            {
                // Service failed to become healthy or reach terminal state within timeout
                await serviceTask.FailAsync(
                    $"Service {service.Name} failed to become healthy within timeout - Status: {service.Status}, Details: {service.Details}",
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            await serviceTask.FailAsync(
                $"Failed to finalize status for service {service.Name}: {ex.Message}",
                cancellationToken);
        }
    }

    public static async Task<List<ServiceStatus>> GetServiceStatuses(
        string deployPath,
        SshClient sshClient,
        CancellationToken cancellationToken)
    {
        var services = new List<ServiceStatus>();

        try
        {
            // Get detailed service information using docker compose ps with format
            var psCommand = $"cd {deployPath} && (docker compose ps --format 'table {{{{.Name}}}}\\t{{{{.Service}}}}\\t{{{{.Status}}}}\\t{{{{.Ports}}}}' || docker-compose ps --format 'table {{{{.Name}}}}\\t{{{{.Service}}}}\\t{{{{.Status}}}}\\t{{{{.Ports}}}}' || true)";
            
            using var sshCommand = sshClient.CreateCommand(psCommand);
            await sshCommand.ExecuteAsync(cancellationToken);

            if (sshCommand.ExitStatus == 0 && !string.IsNullOrEmpty(sshCommand.Result))
            {
                var lines = sshCommand.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                // Skip header line if it exists
                var dataLines = lines.Where(line => 
                    !line.StartsWith("NAME") && 
                    !line.StartsWith("Container") && 
                    !string.IsNullOrWhiteSpace(line)).ToList();

                foreach (var line in dataLines)
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        var containerName = parts[0].Trim();
                        var serviceName = parts.Length > 1 ? parts[1].Trim() : containerName;
                        var status = parts[2].Trim();
                        var ports = parts.Length > 3 ? parts[3].Trim() : "";

                        services.Add(new ServiceStatus
                        {
                            Name = serviceName,
                            ContainerName = containerName,
                            Status = status,
                            Ports = ports,
                            IsHealthy = IsStatusHealthy(status),
                            IsTerminal = IsStatusTerminal(status),
                            Details = $"Container: {containerName}, Status: {status}"
                        });
                    }
                }
            }

            // If the formatted approach didn't work, fall back to basic ps command
            if (services.Count == 0)
            {
                var fallbackCommand = $"cd {deployPath} && (docker compose ps || docker-compose ps || true)";
                using var fallbackSshCommand = sshClient.CreateCommand(fallbackCommand);
                await fallbackSshCommand.ExecuteAsync(cancellationToken);

                if (fallbackSshCommand.ExitStatus == 0 && !string.IsNullOrEmpty(fallbackSshCommand.Result))
                {
                    var lines = fallbackSshCommand.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var line in lines)
                    {
                        // Skip header lines and empty lines
                        if (line.Contains("NAME") || line.Contains("Container") || 
                            line.StartsWith("--") || string.IsNullOrWhiteSpace(line))
                            continue;

                        // Parse basic format: container_name ... status ... ports
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var containerName = parts[0];
                            var serviceName = ExtractServiceNameFromContainer(containerName);
                            var status = ExtractStatusFromLine(line);

                            services.Add(new ServiceStatus
                            {
                                Name = serviceName,
                                ContainerName = containerName,
                                Status = status,
                                Ports = ExtractPortsFromLine(line),
                                IsHealthy = IsStatusHealthy(status),
                                IsTerminal = IsStatusTerminal(status),
                                Details = line.Trim()
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // If we can't get detailed status, create a generic unknown status
            services.Add(new ServiceStatus
            {
                Name = "unknown",
                ContainerName = "unknown",
                Status = "unknown",
                Ports = "",
                IsHealthy = false,
                IsTerminal = false,
                Details = $"Failed to get service status: {ex.Message}"
            });
        }

        return services;
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

    private static bool IsStatusFinal(string status)
    {
        // A status is final if it's either healthy or terminal
        return IsStatusHealthy(status) || IsStatusTerminal(status);
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
