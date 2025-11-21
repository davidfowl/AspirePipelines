#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IRemoteDeploymentMonitorService for testing.
/// Records all calls and allows pre-configuring responses.
/// </summary>
internal class FakeRemoteDeploymentMonitorService : IRemoteDeploymentMonitorService
{
    private readonly List<string> _operations = new();
    private HealthCheckResult? _configuredHealthCheckResult;
    private DeploymentStatus? _configuredDeploymentStatus;
    private bool _shouldHealthCheckFail;

    /// <summary>
    /// Gets all the operations that were performed.
    /// </summary>
    public IReadOnlyList<string> Operations => _operations.AsReadOnly();

    /// <summary>
    /// Configures the health check result to return.
    /// </summary>
    public void ConfigureHealthCheckResult(HealthCheckResult result)
    {
        _configuredHealthCheckResult = result;
    }

    /// <summary>
    /// Configures the deployment status to return.
    /// </summary>
    public void ConfigureDeploymentStatus(DeploymentStatus status)
    {
        _configuredDeploymentStatus = status;
    }

    /// <summary>
    /// Configures health check to fail.
    /// </summary>
    public void ConfigureHealthCheckFailure(bool shouldFail = true)
    {
        _shouldHealthCheckFail = shouldFail;
    }

    public Task<HealthCheckResult> WaitForHealthyAsync(
        string deployPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _operations.Add($"WaitForHealthy:{deployPath}:{timeout}");

        if (_shouldHealthCheckFail)
        {
            throw new InvalidOperationException("Health check failed (configured to fail)");
        }

        var result = _configuredHealthCheckResult ?? new HealthCheckResult(
            TotalServices: 3,
            HealthyServices: 3,
            ServiceDetails: new List<ServiceHealthInfo>
            {
                new("service1", true, "running", "http://localhost:8001"),
                new("service2", true, "running", "http://localhost:8002"),
                new("service3", true, "running", "http://localhost:8003")
            });

        return Task.FromResult(result);
    }

    public Task<DeploymentStatus> GetStatusAsync(string deployPath, CancellationToken cancellationToken)
    {
        _operations.Add($"GetStatus:{deployPath}");

        var status = _configuredDeploymentStatus ?? new DeploymentStatus(
            TotalServices: 3,
            HealthyServices: 3,
            ServiceUrls: new Dictionary<string, string>
            {
                ["service1"] = "http://localhost:8001",
                ["service2"] = "http://localhost:8002",
                ["service3"] = "http://localhost:8003"
            },
            Services: new List<ServiceHealthInfo>
            {
                new("service1", true, "running", "http://localhost:8001"),
                new("service2", true, "running", "http://localhost:8002"),
                new("service3", true, "running", "http://localhost:8003")
            });

        return Task.FromResult(status);
    }

    public Task MonitorServiceHealthAsync(
        string deployPath,
        IReportingStep step,
        CancellationToken cancellationToken,
        TimeSpan? maxWaitTime = null,
        TimeSpan? checkInterval = null)
    {
        _operations.Add($"MonitorServiceHealth:{deployPath}");

        if (_shouldHealthCheckFail)
        {
            throw new InvalidOperationException("Health check monitoring failed (configured to fail)");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks if a specific operation was performed.
    /// </summary>
    public bool WasOperationPerformed(string operation)
    {
        return _operations.Any(op => op.StartsWith(operation));
    }

    /// <summary>
    /// Gets the number of times an operation was performed.
    /// </summary>
    public int GetOperationCount(string operation)
    {
        return _operations.Count(op => op.StartsWith(operation));
    }

    /// <summary>
    /// Clears all recorded operations.
    /// </summary>
    public void ClearOperations()
    {
        _operations.Clear();
    }
}
