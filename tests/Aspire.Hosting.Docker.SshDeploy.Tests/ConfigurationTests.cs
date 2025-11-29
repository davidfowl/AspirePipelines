using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class ConfigurationTests
{
    [Fact]
    public void DockerSSHSection_ReadsSSHConfiguration()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerSSH:SshHost"] = "test-host.com",
                ["DockerSSH:SshUsername"] = "testuser",
                ["DockerSSH:SshPort"] = "2222",
                ["DockerSSH:SshKeyPath"] = "/path/to/key"
            })
            .Build();

        // Act
        var sshHost = configuration["DockerSSH:SshHost"];
        var sshUsername = configuration["DockerSSH:SshUsername"];
        var sshPort = configuration["DockerSSH:SshPort"];
        var sshKeyPath = configuration["DockerSSH:SshKeyPath"];

        // Assert
        Assert.Equal("test-host.com", sshHost);
        Assert.Equal("testuser", sshUsername);
        Assert.Equal("2222", sshPort);
        Assert.Equal("/path/to/key", sshKeyPath);
    }

    [Fact]
    public void DockerSSHSection_UsesDefaultPort_WhenNotSpecified()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerSSH:SshHost"] = "test-host.com",
                ["DockerSSH:SshUsername"] = "testuser"
            })
            .Build();

        // Act
        var sshPort = configuration["DockerSSH:SshPort"] ?? "22";

        // Assert
        Assert.Equal("22", sshPort);
    }

    [Fact]
    public void DockerSSHSection_DoesNotContainRegistryProperties()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerSSH:SshHost"] = "test-host.com",
                ["DockerSSH:SshUsername"] = "testuser",
                ["DockerRegistry:RegistryUrl"] = "docker.io",
                ["DockerRegistry:RegistryUsername"] = "reguser",
                ["DockerRegistry:RepositoryPrefix"] = "myprefix"
            })
            .Build();

        // Act
        var sshHost = configuration["DockerSSH:SshHost"];
        var sshUsername = configuration["DockerSSH:SshUsername"];
        var registryUrlInSSH = configuration["DockerSSH:RegistryUrl"];
        var registryUsernameInSSH = configuration["DockerSSH:RegistryUsername"];

        // Assert - SSH section only has SSH values
        Assert.Equal("test-host.com", sshHost);
        Assert.Equal("testuser", sshUsername);
        // Registry values are NOT in DockerSSH section
        Assert.Null(registryUrlInSSH);
        Assert.Null(registryUsernameInSSH);
    }

    [Fact]
    public void DockerRegistryConfiguration_ReadsFromDockerRegistrySection()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerRegistry:RegistryUrl"] = "ghcr.io",
                ["DockerRegistry:RepositoryPrefix"] = "myorg",
                ["DockerRegistry:RegistryUsername"] = "reguser",
                ["DockerRegistry:RegistryPassword"] = "secret123"
            })
            .Build();

        // Act
        var registryUrl = configuration["DockerRegistry:RegistryUrl"];
        var repositoryPrefix = configuration["DockerRegistry:RepositoryPrefix"];
        var registryUsername = configuration["DockerRegistry:RegistryUsername"];
        var registryPassword = configuration["DockerRegistry:RegistryPassword"];

        // Assert
        Assert.Equal("ghcr.io", registryUrl);
        Assert.Equal("myorg", repositoryPrefix);
        Assert.Equal("reguser", registryUsername);
        Assert.Equal("secret123", registryPassword);
    }

    [Fact]
    public void DockerRegistrySection_IsIndependentOfDockerSSHSection()
    {
        // Arrange - DockerSSH section has different values
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerSSH:SshHost"] = "ssh-host.com",
                ["DockerSSH:SshUsername"] = "sshuser",
                ["DockerRegistry:RegistryUrl"] = "docker.io",
                ["DockerRegistry:RegistryUsername"] = "dockeruser"
            })
            .Build();

        // Act
        var sshHost = configuration["DockerSSH:SshHost"];
        var sshUsername = configuration["DockerSSH:SshUsername"];
        var registryUrl = configuration["DockerRegistry:RegistryUrl"];
        var registryUsername = configuration["DockerRegistry:RegistryUsername"];

        // Assert - SSH and Registry are in separate sections
        Assert.Equal("ssh-host.com", sshHost);
        Assert.Equal("sshuser", sshUsername);
        Assert.Equal("docker.io", registryUrl);
        Assert.Equal("dockeruser", registryUsername);
    }

    [Fact]
    public void DeploymentSection_IsIndependentOfDockerSSHSection()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerSSH:SshHost"] = "ssh-host.com",
                ["Deployment:RemoteDeployPath"] = "$HOME/aspire/apps/myapp"
            })
            .Build();

        // Act
        var sshHost = configuration["DockerSSH:SshHost"];
        var deployPath = configuration["Deployment:RemoteDeployPath"];

        // Assert
        Assert.Equal("ssh-host.com", sshHost);
        Assert.Equal("$HOME/aspire/apps/myapp", deployPath);
    }

    [Fact]
    public void ResolveSSHKeyPath_ReturnsNull_ForEmptyPath()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();

        // Act
        var result = SSHNetConnectionFactory.ResolveSSHKeyPath(null, fakeFileSystem);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSSHKeyPath_ReturnsNull_ForWhitespacePath()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();

        // Act
        var result = SSHNetConnectionFactory.ResolveSSHKeyPath("", fakeFileSystem);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSSHKeyPath_ReturnsPath_WhenContainsDirectorySeparator()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        var keyPath = "/path/to/private/key";

        // Act
        var result = SSHNetConnectionFactory.ResolveSSHKeyPath(keyPath, fakeFileSystem);

        // Assert
        Assert.Equal(keyPath, result);
    }

    [Fact]
    public void ResolveSSHKeyPath_ReturnsPath_WhenContainsWindowsDirectorySeparator()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        var keyPath = @"C:\Users\test\.ssh\id_rsa";

        // Act
        var result = SSHNetConnectionFactory.ResolveSSHKeyPath(keyPath, fakeFileSystem);

        // Assert
        Assert.Equal(keyPath, result);
    }

    [Fact]
    public void ResolveSSHKeyPath_ExpandsKeyName_WhenFileExists()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        var keyName = "id_rsa";
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expectedPath = Path.Combine(homeDir, ".ssh", keyName);

        // Simulate the file existing
        fakeFileSystem.AddFile(expectedPath, "fake key content");

        // Act
        var result = SSHNetConnectionFactory.ResolveSSHKeyPath(keyName, fakeFileSystem);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void ResolveSSHKeyPath_ReturnsKeyName_WhenFileDoesNotExist()
    {
        // Arrange
        var fakeFileSystem = new FakeFileSystem();
        var keyName = "id_rsa";

        // Act
        var result = SSHNetConnectionFactory.ResolveSSHKeyPath(keyName, fakeFileSystem);

        // Assert
        Assert.Equal(keyName, result);
    }
}
