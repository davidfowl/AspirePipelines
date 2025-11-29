#pragma warning disable ASPIREPIPELINES001

using System.Text;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// SSH connection manager that uses native ssh command with ControlMaster for connection reuse.
/// Leverages ssh-agent for key management instead of handling private keys directly.
/// </summary>
internal class NativeSSHConnectionManager : ISSHConnectionManager
{
    private readonly IProcessExecutor _processExecutor;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<NativeSSHConnectionManager> _logger;

    private string? _targetHost;
    private string? _username;
    private string? _port;
    private string? _controlSocketPath;
    private bool _isConnected;

    public NativeSSHConnectionManager(
        IProcessExecutor processExecutor,
        IFileSystem fileSystem,
        ILogger<NativeSSHConnectionManager> logger)
    {
        _processExecutor = processExecutor;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public bool IsConnected => _isConnected;
    public string? TargetHost => _targetHost;

    public async Task EstablishConnectionAsync(
        SSHConnectionContext context,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        _targetHost = context.TargetHost;
        _username = context.SshUsername;
        _port = context.SshPort;

        // Generate unique control socket path in ~/.aspire/temp
        // Unix sockets have max path length of ~104 chars on macOS, so keep it short
        var shortId = Guid.NewGuid().ToString("N")[..8];
        var aspireTempDir = _fileSystem.CombinePaths(_fileSystem.GetUserProfilePath(), ".aspire", "temp");
        _fileSystem.CreateDirectory(aspireTempDir);
        _controlSocketPath = _fileSystem.CombinePaths(aspireTempDir, $"ssh-{shortId}");

        await using var connectTask = await step.CreateTaskAsync(
            "Establishing SSH connection via ssh-agent", cancellationToken);

        try
        {
            _logger.LogDebug("Starting SSH ControlMaster connection to {User}@{Host}:{Port}",
                _username, _targetHost, _port);
            _logger.LogDebug("Control socket path: {ControlPath}", _controlSocketPath);

            // Establish the master connection with a simple echo command
            var sshArgs = BuildMasterConnectionArgs("echo 'SSH connection established'");

            var result = await _processExecutor.ExecuteAsync(
                "ssh",
                sshArgs,
                cancellationToken: cancellationToken);

            if (result.ExitCode != 0)
            {
                var errorMessage = !string.IsNullOrEmpty(result.Error)
                    ? result.Error
                    : "SSH connection failed. Ensure ssh-agent is running and has your key loaded.";
                throw new InvalidOperationException($"Failed to establish SSH connection: {errorMessage}");
            }

            _isConnected = true;
            _logger.LogDebug("SSH ControlMaster connection established successfully");

            // Verify connection with system info
            var verifyResult = await ExecuteCommandWithOutputAsync("whoami && pwd", cancellationToken);
            if (verifyResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"SSH verification failed: {verifyResult.Error}");
            }

            var lines = verifyResult.Output.Trim().Split('\n');
            var remoteUser = lines.Length > 0 ? lines[0].Trim() : "unknown";
            var remoteDir = lines.Length > 1 ? lines[1].Trim() : "unknown";

            _logger.LogDebug("Connected as {User} in {Directory}", remoteUser, remoteDir);

            await connectTask.SucceedAsync(
                $"Connected to {_username}@{_targetHost} via native ssh (ControlMaster)",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish SSH connection to {Host}", _targetHost);
            await CleanupControlSocket();
            throw;
        }
    }

    public async Task<int> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        var result = await ExecuteCommandWithOutputAsync(command, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"SSH command failed with exit code {result.ExitCode}: {result.Error}");
        }
        return result.ExitCode;
    }

    public async Task<(int ExitCode, string Output, string Error)> ExecuteCommandWithOutputAsync(
        string command,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        _logger.LogDebug("Executing SSH command: {Command}", command);

        var sshArgs = BuildSlaveConnectionArgs(command);

        var result = await _processExecutor.ExecuteAsync(
            "ssh",
            sshArgs,
            cancellationToken: cancellationToken);

        _logger.LogDebug("SSH command completed with exit code {ExitCode}", result.ExitCode);

        return (result.ExitCode, result.Output, result.Error);
    }

    public async Task TransferFileAsync(
        string localPath,
        string remotePath,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        _logger.LogDebug("Transferring file {LocalPath} to {RemotePath}", localPath, remotePath);

        // Expand remote path variables (e.g., $HOME) first
        var expandedPath = await ExpandRemotePathAsync(remotePath, cancellationToken);

        var scpArgs = BuildScpArgs(localPath, expandedPath);

        var result = await _processExecutor.ExecuteAsync(
            "scp",
            scpArgs,
            cancellationToken: cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"File transfer failed for {localPath}: {result.Error}");
        }

        _logger.LogDebug("File transfer completed successfully");
    }

    public async Task DisconnectAsync()
    {
        if (!string.IsNullOrEmpty(_controlSocketPath) && _isConnected)
        {
            try
            {
                _logger.LogDebug("Closing SSH ControlMaster connection");

                // Send exit command to control master
                var exitArgs = $"-S \"{_controlSocketPath}\" -O exit {_username}@{_targetHost}";

                await _processExecutor.ExecuteAsync(
                    "ssh",
                    exitArgs,
                    cancellationToken: CancellationToken.None);

                _logger.LogDebug("SSH ControlMaster closed");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing SSH ControlMaster (may already be closed)");
            }
        }

        await CleanupControlSocket();
        _isConnected = false;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }

    #region Private Helpers

    private string BuildMasterConnectionArgs(string command)
    {
        var args = new StringBuilder();

        // ControlMaster options
        args.Append($"-M ");                                    // This is the master connection
        args.Append($"-S \"{_controlSocketPath}\" ");           // Control socket path
        args.Append("-o ControlPersist=600 ");                  // Keep master alive for 10 minutes
        args.Append("-o BatchMode=yes ");                       // No interactive prompts (fail if auth fails)
        args.Append("-o StrictHostKeyChecking=accept-new ");    // Auto-accept new host keys
        args.Append("-o ConnectTimeout=30 ");                   // Connection timeout
        args.Append($"-p {_port} ");                            // Port
        args.Append($"{_username}@{_targetHost} ");             // User@host
        args.Append($"\"{EscapeShellCommand(command)}\"");      // Command to execute

        return args.ToString();
    }

    private string BuildSlaveConnectionArgs(string command)
    {
        var args = new StringBuilder();

        // Use existing control socket
        args.Append($"-S \"{_controlSocketPath}\" ");
        args.Append("-o BatchMode=yes ");
        args.Append($"-p {_port} ");
        args.Append($"{_username}@{_targetHost} ");
        args.Append($"\"{EscapeShellCommand(command)}\"");

        return args.ToString();
    }

    private string BuildScpArgs(string localPath, string remotePath)
    {
        var args = new StringBuilder();

        // Use control socket for SCP
        args.Append($"-o ControlPath=\"{_controlSocketPath}\" ");
        args.Append("-o BatchMode=yes ");
        args.Append($"-P {_port} ");                            // Note: SCP uses uppercase -P for port
        args.Append($"\"{localPath}\" ");
        args.Append($"{_username}@{_targetHost}:\"{remotePath}\"");

        return args.ToString();
    }

    private static string EscapeShellCommand(string command)
    {
        // Escape backslashes and double quotes for shell
        return command
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    private void EnsureConnected()
    {
        if (!_isConnected || string.IsNullOrEmpty(_controlSocketPath))
        {
            throw new InvalidOperationException("SSH connection not established. Call EstablishConnectionAsync first.");
        }
    }

    private async Task<string> ExpandRemotePathAsync(string remotePath, CancellationToken cancellationToken)
    {
        // If path doesn't contain shell variables, return as-is
        if (!remotePath.Contains('$') && !remotePath.Contains('~'))
        {
            return remotePath;
        }

        var result = await ExecuteCommandWithOutputAsync($"echo \"{remotePath}\"", cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            _logger.LogWarning("Failed to expand remote path {RemotePath}, using original", remotePath);
            return remotePath;
        }

        return result.Output.Trim();
    }

    private Task CleanupControlSocket()
    {
        if (!string.IsNullOrEmpty(_controlSocketPath))
        {
            try
            {
                if (_fileSystem.FileExists(_controlSocketPath))
                {
                    _fileSystem.DeleteFile(_controlSocketPath);
                    _logger.LogDebug("Cleaned up control socket file: {ControlPath}", _controlSocketPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error cleaning up control socket file");
            }

            _controlSocketPath = null;
        }

        return Task.CompletedTask;
    }

    #endregion
}
