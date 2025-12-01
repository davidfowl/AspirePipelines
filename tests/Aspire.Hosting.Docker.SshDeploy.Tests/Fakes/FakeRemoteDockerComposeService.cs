using System.Runtime.CompilerServices;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IRemoteDockerComposeService for testing.
/// Records all calls and allows pre-configuring responses.
/// </summary>
internal class FakeRemoteDockerComposeService : IRemoteDockerComposeService
{
    private readonly List<ComposeOperation> _operations = new();
    private bool _shouldStopFail;
    private bool _shouldLoginFail;
    private bool _shouldUpWithPullFail;
    private bool _shouldPruneFail;
    private string _logsOutput = "";

    /// <summary>
    /// Gets all the compose operations that were performed.
    /// </summary>
    public IReadOnlyList<ComposeOperation> Operations => _operations.AsReadOnly();

    /// <summary>
    /// Configures the stop operation to fail.
    /// </summary>
    public void ConfigureStopFailure(bool shouldFail = true)
    {
        _shouldStopFail = shouldFail;
    }

    /// <summary>
    /// Configures the login operation to fail.
    /// </summary>
    public void ConfigureLoginFailure(bool shouldFail = true)
    {
        _shouldLoginFail = shouldFail;
    }

    /// <summary>
    /// Configures the up with pull operation to fail.
    /// </summary>
    public void ConfigureUpWithPullFailure(bool shouldFail = true)
    {
        _shouldUpWithPullFail = shouldFail;
    }

    /// <summary>
    /// Configures the prune operation to fail.
    /// </summary>
    public void ConfigurePruneFailure(bool shouldFail = true)
    {
        _shouldPruneFail = shouldFail;
    }

    /// <summary>
    /// Configures the logs output to return.
    /// </summary>
    public void ConfigureLogsOutput(string logs)
    {
        _logsOutput = logs;
    }

    public Task<ComposeOperationResult> StopAsync(string deployPath, CancellationToken cancellationToken)
    {
        _operations.Add(new ComposeOperation("Stop", deployPath));

        if (_shouldStopFail)
        {
            return Task.FromResult(new ComposeOperationResult(
                ExitCode: 1,
                Output: "",
                Error: "Stop failed (configured to fail)",
                Success: false));
        }

        return Task.FromResult(new ComposeOperationResult(
            ExitCode: 0,
            Output: "Containers stopped",
            Error: "",
            Success: true));
    }

    public Task<ComposeOperationResult> LoginToRegistryAsync(string registryUrl, string username, string password, CancellationToken cancellationToken)
    {
        _operations.Add(new ComposeOperation("Login", registryUrl, Username: username));

        if (_shouldLoginFail)
        {
            return Task.FromResult(new ComposeOperationResult(
                ExitCode: 1,
                Output: "",
                Error: "Login failed (configured to fail)",
                Success: false));
        }

        return Task.FromResult(new ComposeOperationResult(
            ExitCode: 0,
            Output: "Login Succeeded",
            Error: "",
            Success: true));
    }

    public Task<ComposeOperationResult> UpWithPullAsync(string deployPath, CancellationToken cancellationToken)
    {
        _operations.Add(new ComposeOperation("UpWithPull", deployPath));

        if (_shouldUpWithPullFail)
        {
            throw new InvalidOperationException("Failed to deploy containers (configured to fail)");
        }

        return Task.FromResult(new ComposeOperationResult(
            ExitCode: 0,
            Output: "Containers deployed",
            Error: "",
            Success: true));
    }

    public Task<ComposeOperationResult> PruneImagesAsync(CancellationToken cancellationToken)
    {
        _operations.Add(new ComposeOperation("PruneImages", ""));

        if (_shouldPruneFail)
        {
            return Task.FromResult(new ComposeOperationResult(
                ExitCode: 1,
                Output: "",
                Error: "Prune failed (configured to fail)",
                Success: false));
        }

        return Task.FromResult(new ComposeOperationResult(
            ExitCode: 0,
            Output: "Images pruned",
            Error: "",
            Success: true));
    }

    public Task<string> GetServiceLogsAsync(string containerName, int tailLines, CancellationToken cancellationToken)
    {
        _operations.Add(new ComposeOperation("GetServiceLogs", containerName, tailLines));
        return Task.FromResult(_logsOutput);
    }

    public Task<ComposeStatus> GetStatusAsync(string deployPath, string host, CancellationToken cancellationToken)
    {
        _operations.Add(new ComposeOperation("GetStatus", deployPath));
        return Task.FromResult(new ComposeStatus(
            Services: new List<ComposeServiceInfo>(),
            TotalServices: 0,
            HealthyServices: 0,
            UnhealthyServices: 0,
            FailedServices: 0,
            ServiceUrls: new Dictionary<string, List<string>>()));
    }

    public async IAsyncEnumerable<ComposeStatus> StreamStatusAsync(
        string deployPath,
        string host,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _operations.Add(new ComposeOperation("StreamStatus", deployPath));
        yield return await GetStatusAsync(deployPath, host, cancellationToken);
    }

    /// <summary>
    /// Checks if a specific operation was performed.
    /// </summary>
    public bool WasOperationPerformed(string operation, string? deployPath = null)
    {
        return _operations.Any(op =>
            op.Operation == operation &&
            (deployPath == null || op.DeployPath == deployPath));
    }

    /// <summary>
    /// Gets the number of times an operation was performed.
    /// </summary>
    public int GetOperationCount(string operation)
    {
        return _operations.Count(op => op.Operation == operation);
    }

    /// <summary>
    /// Clears all recorded operations.
    /// </summary>
    public void ClearOperations()
    {
        _operations.Clear();
    }
}

/// <summary>
/// Represents a recorded Docker Compose operation.
/// </summary>
public record ComposeOperation(string Operation, string DeployPath, int? TailLines = null, string? Username = null);
