namespace Aspire.Hosting.Docker.Pipelines.Abstractions;

/// <summary>
/// Provides high-level operations for managing environment configuration on remote servers.
/// </summary>
internal interface IRemoteEnvironmentService
{
    /// <summary>
    /// Deploys environment configuration to the remote server.
    /// Merges local environment variables with image tags and transfers the resulting .env file.
    /// </summary>
    /// <param name="localEnvPath">Path to the local environment file</param>
    /// <param name="remoteDeployPath">Path to the remote deployment directory</param>
    /// <param name="imageTags">Dictionary of service names to image tags to merge into environment</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the environment deployment</returns>
    Task<EnvironmentDeploymentResult> DeployEnvironmentAsync(
        string localEnvPath,
        string remoteDeployPath,
        Dictionary<string, string> imageTags,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of deploying environment configuration to a remote server.
/// </summary>
internal record EnvironmentDeploymentResult(
    int VariableCount,
    string RemoteEnvPath,
    Dictionary<string, string> MergedVariables);
