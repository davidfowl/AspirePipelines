namespace Aspire.Hosting.Docker.SshDeploy.Abstractions;

/// <summary>
/// Provides high-level operations for inspecting running services on remote servers.
/// </summary>
internal interface IRemoteServiceInspectionService
{
    /// <summary>
    /// Gets logs from a running service.
    /// </summary>
    /// <param name="serviceName">Name of the service/container</param>
    /// <param name="tailLines">Number of lines to retrieve from the end of the logs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The log output</returns>
    Task<string> GetServiceLogsAsync(
        string serviceName,
        int tailLines,
        CancellationToken cancellationToken);

    /// <summary>
    /// Extracts the dashboard login token from service logs.
    /// Polls the logs for a specified timeout looking for the token.
    /// </summary>
    /// <param name="serviceName">Name of the dashboard service</param>
    /// <param name="timeout">Maximum time to wait for the token to appear</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The dashboard token, or null if not found within the timeout</returns>
    Task<string?> ExtractDashboardTokenAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
