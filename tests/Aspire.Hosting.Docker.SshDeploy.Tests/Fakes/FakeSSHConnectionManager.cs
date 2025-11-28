#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Aspire.Hosting.Pipelines;
using Renci.SshNet;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of ISSHConnectionManager for testing.
/// Records all calls and allows pre-configuring responses.
/// </summary>
internal class FakeSSHConnectionManager : ISSHConnectionManager
{
    private readonly List<SSHCall> _calls = new();
    private readonly Dictionary<string, (int exitCode, string output, string error)> _commandResults = new();
    private (int exitCode, string output, string error) _defaultCommandResult = (0, "", "");
    private bool _isConnected;
    private bool _shouldThrowOnConnect;
    private bool _shouldThrowOnCommand;
    private bool _shouldThrowOnTransfer;
    private string? _targetHost;

    /// <summary>
    /// Gets all the SSH operation calls that were made.
    /// </summary>
    public IReadOnlyList<SSHCall> Calls => _calls.AsReadOnly();

    public bool IsConnected => _isConnected;

    public string? TargetHost => _targetHost;

    public SshClient? SshClient => null; // Fake implementation doesn't provide real SSH client

    /// <summary>
    /// Configures the manager to throw an exception when establishing a connection.
    /// </summary>
    public void ConfigureThrowOnConnect(bool shouldThrow = true)
    {
        _shouldThrowOnConnect = shouldThrow;
    }

    /// <summary>
    /// Configures the manager to throw an exception when executing commands.
    /// </summary>
    public void ConfigureThrowOnCommand(bool shouldThrow = true)
    {
        _shouldThrowOnCommand = shouldThrow;
    }

    /// <summary>
    /// Configures the manager to throw an exception when transferring files.
    /// </summary>
    public void ConfigureThrowOnTransfer(bool shouldThrow = true)
    {
        _shouldThrowOnTransfer = shouldThrow;
    }

    /// <summary>
    /// Configure a specific result for a given command.
    /// </summary>
    public void ConfigureCommandResult(string command, int exitCode, string output = "", string error = "")
    {
        _commandResults[command] = (exitCode, output, error);
    }

    /// <summary>
    /// Configure the default result for any command that doesn't have a specific configured result.
    /// </summary>
    public void ConfigureDefaultCommandResult(int exitCode, string output = "", string error = "")
    {
        _defaultCommandResult = (exitCode, output, error);
    }

    public Task EstablishConnectionAsync(
        SSHConnectionContext context,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        _calls.Add(new SSHCall("EstablishConnection", context.TargetHost, context.SshUsername, context.SshPort));

        if (_shouldThrowOnConnect)
        {
            throw new InvalidOperationException("Failed to establish SSH connection (configured to fail)");
        }

        _targetHost = context.TargetHost;
        _isConnected = true;
        return Task.CompletedTask;
    }

    public Task<int> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        _calls.Add(new SSHCall("ExecuteCommand", command));

        if (_shouldThrowOnCommand)
        {
            throw new InvalidOperationException($"Failed to execute command: {command} (configured to fail)");
        }

        var result = GetCommandResult(command);

        if (result.exitCode != 0)
        {
            throw new InvalidOperationException($"SSH command failed: {result.error}");
        }

        return Task.FromResult(result.exitCode);
    }

    public Task<(int ExitCode, string Output, string Error)> ExecuteCommandWithOutputAsync(
        string command,
        CancellationToken cancellationToken)
    {
        _calls.Add(new SSHCall("ExecuteCommandWithOutput", command));

        if (_shouldThrowOnCommand)
        {
            throw new InvalidOperationException($"Failed to execute command: {command} (configured to fail)");
        }

        var result = GetCommandResult(command);
        return Task.FromResult(result);
    }

    public Task TransferFileAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        _calls.Add(new SSHCall("TransferFile", localPath, remotePath));

        if (_shouldThrowOnTransfer)
        {
            throw new InvalidOperationException($"Failed to transfer file: {localPath} (configured to fail)");
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _calls.Add(new SSHCall("Disconnect"));
        _isConnected = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _calls.Add(new SSHCall("Dispose"));
        _isConnected = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Checks if a specific operation was called.
    /// </summary>
    public bool WasCalled(string operation, string? detail = null)
    {
        return _calls.Any(c =>
            c.Operation == operation &&
            (detail == null || c.Detail == detail || c.Detail2 == detail));
    }

    /// <summary>
    /// Gets the number of times a specific operation was called.
    /// </summary>
    public int GetCallCount(string operation)
    {
        return _calls.Count(c => c.Operation == operation);
    }

    /// <summary>
    /// Clears all recorded calls.
    /// </summary>
    public void ClearCalls()
    {
        _calls.Clear();
    }

    private (int exitCode, string output, string error) GetCommandResult(string command)
    {
        // Check for exact match first
        if (_commandResults.TryGetValue(command, out var result))
        {
            return result;
        }

        // Handle echo commands for path expansion (used by TransferFileAsync)
        if (command.StartsWith("echo \""))
        {
            var path = command.Substring(6, command.Length - 7); // Remove 'echo "' and trailing '"'
            // Simulate shell variable expansion
            var expandedPath = path.Replace("$HOME", "/home/testuser");
            return (0, expandedPath, "");
        }

        return _defaultCommandResult;
    }
}

/// <summary>
/// Represents a recorded SSH operation call.
/// </summary>
public record SSHCall(string Operation, string? Detail = null, string? Detail2 = null, string? Detail3 = null);
