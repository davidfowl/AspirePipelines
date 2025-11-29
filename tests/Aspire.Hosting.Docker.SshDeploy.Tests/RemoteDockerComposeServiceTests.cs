using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class RemoteDockerComposeServiceTests
{
    [Fact]
    public async Task LoginToRegistryAsync_ExecutesDockerLoginCommand()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(0, "Login Succeeded", "");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.LoginToRegistryAsync(
            "ghcr.io",
            "testuser",
            "testpassword",
            CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);

        // Verify the command was executed
        Assert.Equal(1, fakeSSHManager.GetCallCount("ExecuteCommandWithOutput"));

        var call = fakeSSHManager.Calls.First();
        Assert.Contains("docker login ghcr.io", call.Detail);
        Assert.Contains("-u testuser", call.Detail);
        Assert.Contains("--password-stdin", call.Detail);
    }

    [Fact]
    public async Task LoginToRegistryAsync_ReturnsFailureOnError()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(1, "", "unauthorized: access denied");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.LoginToRegistryAsync(
            "ghcr.io",
            "testuser",
            "wrongpassword",
            CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("unauthorized: access denied", result.Error);
    }

    [Fact]
    public async Task LoginToRegistryAsync_EscapesSingleQuotesInPassword()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(0, "Login Succeeded", "");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.LoginToRegistryAsync(
            "docker.io",
            "testuser",
            "pass'word",  // Password with single quote
            CancellationToken.None);

        // Assert
        Assert.True(result.Success);

        var call = fakeSSHManager.Calls.First();
        // Verify the password single quote was escaped
        Assert.Contains("pass'\"'\"'word", call.Detail);
    }

    [Fact]
    public async Task LoginToRegistryAsync_WorksWithDifferentRegistries()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(0, "Login Succeeded", "");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        var registries = new[] { "ghcr.io", "docker.io", "registry.example.com:5000" };

        foreach (var registry in registries)
        {
            fakeSSHManager.ClearCalls();

            // Act
            var result = await service.LoginToRegistryAsync(
                registry,
                "user",
                "pass",
                CancellationToken.None);

            // Assert
            Assert.True(result.Success);
            var call = fakeSSHManager.Calls.First();
            Assert.Contains($"docker login {registry}", call.Detail);
        }
    }

    [Fact]
    public async Task PullImagesAsync_ExecutesDockerComposePull()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(0, "Pulling images...", "");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.PullImagesAsync("$HOME/app", CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        var call = fakeSSHManager.Calls.First();
        Assert.Contains("docker compose pull", call.Detail);
    }

    [Fact]
    public async Task StopAsync_ExecutesDockerComposeDown()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(0, "Stopped", "");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.StopAsync("$HOME/app", CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        var call = fakeSSHManager.Calls.First();
        Assert.Contains("docker compose down", call.Detail);
    }

    [Fact]
    public async Task StartAsync_ExecutesDockerComposeUp()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(0, "Started", "");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.StartAsync("$HOME/app", CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        var call = fakeSSHManager.Calls.First();
        Assert.Contains("docker compose up -d", call.Detail);
    }

    [Fact]
    public async Task StartAsync_ThrowsOnFailure()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(1, "", "Container failed to start");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync("$HOME/app", CancellationToken.None));

        Assert.Contains("Failed to start containers", ex.Message);
    }
}
