#pragma warning disable ASPIREPIPELINES001

using System.Diagnostics;
using System.Text;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// SSH connection manager that maintains a persistent SSH session using stdin/stdout.
/// This approach keeps a single SSH connection open and sends commands through stdin,
/// avoiding the connection exhaustion issues that can occur when opening many separate
/// SSH connections (especially on Windows where ControlMaster is not supported).
/// </summary>
internal class PersistentSSHConnectionManager : ISSHConnectionManager
{
    private readonly IProcessExecutor _processExecutor;
    private readonly ILogger<PersistentSSHConnectionManager> _logger;

    private string? _targetHost;
    private string? _username;
    private string? _port;
    private int _connectTimeout;
    private bool _isConnected;

    private Process? _sshProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    private const string ReadyMarker = "___ASPIRE_READY___";

    public PersistentSSHConnectionManager(
        IProcessExecutor processExecutor,
        ILogger<PersistentSSHConnectionManager> logger)
    {
        _processExecutor = processExecutor;
        _logger = logger;
    }

    public bool IsConnected => _isConnected && _sshProcess != null && !_sshProcess.HasExited;
    public string? TargetHost => _targetHost;

    public async Task EstablishConnectionAsync(
        SSHConnectionContext context,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        _targetHost = context.TargetHost;
        _username = context.SshUsername;
        _port = context.SshPort;
        _connectTimeout = context.ConnectTimeout;

        await using var connectTask = await step.CreateTaskAsync(
            "Establishing persistent SSH session", cancellationToken);

        try
        {
            _logger.LogDebug("Starting persistent SSH session to {User}@{Host}:{Port}",
                _username, _targetHost, _port);

            // Build SSH arguments - just starts bash, we wrap each command with markers
            var sshArgs = BuildPersistentSessionArgs();

            _logger.LogDebug("SSH command: ssh {Args}", sshArgs);

            // Start SSH process with redirected stdin/stdout
            var processInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = sshArgs,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _sshProcess = new Process { StartInfo = processInfo };
            _sshProcess.Start();

            _stdin = _sshProcess.StandardInput;
            _stdout = _sshProcess.StandardOutput;
            _stderr = _sshProcess.StandardError;

            // Set up auto-flush for stdin
            _stdin.AutoFlush = true;

            // Send a ready check command - just echo the marker
            _logger.LogDebug("Sending ready check command...");
            await _stdin.WriteLineAsync($"echo {ReadyMarker}");
            await _stdin.FlushAsync();

            // Wait for the ready signal
            _logger.LogDebug("Waiting for ready signal from remote bash...");
            var readyLine = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(30), cancellationToken);
            _logger.LogDebug("Got line from remote: '{Line}'", readyLine ?? "(null)");

            if (readyLine != ReadyMarker)
            {
                var errorOutput = "";
                if (_sshProcess.HasExited)
                {
                    errorOutput = await _stderr.ReadToEndAsync(cancellationToken);
                    _logger.LogDebug("SSH process exited with code {ExitCode}", _sshProcess.ExitCode);
                }
                else
                {
                    errorOutput = await DrainStderrAsync();
                }
                throw new InvalidOperationException(
                    $"SSH connection failed. Expected ready signal, got: '{readyLine}'. Error: {errorOutput}");
            }

            _isConnected = true;
            _logger.LogDebug("Persistent SSH session ready");

            // Verify connection with a simple command
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
                $"Connected to {_username}@{_targetHost} via persistent SSH session",
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish persistent SSH session to {Host}", _targetHost);
            CleanupProcess();
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

        // Only one command can execute at a time over the persistent connection
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogDebug("Executing SSH command: {Command}", command);

            // Wrap the command with markers so we can parse the output from stdout.
            // The wrapped format is:
            //   echo ___ASPIRE_CMD_START___; { <command>; __ec=$?; } 2>&1; echo ___ASPIRE_EXIT_CODE___$__ec; echo ___ASPIRE_CMD_END___
            // This allows us to:
            //   1. Know where command output starts (after START marker)
            //   2. Capture both stdout and stderr (via 2>&1 redirect)
            //   3. Capture the exit code (from EXIT_CODE marker)
            //   4. Know when output is complete (END marker)
            var wrappedCommand = SSHOutputParser.WrapCommand(command);
            await _stdin!.WriteLineAsync(wrappedCommand);

            var outputBuilder = new StringBuilder();
            int exitCode = 0;
            bool foundStart = false;

            var timeout = TimeSpan.FromSeconds(_connectTimeout > 0 ? _connectTimeout : 120);

            // Read lines from stdout until we see the end marker
            while (true)
            {
                var line = await ReadLineWithTimeoutAsync(timeout, cancellationToken);

                if (line == null)
                {
                    // End of stream means the SSH connection was closed unexpectedly
                    var stderrContent = await DrainStderrAsync();
                    throw new InvalidOperationException(
                        $"SSH connection closed unexpectedly. Stderr: {stderrContent}");
                }

                _logger.LogTrace("SSH output line: {Line}", line);

                // ProcessLine handles the marker parsing and returns true when END marker is found
                if (SSHOutputParser.ProcessLine(line, ref foundStart, ref exitCode, outputBuilder))
                {
                    break;
                }
            }

            var output = outputBuilder.ToString().TrimEnd('\r', '\n');

            _logger.LogDebug("SSH command completed with exit code {ExitCode}", exitCode);

            return (exitCode, output, string.Empty);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _stdout!.ReadLineAsync(cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Read operation timed out after {timeout.TotalSeconds} seconds");
        }
    }

    private async Task<string> DrainStderrAsync()
    {
        var buffer = new char[4096];
        var result = new StringBuilder();

        try
        {
            while (_stderr != null && _stderr.Peek() > -1)
            {
                var bytesRead = await _stderr.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    result.Append(buffer, 0, bytesRead);
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return result.ToString();
    }

    public async Task TransferFileAsync(
        string localPath,
        string remotePath,
        CancellationToken cancellationToken)
    {
        EnsureConnected();

        _logger.LogDebug("Transferring file {LocalPath} to {RemotePath}", localPath, remotePath);

        // Expand remote path variables first
        var expandedPath = await ExpandRemotePathAsync(remotePath, cancellationToken);

        // For file transfer, we need to use scp as a separate command
        // since the persistent SSH session can't handle binary data transfer well
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
        if (_isConnected && _sshProcess != null && !_sshProcess.HasExited)
        {
            try
            {
                _logger.LogDebug("Closing persistent SSH session");

                // Send exit command to cleanly close the session
                if (_stdin != null)
                {
                    await _stdin.WriteLineAsync("exit");
                    await _stdin.FlushAsync();
                }

                // Give the process a moment to exit gracefully
                var exited = _sshProcess.WaitForExit(3000);
                if (!exited)
                {
                    _logger.LogDebug("SSH process did not exit gracefully, killing it");
                    _sshProcess.Kill();
                }

                _logger.LogDebug("Persistent SSH session closed");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing persistent SSH session");
            }
        }

        CleanupProcess();
        _isConnected = false;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _commandLock.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Private Helpers

    private string BuildPersistentSessionArgs()
    {
        var args = new StringBuilder();

        // SSH options for persistent session
        args.Append("-o BatchMode=yes ");                       // No interactive prompts
        args.Append("-o StrictHostKeyChecking=accept-new ");    // Auto-accept new host keys
        args.Append($"-o ConnectTimeout={_connectTimeout} ");   // Connection timeout
        args.Append("-o ServerAliveInterval=30 ");              // Keep connection alive
        args.Append("-o ServerAliveCountMax=3 ");               // Allow 3 missed keepalives
        args.Append("-T ");                                     // Disable pseudo-terminal allocation
        args.Append($"-p {_port} ");                            // Port
        args.Append($"{_username}@{_targetHost} ");             // User@host
        // Just run bash - we'll send the script via stdin
        args.Append("bash");

        return args.ToString();
    }

    private string BuildScpArgs(string localPath, string remotePath)
    {
        var args = new StringBuilder();

        // SCP options (separate connection, but typically fast after initial connection)
        args.Append("-o BatchMode=yes ");
        args.Append("-o StrictHostKeyChecking=accept-new ");
        args.Append($"-o ConnectTimeout={_connectTimeout} ");
        args.Append($"-P {_port} ");                            // Note: SCP uses uppercase -P for port
        args.Append($"\"{localPath}\" ");
        args.Append($"{_username}@{_targetHost}:\"{remotePath}\"");

        return args.ToString();
    }

    private void EnsureConnected()
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("SSH connection not established. Call EstablishConnectionAsync first.");
        }

        if (_sshProcess == null || _sshProcess.HasExited)
        {
            _isConnected = false;
            throw new InvalidOperationException("SSH session has been terminated unexpectedly.");
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

    private void CleanupProcess()
    {
        try
        {
            _stdin?.Dispose();
            _stdout?.Dispose();
            _stderr?.Dispose();
            _sshProcess?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error cleaning up SSH process resources");
        }

        _stdin = null;
        _stdout = null;
        _stderr = null;
        _sshProcess = null;
    }

    #endregion
}
