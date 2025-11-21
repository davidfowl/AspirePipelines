using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class DockerCommandExecutorTests
{
    [Fact]
    public async Task ExecuteProcessAsync_ReturnsProcessResult()
    {
        // Arrange
        var fakeExecutor = new FakeProcessExecutor();
        fakeExecutor.ConfigureResult("test arg1 arg2", new ProcessResult(0, "output", ""));

        var utility = new DockerCommandExecutor(fakeExecutor);

        // Act
        var result = await utility.ExecuteProcessAsync("test", "arg1 arg2", CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("output", result.Output);
        Assert.Equal("", result.Error);
        Assert.True(fakeExecutor.WasCalled("test", "arg1 arg2"));
    }

    [Fact]
    public async Task ExecuteDockerCommand_CallsDockerWithArguments()
    {
        // Arrange
        var fakeExecutor = new FakeProcessExecutor();
        fakeExecutor.ConfigureResult("docker --version", new ProcessResult(0, "Docker version 24.0.0", ""));

        var utility = new DockerCommandExecutor(fakeExecutor);

        // Act
        var result = await utility.ExecuteDockerCommand("--version", CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Docker version 24.0.0", result.Output);
        Assert.True(fakeExecutor.WasCalled("docker", "--version"));
    }

    [Fact]
    public async Task ExecuteDockerComposeCommand_CallsDockerComposeWithArguments()
    {
        // Arrange
        var fakeExecutor = new FakeProcessExecutor();
        fakeExecutor.ConfigureResult("docker-compose --version", new ProcessResult(0, "docker-compose version 2.0.0", ""));

        var utility = new DockerCommandExecutor(fakeExecutor);

        // Act
        var result = await utility.ExecuteDockerComposeCommand("--version", CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("docker-compose version 2.0.0", result.Output);
        Assert.True(fakeExecutor.WasCalled("docker-compose", "--version"));
    }

    [Fact]
    public async Task ExecuteDockerLogin_PassesPasswordViaStdin()
    {
        // Arrange
        var fakeExecutor = new FakeProcessExecutor();
        var registryUrl = "docker.io";
        var username = "testuser";
        var password = "testpassword";

        fakeExecutor.ConfigureResult(
            $"docker login {registryUrl} --username {username} --password-stdin",
            new ProcessResult(0, "Login Succeeded", ""));

        var utility = new DockerCommandExecutor(fakeExecutor);

        // Act
        var result = await utility.ExecuteDockerLogin(registryUrl, username, password, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Login Succeeded", result.Output);

        // Verify the password was passed via stdin
        var call = fakeExecutor.Calls.First();
        Assert.Equal("docker", call.FileName);
        Assert.Contains("--password-stdin", call.Arguments);
        Assert.Equal(password, call.StdinInput);
    }

    [Fact]
    public async Task ExecuteDockerLogin_ReturnsErrorOnFailure()
    {
        // Arrange
        var fakeExecutor = new FakeProcessExecutor();
        fakeExecutor.ConfigureDefaultResult(new ProcessResult(1, "", "Error: unauthorized"));

        var utility = new DockerCommandExecutor(fakeExecutor);

        // Act
        var result = await utility.ExecuteDockerLogin("docker.io", "user", "wrongpass", CancellationToken.None);

        // Assert
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Error: unauthorized", result.Error);
    }

    [Fact]
    public async Task MultipleCommands_RecordsAllCalls()
    {
        // Arrange
        var fakeExecutor = new FakeProcessExecutor();
        fakeExecutor.ConfigureDefaultResult(new ProcessResult(0, "", ""));

        var utility = new DockerCommandExecutor(fakeExecutor);

        // Act
        await utility.ExecuteDockerCommand("version", CancellationToken.None);
        await utility.ExecuteDockerCommand("ps", CancellationToken.None);
        await utility.ExecuteDockerComposeCommand("up", CancellationToken.None);

        // Assert
        Assert.Equal(3, fakeExecutor.Calls.Count);
        Assert.True(fakeExecutor.WasCalled("docker", "version"));
        Assert.True(fakeExecutor.WasCalled("docker", "ps"));
        Assert.True(fakeExecutor.WasCalled("docker-compose", "up"));
    }
}
