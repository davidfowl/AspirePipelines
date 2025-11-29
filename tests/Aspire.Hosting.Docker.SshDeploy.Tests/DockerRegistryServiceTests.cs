#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Tests;

public class DockerRegistryServiceTests
{
    [Fact]
    public async Task ConfigureRegistryAsync_AuthenticatesWhenCredentialsProvidedInConfig()
    {
        // Arrange
        var fakeProcessExecutor = new FakeProcessExecutor();
        fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(0, "Login Succeeded", ""));

        var dockerCommandExecutor = new DockerCommandExecutor(fakeProcessExecutor);
        var fakeFileSystem = new FakeFileSystem();
        var environmentFileReader = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);
        var service = new DockerRegistryService(
            dockerCommandExecutor,
            environmentFileReader,
            NullLogger<DockerRegistryService>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerRegistry:RegistryUrl"] = "ghcr.io",
                ["DockerRegistry:RepositoryPrefix"] = "testorg",
                ["DockerRegistry:RegistryUsername"] = "testuser",
                ["DockerRegistry:RegistryPassword"] = "testpassword"
            })
            .Build();

        var fakeInteractionService = new FakeInteractionService();
        var fakeDeploymentStateManager = new FakeDeploymentStateManager();
        var fakeReportingStep = new FakeReportingStep();

        // Act
        var result = await service.ConfigureRegistryAsync(
            fakeInteractionService,
            configuration,
            fakeDeploymentStateManager,
            fakeReportingStep,
            NullLogger.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal("ghcr.io", result.RegistryUrl);
        Assert.Equal("testorg", result.RepositoryPrefix);

        // Verify docker login was called
        Assert.True(fakeProcessExecutor.WasCalled("docker"));
        var loginCall = fakeProcessExecutor.Calls.First();
        Assert.Contains("login ghcr.io", loginCall.Arguments);
        Assert.Contains("--username testuser", loginCall.Arguments);
        Assert.Contains("--password-stdin", loginCall.Arguments);
        Assert.Equal("testpassword", loginCall.StdinInput);
    }

    [Fact]
    public async Task ConfigureRegistryAsync_SkipsAuthenticationWhenNoCredentials()
    {
        // Arrange
        var fakeProcessExecutor = new FakeProcessExecutor();
        var dockerCommandExecutor = new DockerCommandExecutor(fakeProcessExecutor);
        var fakeFileSystem = new FakeFileSystem();
        var environmentFileReader = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);
        var service = new DockerRegistryService(
            dockerCommandExecutor,
            environmentFileReader,
            NullLogger<DockerRegistryService>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerRegistry:RegistryUrl"] = "docker.io",
                ["DockerRegistry:RepositoryPrefix"] = "testorg"
                // No username or password
            })
            .Build();

        var fakeInteractionService = new FakeInteractionService();
        var fakeDeploymentStateManager = new FakeDeploymentStateManager();
        var fakeReportingStep = new FakeReportingStep();

        // Act
        var result = await service.ConfigureRegistryAsync(
            fakeInteractionService,
            configuration,
            fakeDeploymentStateManager,
            fakeReportingStep,
            NullLogger.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal("docker.io", result.RegistryUrl);

        // Verify docker login was NOT called
        Assert.False(fakeProcessExecutor.WasCalled("docker"));
        Assert.Empty(fakeProcessExecutor.Calls);
    }

    [Fact]
    public async Task ConfigureRegistryAsync_ThrowsWhenLoginFails()
    {
        // Arrange
        var fakeProcessExecutor = new FakeProcessExecutor();
        fakeProcessExecutor.ConfigureDefaultResult(new ProcessResult(1, "", "unauthorized: access denied"));

        var dockerCommandExecutor = new DockerCommandExecutor(fakeProcessExecutor);
        var fakeFileSystem = new FakeFileSystem();
        var environmentFileReader = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);
        var service = new DockerRegistryService(
            dockerCommandExecutor,
            environmentFileReader,
            NullLogger<DockerRegistryService>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerRegistry:RegistryUrl"] = "ghcr.io",
                ["DockerRegistry:RepositoryPrefix"] = "testorg",
                ["DockerRegistry:RegistryUsername"] = "testuser",
                ["DockerRegistry:RegistryPassword"] = "wrongpassword"
            })
            .Build();

        var fakeInteractionService = new FakeInteractionService();
        var fakeDeploymentStateManager = new FakeDeploymentStateManager();
        var fakeReportingStep = new FakeReportingStep();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfigureRegistryAsync(
                fakeInteractionService,
                configuration,
                fakeDeploymentStateManager,
                fakeReportingStep,
                NullLogger.Instance,
                CancellationToken.None));

        Assert.Contains("Docker login failed", ex.Message);
    }

    [Fact]
    public async Task ConfigureRegistryAsync_SkipsAuthenticationWhenUsernameWithoutPassword()
    {
        // Arrange
        var fakeProcessExecutor = new FakeProcessExecutor();
        var dockerCommandExecutor = new DockerCommandExecutor(fakeProcessExecutor);
        var fakeFileSystem = new FakeFileSystem();
        var environmentFileReader = new EnvironmentFileReader(fakeFileSystem, NullLogger<EnvironmentFileReader>.Instance);
        var service = new DockerRegistryService(
            dockerCommandExecutor,
            environmentFileReader,
            NullLogger<DockerRegistryService>.Instance);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DockerRegistry:RegistryUrl"] = "docker.io",
                ["DockerRegistry:RepositoryPrefix"] = "testorg",
                ["DockerRegistry:RegistryUsername"] = "testuser"
                // No password
            })
            .Build();

        var fakeInteractionService = new FakeInteractionService();
        var fakeDeploymentStateManager = new FakeDeploymentStateManager();
        var fakeReportingStep = new FakeReportingStep();

        // Act
        var result = await service.ConfigureRegistryAsync(
            fakeInteractionService,
            configuration,
            fakeDeploymentStateManager,
            fakeReportingStep,
            NullLogger.Instance,
            CancellationToken.None);

        // Assert - should succeed but skip auth
        Assert.Equal("docker.io", result.RegistryUrl);
        Assert.False(fakeProcessExecutor.WasCalled("docker"));
    }
}

#region Test Fakes

internal class FakeInteractionService : IInteractionService
{
    public bool IsAvailable => true;

    public Task<InteractionResult<bool>> PromptConfirmationAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Should not be called when config is provided");
    }

    public Task<InteractionResult<bool>> PromptMessageBoxAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Should not be called when config is provided");
    }

    public Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, string inputLabel, string placeHolder, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Should not be called when config is provided");
    }

    public Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, InteractionInput input, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Should not be called when config is provided");
    }

    public Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(string title, string? message, IReadOnlyList<InteractionInput> inputs, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Should not be called when config is provided");
    }

    public Task<InteractionResult<bool>> PromptNotificationAsync(string title, string message, NotificationInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Should not be called when config is provided");
    }
}

internal class FakeDeploymentStateManager : IDeploymentStateManager
{
    private readonly Dictionary<string, DeploymentStateSection> _sections = new();
    private long _version = 0;

    public string? StateFilePath => null;

    public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
    {
        if (!_sections.TryGetValue(sectionName, out var section))
        {
            section = new DeploymentStateSection(sectionName, null, _version++);
            _sections[sectionName] = section;
        }
        return Task.FromResult(section);
    }

    public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        _sections[section.SectionName] = section;
        return Task.CompletedTask;
    }
}

internal class FakeReportingStep : IReportingStep
{
    private readonly List<FakeReportingTask> _tasks = new();

    public void Log(LogLevel logLevel, string message, bool enableMarkdown) { }

    public Task CompleteAsync(string completionText, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReportingTask> CreateTaskAsync(string description, CancellationToken cancellationToken = default)
    {
        var task = new FakeReportingTask(description);
        _tasks.Add(task);
        return Task.FromResult<IReportingTask>(task);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal class FakeReportingTask : IReportingTask
{
    public string Description { get; }

    public FakeReportingTask(string description)
    {
        Description = description;
    }

    public Task UpdateAsync(string statusText, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CompleteAsync(string? completionText = null, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

#endregion
