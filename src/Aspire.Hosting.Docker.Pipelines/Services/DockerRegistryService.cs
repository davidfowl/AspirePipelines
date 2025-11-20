#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting;
using Aspire.Hosting.Docker.Pipelines.Models;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.Pipelines.Services;

/// <summary>
/// Service for managing Docker registry configuration and image operations.
/// Handles configuration discovery, user prompting, authentication, and push operations.
/// </summary>
internal class DockerRegistryService
{
    private readonly DockerCommandExecutor _dockerCommandExecutor;
    private readonly EnvironmentFileReader _environmentFileReader;
    private readonly ILogger<DockerRegistryService> _logger;
    private const string RegistryContextKey = "DockerRegistry";

    public DockerRegistryService(
        DockerCommandExecutor dockerCommandExecutor,
        EnvironmentFileReader environmentFileReader,
        ILogger<DockerRegistryService> logger)
    {
        _dockerCommandExecutor = dockerCommandExecutor;
        _environmentFileReader = environmentFileReader;
        _logger = logger;
    }

    /// <summary>
    /// Configures registry settings from configuration or by prompting the user.
    /// </summary>
    public async Task<RegistryConfiguration> ConfigureRegistryAsync(
        PipelineStepContext context,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();

        return await ConfigureRegistryAsync(
            interactionService,
            configuration,
            deploymentStateManager,
            step,
            context.Logger,
            cancellationToken);
    }

    /// <summary>
    /// Configures registry settings (testable overload).
    /// </summary>
    internal async Task<RegistryConfiguration> ConfigureRegistryAsync(
        IInteractionService interactionService,
        IConfiguration configuration,
        IDeploymentStateManager deploymentStateManager,
        IReportingStep step,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Configuring Docker registry");

        // Try to load from configuration first
        var registryConfig = LoadFromConfiguration(configuration);
        if (registryConfig != null)
        {
            _logger.LogInformation("Using registry configuration from settings");
            return registryConfig;
        }

        // Prompt user for configuration
        registryConfig = await PromptForRegistryConfigurationAsync(interactionService, configuration, cancellationToken);

        // Authenticate if credentials provided
        if (!string.IsNullOrEmpty(registryConfig.RegistryUsername) && !string.IsNullOrEmpty(registryConfig.RegistryPassword))
        {
            await AuthenticateWithRegistryAsync(registryConfig, step, cancellationToken);
        }
        else
        {
            logger.LogInformation("Skipping authentication (no credentials provided)");
        }

        // Persist configuration
        await PersistRegistryConfigurationAsync(deploymentStateManager, registryConfig, cancellationToken);

        _logger.LogInformation("Registry configured: {RegistryUrl}", registryConfig.RegistryUrl);
        return registryConfig;
    }

    /// <summary>
    /// Tags and pushes container images to the configured registry.
    /// </summary>
    public async Task<Dictionary<string, string>> TagAndPushImagesAsync(
        RegistryConfiguration registryConfig,
        string outputPath,
        string environmentName,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Tagging and pushing images to registry");

        var imageTags = new Dictionary<string, string>();

        // Read environment file to find built images
        var envFilePath = Path.Combine(outputPath, $".env.{environmentName}");
        if (!File.Exists(envFilePath))
        {
            throw new InvalidOperationException($".env.{environmentName} file not found at {envFilePath}");
        }

        var envVars = await _environmentFileReader.ReadEnvironmentFile(envFilePath);

        // Find all *_IMAGE variables
        var imageVars = envVars.Where(kvp => kvp.Key.EndsWith("_IMAGE", StringComparison.OrdinalIgnoreCase)).ToList();

        if (imageVars.Count == 0)
        {
            await step.WarnAsync($"No container images found in .env.{environmentName} file to push.");
            return imageTags;
        }

        // Generate timestamp-based tag
        var imageTag = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        // Tag all images
        imageTags = await TagImagesAsync(imageVars, registryConfig, imageTag, step, cancellationToken);

        // Push all images in parallel
        await PushImagesAsync(imageTags, step, cancellationToken);

        await step.SucceedAsync("Container images pushed successfully.");
        return imageTags;
    }

    private RegistryConfiguration? LoadFromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(RegistryContextKey);

        string registryUrl = section["RegistryUrl"] ?? "";
        string? repositoryPrefix = section["RepositoryPrefix"];
        string? registryUsername = section["RegistryUsername"];
        string? registryPassword = section["RegistryPassword"];

        if (!string.IsNullOrWhiteSpace(registryUrl) && !string.IsNullOrWhiteSpace(repositoryPrefix))
        {
            return new RegistryConfiguration(registryUrl, repositoryPrefix, registryUsername, registryPassword);
        }

        return null;
    }

    private async Task<RegistryConfiguration> PromptForRegistryConfigurationAsync(
        IInteractionService interactionService,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Read defaults from DockerRegistry configuration section
        var defaultRegistryUrl = configuration["DockerRegistry:RegistryUrl"] ?? "docker.io";
        var defaultRepositoryPrefix = configuration["DockerRegistry:RepositoryPrefix"];
        var defaultRegistryUsername = configuration["DockerRegistry:RegistryUsername"];

        var inputs = new InteractionInput[]
        {
            new() { Name = "registryUrl", Required = true, InputType = InputType.Text, Label = "Container Registry URL", Value = defaultRegistryUrl },
            new() { Name = "repositoryPrefix", InputType = InputType.Text, Label = "Image Repository Prefix", Value = defaultRepositoryPrefix },
            new() { Name = "registryUsername", InputType = InputType.Text, Label = "Registry Username", Value = defaultRegistryUsername },
            new() { Name = "registryPassword", InputType = InputType.SecretText, Label = "Registry Password/Token" }
        };

        _logger.LogInformation("Collecting registry settings...");

        var result = await interactionService.PromptInputsAsync(
            "Container Registry Configuration",
            "Provide container registry details (leave credentials blank for anonymous access).\n",
            inputs,
            cancellationToken: cancellationToken);

        if (result.Canceled)
        {
            throw new InvalidOperationException("Registry configuration was canceled");
        }

        var registryUrl = result.Data["registryUrl"].Value ?? throw new InvalidOperationException("Registry URL is required");
        var repositoryPrefix = result.Data["repositoryPrefix"].Value?.Trim();
        var registryUsername = result.Data["registryUsername"].Value;
        var registryPassword = result.Data["registryPassword"].Value;

        return new RegistryConfiguration(registryUrl, repositoryPrefix, registryUsername, registryPassword);
    }

    private async Task AuthenticateWithRegistryAsync(
        RegistryConfiguration registryConfig,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        await using var loginTask = await step.CreateTaskAsync("Authenticating", cancellationToken);
        var loginResult = await _dockerCommandExecutor.ExecuteDockerLogin(
            registryConfig.RegistryUrl,
            registryConfig.RegistryUsername!,
            registryConfig.RegistryPassword!,
            cancellationToken);

        if (loginResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker login failed: {loginResult.Error}");
        }

        await loginTask.SucceedAsync($"Authenticated with {registryConfig.RegistryUrl}", cancellationToken);
    }

    private async Task PersistRegistryConfigurationAsync(
        IDeploymentStateManager deploymentStateManager,
        RegistryConfiguration registryConfig,
        CancellationToken cancellationToken)
    {
        try
        {
            var registryStateSection = await deploymentStateManager.AcquireSectionAsync(RegistryContextKey, cancellationToken);
            registryStateSection.Data["RegistryUrl"] = registryConfig.RegistryUrl;
            registryStateSection.Data["RepositoryPrefix"] = registryConfig.RepositoryPrefix ?? string.Empty;
            registryStateSection.Data["RegistryUsername"] = registryConfig.RegistryUsername ?? string.Empty;
            registryStateSection.Data["RegistryPassword"] = registryConfig.RegistryPassword ?? string.Empty;

            await deploymentStateManager.SaveSectionAsync(registryStateSection, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist registry configuration");
        }
    }

    private async Task<Dictionary<string, string>> TagImagesAsync(
        List<KeyValuePair<string, string>> imageVars,
        RegistryConfiguration registryConfig,
        string imageTag,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        var imageTags = new Dictionary<string, string>();

        foreach (var (envKey, localImageName) in imageVars)
        {
            // Extract service name from env key (e.g., "APISERVICE_IMAGE" -> "apiservice")
            var serviceName = envKey.Substring(0, envKey.Length - "_IMAGE".Length).ToLowerInvariant();

            await using var tagTask = await step.CreateTaskAsync($"Tagging {serviceName} image", cancellationToken);

            // Construct the target image name
            var targetImageName = !string.IsNullOrEmpty(registryConfig.RepositoryPrefix)
                ? $"{registryConfig.RegistryUrl}/{registryConfig.RepositoryPrefix}/{serviceName}:{imageTag}"
                : $"{registryConfig.RegistryUrl}/{serviceName}:{imageTag}";

            // Tag the image
            var tagResult = await _dockerCommandExecutor.ExecuteDockerCommand($"tag {localImageName} {targetImageName}", cancellationToken);

            if (tagResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to tag image {localImageName}: {tagResult.Error}");
            }

            imageTags[serviceName] = targetImageName;
            await tagTask.SucceedAsync($"Successfully tagged {serviceName} image: {targetImageName}", cancellationToken: cancellationToken);
        }

        return imageTags;
    }

    private async Task PushImagesAsync(
        Dictionary<string, string> imageTags,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        async Task PushSingleImageAsync(string serviceName, string targetImageName)
        {
            await using var pushTask = await step.CreateTaskAsync($"Pushing {serviceName} image", cancellationToken);

            var pushResult = await _dockerCommandExecutor.ExecuteDockerCommand($"push {targetImageName}", cancellationToken);

            if (pushResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to push image {targetImageName}: {pushResult.Error}");
            }

            await pushTask.SucceedAsync($"Successfully pushed {serviceName} image: {targetImageName}", cancellationToken: cancellationToken);
        }

        // Push all images in parallel
        var tasks = imageTags.Select(kvp => PushSingleImageAsync(kvp.Key, kvp.Value)).ToList();
        await Task.WhenAll(tasks);
    }
}

/// <summary>
/// Docker registry configuration.
/// </summary>
internal record RegistryConfiguration(
    string RegistryUrl,
    string? RepositoryPrefix,
    string? RegistryUsername,
    string? RegistryPassword);
