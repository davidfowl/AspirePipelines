#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Aspire.Hosting.Docker.Pipelines.Models;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Aspire.Hosting.Docker.Pipelines.Services;

/// <summary>
/// Manages SSH and SCP connections for remote operations.
/// </summary>
internal class SSHConnectionManager : ISSHConnectionManager
{
    private readonly ILogger<SSHConnectionManager> _logger;
    private SshClient? _sshClient;
    private ScpClient? _scpClient;

    public SSHConnectionManager(ILogger<SSHConnectionManager> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _sshClient?.IsConnected == true && _scpClient?.IsConnected == true;

    public SshClient? SshClient => _sshClient;

    public async Task EstablishConnectionAsync(
        SSHConnectionContext context,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Establishing SSH connection using SSH.NET");

        // Task 1: Establish SSH connection
        await using var connectTask = await step.CreateTaskAsync("Establishing SSH connection", cancellationToken);

        try
        {
            _logger.LogDebug("Creating SSH and SCP connections...");

            var connectionInfo = CreateConnectionInfo(
                context.TargetHost,
                context.SshUsername,
                context.SshPassword,
                context.SshKeyPath,
                context.SshPort);

            _sshClient = await CreateSSHClientAsync(connectionInfo, cancellationToken);
            _scpClient = await CreateSCPClientAsync(connectionInfo, cancellationToken);

            _logger.LogDebug("SSH and SCP connections established");

            // Test basic SSH connectivity
            _logger.LogDebug("Testing SSH connectivity...");
            var testCommand = "echo 'SSH connection successful'";
            var result = await ExecuteCommandWithOutputAsync(testCommand, cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"SSH connection test failed: {result.Error}");
            }
            _logger.LogDebug("SSH connectivity test passed: {Output}", result.Output?.Trim());

            // Verify remote system access
            _logger.LogDebug("Verifying remote system access...");
            var infoCommand = "whoami && pwd && ls -la";
            var infoResult = await ExecuteCommandWithOutputAsync(infoCommand, cancellationToken);

            if (infoResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"SSH system info check failed: {infoResult.Error}");
            }
            _logger.LogDebug("Remote system access verified");

            await connectTask.SucceedAsync("SSH connection established and tested successfully", cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish SSH connection");
            await DisconnectAsync();
            throw;
        }
    }

    public async Task<int> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        var result = await ExecuteCommandWithOutputAsync(command, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"SSH command failed: {result.Error}");
        }

        return result.ExitCode;
    }

    public async Task<(int ExitCode, string Output, string Error)> ExecuteCommandWithOutputAsync(
        string command,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Executing SSH command: {Command}", command);

        var startTime = DateTime.UtcNow;

        try
        {
            if (_sshClient == null || !_sshClient.IsConnected)
            {
                throw new InvalidOperationException("SSH connection not established");
            }

            using var sshCommand = _sshClient.CreateCommand(command);
            await sshCommand.ExecuteAsync(cancellationToken);
            var result = sshCommand.Result ?? "";

            var endTime = DateTime.UtcNow;
            var exitCode = sshCommand.ExitStatus ?? -1;
            _logger.LogDebug("SSH command completed in {Duration:F1}s, exit code: {ExitCode}", (endTime - startTime).TotalSeconds, exitCode);

            if (exitCode != 0)
            {
                _logger.LogDebug("SSH error output: {Error}", sshCommand.Error);
            }

            return (exitCode, result, sshCommand.Error ?? "");
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            _logger.LogDebug("SSH command failed in {Duration:F1}s: {Message}", (endTime - startTime).TotalSeconds, ex.Message);
            return (-1, "", ex.Message);
        }
    }

    public async Task TransferFileAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Transferring file {LocalPath} to {RemotePath}", localPath, remotePath);

        try
        {
            if (_scpClient == null || !_scpClient.IsConnected)
            {
                throw new InvalidOperationException("SCP connection not established");
            }

            using var fileStream = File.OpenRead(localPath);
            await Task.Run(() => _scpClient.Upload(fileStream, remotePath), cancellationToken);
            _logger.LogDebug("File transfer completed successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"File transfer failed for {localPath}: {ex.Message}", ex);
        }
    }

    public async Task DisconnectAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                if (_sshClient != null)
                {
                    if (_sshClient.IsConnected)
                    {
                        _sshClient.Disconnect();
                    }
                    _sshClient.Dispose();
                    _sshClient = null;
                    _logger.LogDebug("SSH client connection cleaned up");
                }

                if (_scpClient != null)
                {
                    if (_scpClient.IsConnected)
                    {
                        _scpClient.Disconnect();
                    }
                    _scpClient.Dispose();
                    _scpClient = null;
                    _logger.LogDebug("SCP client connection cleaned up");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error cleaning up SSH connections: {Message}", ex.Message);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }

    private static async Task<SshClient> CreateSSHClientAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        var client = new SshClient(connectionInfo);

        await client.ConnectAsync(cancellationToken);

        if (!client.IsConnected)
        {
            client.Dispose();
            throw new InvalidOperationException("Failed to establish SSH connection");
        }

        return client;
    }

    private static async Task<ScpClient> CreateSCPClientAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        var client = new ScpClient(connectionInfo);

        await client.ConnectAsync(cancellationToken);

        if (!client.IsConnected)
        {
            client.Dispose();
            throw new InvalidOperationException("Failed to establish SCP connection");
        }

        return client;
    }

    private static ConnectionInfo CreateConnectionInfo(string host, string username, string? password, string? keyPath, string port)
    {
        var portInt = int.Parse(port);

        if (!string.IsNullOrEmpty(keyPath))
        {
            // Use key-based authentication
            var keyFile = new PrivateKeyFile(keyPath, password ?? "");
            return new ConnectionInfo(host, portInt, username, new PrivateKeyAuthenticationMethod(username, keyFile));
        }
        else if (!string.IsNullOrEmpty(password))
        {
            // Use password authentication
            return new ConnectionInfo(host, portInt, username, new PasswordAuthenticationMethod(username, password));
        }
        else
        {
            throw new InvalidOperationException("Either SSH password or SSH private key path must be provided");
        }
    }
}
