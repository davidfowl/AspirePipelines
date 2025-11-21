namespace Aspire.Hosting.Docker.SshDeploy.Abstractions;

/// <summary>
/// Provides high-level Docker Compose operations on remote servers.
/// </summary>
internal interface IRemoteDockerComposeService
{
    /// <summary>
    /// Stops all containers in a Docker Compose deployment.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory containing docker-compose.yaml</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the stop operation</returns>
    Task<ComposeOperationResult> StopAsync(string deployPath, CancellationToken cancellationToken);

    /// <summary>
    /// Pulls the latest images for all services in a Docker Compose deployment.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory containing docker-compose.yaml</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the pull operation</returns>
    Task<ComposeOperationResult> PullImagesAsync(string deployPath, CancellationToken cancellationToken);

    /// <summary>
    /// Starts all services in a Docker Compose deployment.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory containing docker-compose.yaml</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the start operation</returns>
    Task<ComposeOperationResult> StartAsync(string deployPath, CancellationToken cancellationToken);

    /// <summary>
    /// Gets diagnostic logs from all services in a Docker Compose deployment.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory containing docker-compose.yaml</param>
    /// <param name="tailLines">Number of lines to retrieve from the end of the logs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The log output</returns>
    Task<string> GetLogsAsync(string deployPath, int tailLines, CancellationToken cancellationToken);
}

/// <summary>
/// Result of a Docker Compose operation.
/// </summary>
internal record ComposeOperationResult(
    int ExitCode,
    string Output,
    string Error,
    bool Success);
