#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Aspire.Hosting.Docker.Pipelines.Utilities;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.Pipelines.Services;

/// <summary>
/// Provides high-level operations for monitoring deployment health and status on remote servers.
/// </summary>
internal class RemoteDeploymentMonitorService : IRemoteDeploymentMonitorService
{
    private readonly ISSHConnectionManager _sshConnectionManager;
    private readonly ILogger<RemoteDeploymentMonitorService> _logger;

    public RemoteDeploymentMonitorService(
        ISSHConnectionManager sshConnectionManager,
        ILogger<RemoteDeploymentMonitorService> logger)
    {
        _sshConnectionManager = sshConnectionManager;
        _logger = logger;
    }

    public async Task<HealthCheckResult> WaitForHealthyAsync(
        string deployPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Waiting for services to become healthy in {DeployPath}, timeout={Timeout}", deployPath, timeout);

        if (_sshConnectionManager.SshClient == null)
        {
            throw new InvalidOperationException("SSH connection not established");
        }

        // Note: HealthCheckUtility.CheckServiceHealth performs the wait internally
        // This is a wrapper to provide a cleaner API
        var serviceStatuses = await HealthCheckUtility.GetServiceStatuses(
            deployPath,
            _sshConnectionManager,
            _logger,
            cancellationToken);

        var healthyCount = serviceStatuses.Count(s => s.IsHealthy);
        var totalCount = serviceStatuses.Count;

        _logger.LogDebug(
            "Health check completed: {HealthyCount}/{TotalCount} services healthy",
            healthyCount,
            totalCount);

        var serviceDetails = serviceStatuses.Select(s => new ServiceHealthInfo(
            ServiceName: s.Name,
            IsHealthy: s.IsHealthy,
            Status: s.Status,
            Url: null)).ToList();

        return new HealthCheckResult(
            TotalServices: totalCount,
            HealthyServices: healthyCount,
            ServiceDetails: serviceDetails);
    }

    public async Task<DeploymentStatus> GetStatusAsync(string deployPath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Getting deployment status for {DeployPath}", deployPath);

        if (_sshConnectionManager.SshClient == null)
        {
            throw new InvalidOperationException("SSH connection not established");
        }

        // Get service health statuses
        var serviceStatuses = await HealthCheckUtility.GetServiceStatuses(
            deployPath,
            _sshConnectionManager,
            _logger,
            cancellationToken);

        var healthyCount = serviceStatuses.Count(s => s.IsHealthy);
        var totalCount = serviceStatuses.Count;

        // Get service URLs (returns Dictionary<string, List<string>> where value is list of URLs)
        var serviceUrlsRaw = await PortInformationUtility.ExtractPortInformation(
            deployPath,
            _sshConnectionManager,
            _logger,
            cancellationToken);

        // Flatten to single URL per service (take first URL)
        var serviceUrls = serviceUrlsRaw.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.FirstOrDefault() ?? "");

        // Merge health info with URL info
        var services = serviceStatuses.Select(s =>
        {
            serviceUrls.TryGetValue(s.Name, out var url);
            return new ServiceHealthInfo(
                ServiceName: s.Name,
                IsHealthy: s.IsHealthy,
                Status: s.Status,
                Url: url);
        }).ToList();

        _logger.LogDebug(
            "Deployment status: {HealthyCount}/{TotalCount} services healthy, {UrlCount} URLs discovered",
            healthyCount,
            totalCount,
            serviceUrls.Count);

        return new DeploymentStatus(
            TotalServices: totalCount,
            HealthyServices: healthyCount,
            ServiceUrls: serviceUrls,
            Services: services);
    }

    public async Task MonitorServiceHealthAsync(
        string deployPath,
        IReportingStep step,
        CancellationToken cancellationToken,
        TimeSpan? maxWaitTime = null,
        TimeSpan? checkInterval = null)
    {
        _logger.LogDebug("Monitoring service health for {DeployPath}", deployPath);

        // Delegate to HealthCheckUtility for now
        await HealthCheckUtility.CheckServiceHealth(
            deployPath,
            _sshConnectionManager,
            step,
            _logger,
            cancellationToken,
            maxWaitTime,
            checkInterval);
    }
}
