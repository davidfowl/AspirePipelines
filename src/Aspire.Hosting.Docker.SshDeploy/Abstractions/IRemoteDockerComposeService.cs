using Aspire.Hosting.Docker.SshDeploy.Models;

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
    /// Authenticates with a container registry on the remote server.
    /// </summary>
    /// <param name="registryUrl">The registry URL (e.g., ghcr.io, docker.io)</param>
    /// <param name="username">Registry username</param>
    /// <param name="password">Registry password or token</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the login operation</returns>
    Task<ComposeOperationResult> LoginToRegistryAsync(string registryUrl, string username, string password, CancellationToken cancellationToken);

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

    /// <summary>
    /// Gets the current status of all services using docker compose ps --format json.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory containing docker-compose.yaml</param>
    /// <param name="host">The host address for generating service URLs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Aggregated status of all services</returns>
    Task<ComposeStatus> GetStatusAsync(string deployPath, string host, CancellationToken cancellationToken);

    /// <summary>
    /// Streams status updates at a specified interval until cancelled.
    /// </summary>
    /// <param name="deployPath">Path to the deployment directory containing docker-compose.yaml</param>
    /// <param name="host">The host address for generating service URLs</param>
    /// <param name="interval">Time between status polls</param>
    /// <param name="cancellationToken">Cancellation token to stop streaming</param>
    /// <returns>Async enumerable of status updates</returns>
    IAsyncEnumerable<ComposeStatus> StreamStatusAsync(string deployPath, string host, TimeSpan interval, CancellationToken cancellationToken);
}
