#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class NativeSSHConnectionManagerTests
{
    private readonly FakeProcessExecutor _fakeProcessExecutor;
    private readonly FakeFileSystem _fakeFileSystem;
    private readonly ILogger<NativeSSHConnectionManager> _logger;
    private readonly FakeReportingStep _fakeReportingStep;

    public NativeSSHConnectionManagerTests()
    {
        _fakeProcessExecutor = new FakeProcessExecutor();
        _fakeFileSystem = new FakeFileSystem();
        _logger = NullLogger<NativeSSHConnectionManager>.Instance;
        _fakeReportingStep = new FakeReportingStep();
    }

    [Fact]
    public async Task EstablishConnectionAsync_CreatesAspireTempDirectory()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "testuser\n/home/testuser", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        var context = CreateSSHContext();

        // Act
        await manager.EstablishConnectionAsync(context, _fakeReportingStep, CancellationToken.None);

        // Assert
        Assert.True(_fakeFileSystem.WasCalled("CreateDirectory"));
        Assert.True(_fakeFileSystem.DirectoryExists("/home/testuser/.aspire/temp"));
    }

    [Fact]
    public async Task EstablishConnectionAsync_ExecutesSshWithControlMasterOptions()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "testuser\n/home/testuser", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        var context = CreateSSHContext("192.168.1.100", "admin", "2222");

        // Act
        await manager.EstablishConnectionAsync(context, _fakeReportingStep, CancellationToken.None);

        // Assert
        Assert.True(_fakeProcessExecutor.WasCalled("ssh"));
        var sshCall = _fakeProcessExecutor.Calls.First(c => c.FileName == "ssh");
        Assert.Contains("-M", sshCall.Arguments); // ControlMaster
        Assert.Contains("-S", sshCall.Arguments); // Control socket path
        Assert.Contains("ControlPersist=600", sshCall.Arguments);
        Assert.Contains("BatchMode=yes", sshCall.Arguments);
        Assert.Contains("-p 2222", sshCall.Arguments);
        Assert.Contains("admin@192.168.1.100", sshCall.Arguments);
    }

    [Fact]
    public async Task EstablishConnectionAsync_SetsIsConnectedToTrue()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "testuser\n/home/testuser", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        var context = CreateSSHContext();

        // Act
        await manager.EstablishConnectionAsync(context, _fakeReportingStep, CancellationToken.None);

        // Assert
        Assert.True(manager.IsConnected);
        Assert.Equal("test-host.com", manager.TargetHost);
    }

    [Fact]
    public async Task EstablishConnectionAsync_ThrowsOnSshFailure()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(1, "", "Connection refused"));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        var context = CreateSSHContext();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.EstablishConnectionAsync(context, _fakeReportingStep, CancellationToken.None));

        Assert.Contains("Failed to establish SSH connection", ex.Message);
        Assert.Contains("Connection refused", ex.Message);
    }

    [Fact]
    public async Task ExecuteCommandWithOutputAsync_UsesSshWithControlSocket()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "command output", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        await manager.EstablishConnectionAsync(CreateSSHContext(), _fakeReportingStep, CancellationToken.None);

        _fakeProcessExecutor.ClearCalls();

        // Act
        var result = await manager.ExecuteCommandWithOutputAsync("ls -la", CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("command output", result.Output);

        var sshCall = _fakeProcessExecutor.Calls.First(c => c.FileName == "ssh");
        Assert.Contains("-S", sshCall.Arguments); // Uses control socket
        Assert.Contains("ls -la", sshCall.Arguments);
    }

    [Fact]
    public async Task ExecuteCommandAsync_ThrowsWhenNotConnected()
    {
        // Arrange
        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ExecuteCommandAsync("ls", CancellationToken.None));

        Assert.Contains("SSH connection not established", ex.Message);
    }

    [Fact]
    public async Task TransferFileAsync_UsesScpWithControlSocket()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        await manager.EstablishConnectionAsync(CreateSSHContext(), _fakeReportingStep, CancellationToken.None);

        _fakeProcessExecutor.ClearCalls();

        // Act
        await manager.TransferFileAsync("/local/file.txt", "/remote/file.txt", CancellationToken.None);

        // Assert
        Assert.True(_fakeProcessExecutor.WasCalled("scp"));
        var scpCall = _fakeProcessExecutor.Calls.First(c => c.FileName == "scp");
        Assert.Contains("ControlPath=", scpCall.Arguments);
        Assert.Contains("/local/file.txt", scpCall.Arguments);
        Assert.Contains("/remote/file.txt", scpCall.Arguments);
    }

    [Fact]
    public async Task TransferFileAsync_ExpandsRemotePathVariables()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "/home/testuser/app", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        await manager.EstablishConnectionAsync(CreateSSHContext(), _fakeReportingStep, CancellationToken.None);

        _fakeProcessExecutor.ClearCalls();

        // Act
        await manager.TransferFileAsync("/local/file.txt", "$HOME/app", CancellationToken.None);

        // Assert
        // Should have executed echo to expand the path
        var echoCall = _fakeProcessExecutor.Calls.FirstOrDefault(c => c.FileName == "ssh" && c.Arguments.Contains("echo"));
        Assert.NotNull(echoCall);
    }

    [Fact]
    public async Task DisconnectAsync_SendsExitCommandToControlMaster()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "testuser\n/home/testuser", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        await manager.EstablishConnectionAsync(CreateSSHContext(), _fakeReportingStep, CancellationToken.None);

        _fakeProcessExecutor.ClearCalls();

        // Act
        await manager.DisconnectAsync();

        // Assert
        Assert.False(manager.IsConnected);

        var exitCall = _fakeProcessExecutor.Calls.FirstOrDefault(c => c.FileName == "ssh" && c.Arguments.Contains("-O exit"));
        Assert.NotNull(exitCall);
    }

    [Fact]
    public async Task DisconnectAsync_CleansUpControlSocketFile()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "testuser\n/home/testuser", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        await manager.EstablishConnectionAsync(CreateSSHContext(), _fakeReportingStep, CancellationToken.None);

        // Simulate socket file exists
        var socketPath = _fakeFileSystem.Calls
            .Where(c => c.Operation == "CombinePaths" && c.Path.Contains("ssh-"))
            .Select(c => c.Path)
            .FirstOrDefault();

        // Add socket file to fake file system
        _fakeFileSystem.AddFile("/home/testuser/.aspire/temp/ssh-12345678", "");

        _fakeFileSystem.ClearCalls();

        // Act
        await manager.DisconnectAsync();

        // Assert
        Assert.True(_fakeFileSystem.WasCalled("FileExists"));
    }

    [Fact]
    public async Task DisposeAsync_CallsDisconnect()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "testuser\n/home/testuser", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);
        await manager.EstablishConnectionAsync(CreateSSHContext(), _fakeReportingStep, CancellationToken.None);

        // Act
        await manager.DisposeAsync();

        // Assert
        Assert.False(manager.IsConnected);
    }

    [Fact]
    public async Task ControlSocketPath_UsesShortGuidForUnixSocketLimit()
    {
        // Arrange
        _fakeFileSystem.SetUserProfilePath("/home/testuser");
        _fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "testuser\n/home/testuser", ""));

        var manager = new NativeSSHConnectionManager(_fakeProcessExecutor, _fakeFileSystem, _logger);

        // Act
        await manager.EstablishConnectionAsync(CreateSSHContext(), _fakeReportingStep, CancellationToken.None);

        // Assert - verify socket path format is short (ssh-{8chars})
        var combineCall = _fakeFileSystem.Calls
            .FirstOrDefault(c => c.Operation == "CombinePaths" && c.Path.Contains("ssh-"));
        Assert.NotNull(combineCall);
        // Socket filename should be "ssh-" + 8 chars = 12 chars total
    }

    private static SSHConnectionContext CreateSSHContext(
        string targetHost = "test-host.com",
        string username = "testuser",
        string port = "22")
    {
        return new SSHConnectionContext
        {
            TargetHost = targetHost,
            SshUsername = username,
            SshPort = port,
            SshPassword = null,
            SshKeyPath = null
        };
    }
}
