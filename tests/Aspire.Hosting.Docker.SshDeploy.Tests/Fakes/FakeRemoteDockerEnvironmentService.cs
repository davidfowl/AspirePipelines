using Aspire.Hosting.Docker.SshDeploy.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IRemoteDockerEnvironmentService for testing.
/// Records all calls and allows pre-configuring responses.
/// </summary>
internal class FakeRemoteDockerEnvironmentService : IRemoteDockerEnvironmentService
{
    private readonly List<string> _operations = new();
    private DockerEnvironmentInfo? _configuredEnvironmentInfo;
    private DeploymentState? _configuredDeploymentState;
    private bool _shouldValidationFail;
    private string? _validationFailureMessage;

    /// <summary>
    /// Gets all the operations that were performed.
    /// </summary>
    public IReadOnlyList<string> Operations => _operations.AsReadOnly();

    /// <summary>
    /// Configures the Docker environment information to return.
    /// </summary>
    public void ConfigureEnvironmentInfo(DockerEnvironmentInfo info)
    {
        _configuredEnvironmentInfo = info;
    }

    /// <summary>
    /// Configures the deployment state to return.
    /// </summary>
    public void ConfigureDeploymentState(DeploymentState state)
    {
        _configuredDeploymentState = state;
    }

    /// <summary>
    /// Configures validation to fail with a specific message.
    /// </summary>
    public void ConfigureValidationFailure(string? message = null)
    {
        _shouldValidationFail = true;
        _validationFailureMessage = message ?? "Docker validation failed (configured to fail)";
    }

    public Task<DockerEnvironmentInfo> ValidateDockerEnvironmentAsync(CancellationToken cancellationToken)
    {
        _operations.Add("ValidateDockerEnvironment");

        if (_shouldValidationFail)
        {
            throw new InvalidOperationException(_validationFailureMessage!);
        }

        var info = _configuredEnvironmentInfo ?? new DockerEnvironmentInfo(
            DockerVersion: "Docker version 24.0.0",
            ServerVersion: "24.0.0",
            ComposeVersion: "Docker Compose version v2.20.0",
            HasPermissions: true);

        return Task.FromResult(info);
    }

    public Task<string> PrepareDeploymentDirectoryAsync(string deployPath, CancellationToken cancellationToken)
    {
        _operations.Add($"PrepareDeploymentDirectory:{deployPath}");
        return Task.FromResult(deployPath);
    }

    public Task<DeploymentState> GetDeploymentStateAsync(string deployPath, CancellationToken cancellationToken)
    {
        _operations.Add($"GetDeploymentState:{deployPath}");

        var state = _configuredDeploymentState ?? new DeploymentState(
            ExistingContainerCount: 0,
            HasPreviousDeployment: false);

        return Task.FromResult(state);
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
