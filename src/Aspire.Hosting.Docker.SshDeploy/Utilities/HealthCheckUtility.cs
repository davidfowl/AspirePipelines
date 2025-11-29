#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Utilities;

internal static class HealthCheckUtility
{
    public static async Task CheckServiceHealth(
        string deployPath,
        string host,
        IRemoteDockerComposeService dockerComposeService,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? maxWaitTime = null,
        TimeSpan? checkInterval = null,
        int minimumPolls = 2)
    {
        var maxWait = maxWaitTime ?? TimeSpan.FromMinutes(5);
        var interval = checkInterval ?? TimeSpan.FromSeconds(10);

        logger.LogDebug("Starting health check for services in {DeployPath}, timeout={MaxWait}", deployPath, maxWait);

        using var timeoutCts = new CancellationTokenSource(maxWait);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var startTime = DateTime.UtcNow;
        var pollCount = 0;

        try
        {
            await foreach (var status in dockerComposeService.StreamStatusAsync(deployPath, host, interval, linkedCts.Token))
            {
                pollCount++;
                var elapsed = DateTime.UtcNow - startTime;

                if (pollCount == 1 && status.TotalServices == 0)
                {
                    logger.LogWarning("No services found in {DeployPath}", deployPath);
                    return;
                }

                if (pollCount == 1)
                {
                    logger.LogDebug("Monitoring {ServiceCount} services: {Services}",
                        status.TotalServices,
                        string.Join(", ", status.Services.Select(s => s.Service)));
                }

                logger.LogDebug("Poll #{PollCount} at {Elapsed:F1}s: {HealthyCount}/{TotalCount} healthy, {TerminalCount} terminal",
                    pollCount, elapsed.TotalSeconds, status.HealthyServices, status.TotalServices,
                    status.Services.Count(s => s.IsTerminal));

                // Check if all services are healthy or in terminal state
                // Require minimum polls to catch containers that crash shortly after starting
                if (pollCount >= minimumPolls && status.Services.All(s => s.IsHealthy || s.IsTerminal))
                {
                    logger.LogDebug("All services reached final state after {Elapsed:F1}s ({PollCount} polls)",
                        elapsed.TotalSeconds, pollCount);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogWarning("Health check timed out after {MaxWait}", maxWait);
        }

        var totalElapsed = DateTime.UtcNow - startTime;
        logger.LogDebug("Health check completed after {Elapsed:F1}s ({PollCount} polls)", totalElapsed.TotalSeconds, pollCount);
    }
}
