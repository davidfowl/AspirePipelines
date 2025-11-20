#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Docker.Pipelines.Abstractions;

/// <summary>
/// Provides high-level operations for monitoring deployment health and status on remote servers.
/// </summary>
internal interface IRemoteDeploymentMonitorService
{
    /// <summary>
    /// Waits for services in a deployment to become healthy.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory</param>
    /// <param name="timeout">Maximum time to wait for services to become healthy</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the health check</returns>
    Task<HealthCheckResult> WaitForHealthyAsync(
        string deployPath,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current deployment status including service health and URLs.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current deployment status</returns>
    Task<DeploymentStatus> GetStatusAsync(string deployPath, CancellationToken cancellationToken);

    /// <summary>
    /// Monitors service health with real-time reporting via IReportingStep.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory</param>
    /// <param name="step">Reporting step for progress updates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="maxWaitTime">Maximum time to wait for services</param>
    /// <param name="checkInterval">Interval between health checks</param>
    Task MonitorServiceHealthAsync(
        string deployPath,
        IReportingStep step,
        CancellationToken cancellationToken,
        TimeSpan? maxWaitTime = null,
        TimeSpan? checkInterval = null);
}

/// <summary>
/// Result of a health check operation.
/// </summary>
internal record HealthCheckResult(
    int TotalServices,
    int HealthyServices,
    List<ServiceHealthInfo> ServiceDetails);

/// <summary>
/// Current deployment status including service health and URLs.
/// </summary>
internal record DeploymentStatus(
    int TotalServices,
    int HealthyServices,
    Dictionary<string, string> ServiceUrls,
    List<ServiceHealthInfo> Services);

/// <summary>
/// Health information for a single service.
/// </summary>
internal record ServiceHealthInfo(
    string ServiceName,
    bool IsHealthy,
    string Status,
    string? Url);
