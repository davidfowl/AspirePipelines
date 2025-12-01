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
    public async Task UpWithPullAsync_ExecutesDockerComposeUpWithPullAlways()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(0, "Deployed", "");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.UpWithPullAsync("$HOME/app", CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        var call = fakeSSHManager.Calls.First();
        Assert.Contains("docker compose up -d --pull always --remove-orphans", call.Detail);
    }

    [Fact]
    public async Task UpWithPullAsync_ThrowsOnFailure()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(1, "", "Failed to pull images");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpWithPullAsync("$HOME/app", CancellationToken.None));

        Assert.Contains("Failed to deploy containers", ex.Message);
    }

    [Fact]
    public async Task PruneImagesAsync_ExecutesDockerImagePrune()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(0, "Deleted images:\ndeleted: sha256:abc123\nTotal reclaimed space: 100MB", "");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.PruneImagesAsync(CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        var call = fakeSSHManager.Calls.First();
        Assert.Contains("docker image prune -f", call.Detail);
    }

    [Fact]
    public async Task PruneImagesAsync_ReturnsFailureOnError()
    {
        // Arrange
        var fakeSSHManager = new FakeSSHConnectionManager();
        fakeSSHManager.ConfigureDefaultCommandResult(1, "", "permission denied");

        var service = new RemoteDockerComposeService(
            fakeSSHManager,
            NullLogger<RemoteDockerComposeService>.Instance);

        // Act
        var result = await service.PruneImagesAsync(CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(1, result.ExitCode);
    }
}
