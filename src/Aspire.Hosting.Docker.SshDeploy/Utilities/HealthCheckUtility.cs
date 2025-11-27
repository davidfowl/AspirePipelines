#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Utilities;

internal static class HealthCheckUtility
{
    public static async Task CheckServiceHealth(
        string deployPath,
        string host,
        IRemoteDockerComposeService dockerComposeService,
        IReportingStep step,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? maxWaitTime = null,
        TimeSpan? checkInterval = null)
    {
        var maxWait = maxWaitTime ?? TimeSpan.FromMinutes(5);
        var interval = checkInterval ?? TimeSpan.FromSeconds(10);

        logger.LogDebug("Starting health check for services in {DeployPath}, timeout={MaxWait}", deployPath, maxWait);

        using var timeoutCts = new CancellationTokenSource(maxWait);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var startTime = DateTime.UtcNow;
        var pollCount = 0;
        ComposeStatus? finalStatus = null;

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

                finalStatus = status;

                // Check if all services are healthy or in terminal state
                if (status.Services.All(s => s.IsHealthy || s.IsTerminal))
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

        // Report final status
        if (finalStatus != null)
        {
            var hasAnyFailures = false;
            foreach (var service in finalStatus.Services)
            {
                ReportServiceStatus(service, logger, out var hasFailure);
                hasAnyFailures = hasAnyFailures || hasFailure;
            }

            logger.LogInformation("Health check completed after {Elapsed:F1}s ({PollCount} polls)", totalElapsed.TotalSeconds, pollCount);

            if (hasAnyFailures)
            {
                throw new InvalidOperationException("One or more services failed health checks");
            }
        }
    }

    private static void ReportServiceStatus(ComposeServiceInfo service, ILogger logger, out bool hasFailure)
    {
        hasFailure = false;

        if (service.IsHealthy)
        {
            logger.LogDebug("Service {ServiceName} is healthy - State: {State}, Status: {Status}",
                service.Service, service.State, service.Status);
        }
        else if (service.IsTerminal)
        {
            // Check if it's a successful exit (exit code 0)
            var isSuccessfulExit = service.Status.Contains("(0)") || service.Status.Contains("exit 0");

            if (isSuccessfulExit)
            {
                logger.LogDebug("Service {ServiceName} completed successfully - State: {State}", service.Service, service.State);
            }
            else
            {
                logger.LogError("Service {ServiceName} terminated unexpectedly - State: {State}, Status: {Status}",
                    service.Service, service.State, service.Status);
                hasFailure = true;
            }
        }
        else
        {
            logger.LogError("Service {ServiceName} failed to become healthy - State: {State}, Status: {Status}",
                service.Service, service.State, service.Status);
            hasFailure = true;
        }
    }
}
