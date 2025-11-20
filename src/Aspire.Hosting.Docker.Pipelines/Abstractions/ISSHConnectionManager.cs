#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.Pipelines.Models;
using Aspire.Hosting.Pipelines;
using Renci.SshNet;

namespace Aspire.Hosting.Docker.Pipelines.Abstractions;

/// <summary>
/// Manages SSH and SCP connections for remote operations.
/// </summary>
internal interface ISSHConnectionManager : IAsyncDisposable
{
    /// <summary>
    /// Gets whether the SSH connection is currently established.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the underlying SSH client for advanced operations (e.g., utilities that need direct access).
    /// Returns null if not connected.
    /// </summary>
    SshClient? SshClient { get; }

    /// <summary>
    /// Establishes and tests an SSH connection using the provided context.
    /// </summary>
    Task EstablishConnectionAsync(
        SSHConnectionContext context,
        IReportingStep step,
        CancellationToken cancellationToken);

    /// <summary>
    /// Executes a command on the remote server and returns the exit code.
    /// </summary>
    Task<int> ExecuteCommandAsync(string command, CancellationToken cancellationToken);

    /// <summary>
    /// Executes a command on the remote server and captures output and error streams.
    /// </summary>
    Task<(int ExitCode, string Output, string Error)> ExecuteCommandWithOutputAsync(
        string command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Transfers a file from the local machine to the remote server using SCP.
    /// </summary>
    Task TransferFileAsync(
        string localPath,
        string remotePath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Disconnects and cleans up the SSH and SCP connections.
    /// </summary>
    Task DisconnectAsync();
}
