using Aspire.Hosting.Docker.SshDeploy.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IRemoteServiceInspectionService for testing.
/// Records all calls and allows pre-configuring responses.
/// </summary>
internal class FakeRemoteServiceInspectionService : IRemoteServiceInspectionService
{
    private readonly List<ServiceInspectionCall> _calls = new();
    private readonly Dictionary<string, string> _serviceLogs = new();
    private string? _configuredToken;
    private bool _shouldTokenExtractionFail;

    /// <summary>
    /// Gets all the service inspection calls that were made.
    /// </summary>
    public IReadOnlyList<ServiceInspectionCall> Calls => _calls.AsReadOnly();

    /// <summary>
    /// Configures the logs to return for a specific service.
    /// </summary>
    public void ConfigureServiceLogs(string serviceName, string logs)
    {
        _serviceLogs[serviceName] = logs;
    }

    /// <summary>
    /// Configures the dashboard token to return.
    /// </summary>
    public void ConfigureDashboardToken(string? token)
    {
        _configuredToken = token;
    }

    /// <summary>
    /// Configures token extraction to fail (return null).
    /// </summary>
    public void ConfigureTokenExtractionFailure(bool shouldFail = true)
    {
        _shouldTokenExtractionFail = shouldFail;
    }

    public Task<string> GetServiceLogsAsync(
        string serviceName,
        int tailLines,
        CancellationToken cancellationToken)
    {
        _calls.Add(new ServiceInspectionCall("GetLogs", serviceName, tailLines));

        if (_serviceLogs.TryGetValue(serviceName, out var logs))
        {
            return Task.FromResult(logs);
        }

        // Default logs
        return Task.FromResult($"[{serviceName}] Log line 1\n[{serviceName}] Log line 2\n");
    }

    public Task<string?> ExtractDashboardTokenAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _calls.Add(new ServiceInspectionCall("ExtractToken", serviceName, null, timeout));

        if (_shouldTokenExtractionFail)
        {
            return Task.FromResult<string?>(null);
        }

        var token = _configuredToken ?? "fake-dashboard-token-12345";
        return Task.FromResult<string?>(token);
    }

    /// <summary>
    /// Checks if a specific operation was performed.
    /// </summary>
    public bool WasOperationPerformed(string operation, string? serviceName = null)
    {
        return _calls.Any(c =>
            c.Operation == operation &&
            (serviceName == null || c.ServiceName == serviceName));
    }

    /// <summary>
    /// Gets the number of times an operation was performed.
    /// </summary>
    public int GetOperationCount(string operation)
    {
        return _calls.Count(c => c.Operation == operation);
    }

    /// <summary>
    /// Clears all recorded calls.
    /// </summary>
    public void ClearCalls()
    {
        _calls.Clear();
    }
}

/// <summary>
/// Represents a recorded service inspection call.
/// </summary>
public record ServiceInspectionCall(
    string Operation,
    string ServiceName,
    int? TailLines = null,
    TimeSpan? Timeout = null);
