#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Docker.SshDeploy.Abstractions;

/// <summary>
/// Factory interface for creating SSH connection managers.
/// </summary>
internal interface ISSHConnectionFactory
{
    /// <summary>
    /// Creates and establishes an SSH connection.
    /// </summary>
    /// <param name="context">The pipeline step context.</param>
    /// <param name="step">The reporting step for progress updates.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected SSH connection manager.</returns>
    Task<ISSHConnectionManager> CreateConnectedManagerAsync(
        PipelineStepContext context,
        IReportingStep step,
        CancellationToken cancellationToken);
}
