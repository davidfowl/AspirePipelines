namespace Aspire.Hosting.Docker.Pipelines.Abstractions;

/// <summary>
/// Provides high-level operations for validating and preparing remote Docker environments.
/// </summary>
internal interface IRemoteDockerEnvironmentService
{
    /// <summary>
    /// Validates that the remote server has a properly configured Docker environment.
    /// Checks Docker installation, daemon status, Compose availability, and permissions.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Information about the Docker environment</returns>
    Task<DockerEnvironmentInfo> ValidateDockerEnvironmentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Prepares a directory on the remote server for deployment.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The deployment path that was created</returns>
    Task<string> PrepareDeploymentDirectoryAsync(string deployPath, CancellationToken cancellationToken);

    /// <summary>
    /// Gets information about the current deployment state in a directory.
    /// </summary>
    /// <param name="deployPath">Path to check for existing deployments</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Information about the deployment state</returns>
    Task<DeploymentState> GetDeploymentStateAsync(string deployPath, CancellationToken cancellationToken);
}

/// <summary>
/// Information about a Docker environment on a remote server.
/// </summary>
internal record DockerEnvironmentInfo(
    string DockerVersion,
    string ServerVersion,
    string ComposeVersion,
    bool HasPermissions);

/// <summary>
/// Information about the deployment state in a directory.
/// </summary>
internal record DeploymentState(
    int ExistingContainerCount,
    bool HasPreviousDeployment);
