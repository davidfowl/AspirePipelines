#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting;
using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Aspire.Hosting.Docker.Pipelines.Services;
using Aspire.Hosting.Docker.Pipelines.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aspire.Hosting.Docker.Pipelines.Models;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Docker;

internal class DockerSSHPipeline(
    DockerComposeEnvironmentResource dockerComposeEnvironmentResource,
    DockerCommandExecutor dockerCommandExecutor,
    EnvironmentFileReader environmentFileReader,
    IPipelineOutputService pipelineOutputService,
    SSHConfigurationDiscovery sshConfigurationDiscovery,
    ISSHConnectionManager sshConnectionManager,
    IRemoteFileService remoteFileService,
    IRemoteDockerEnvironmentService remoteDockerEnvironmentService,
    IRemoteDockerComposeService remoteDockerComposeService,
    IRemoteDeploymentMonitorService remoteDeploymentMonitorService,
    IRemoteEnvironmentService remoteEnvironmentService,
    IRemoteServiceInspectionService remoteServiceInspectionService) : IAsyncDisposable
{
    private readonly DockerCommandExecutor _dockerCommandExecutor = dockerCommandExecutor;
    private readonly EnvironmentFileReader _environmentFileReader = environmentFileReader;
    private readonly SSHConfigurationDiscovery _sshConfigurationDiscovery = sshConfigurationDiscovery;
    private readonly ISSHConnectionManager _sshConnectionManager = sshConnectionManager;
    private readonly IRemoteFileService _remoteFileService = remoteFileService;
    private readonly IRemoteDockerEnvironmentService _remoteDockerEnvironmentService = remoteDockerEnvironmentService;
    private readonly IRemoteDockerComposeService _remoteDockerComposeService = remoteDockerComposeService;
    private readonly IRemoteDeploymentMonitorService _remoteDeploymentMonitorService = remoteDeploymentMonitorService;
    private readonly IRemoteEnvironmentService _remoteEnvironmentService = remoteEnvironmentService;
    private readonly IRemoteServiceInspectionService _remoteServiceInspectionService = remoteServiceInspectionService;

    // Deployment state keys
    private const string SshContextKey = "DockerSSH";
    private const string RegistryContextKey = "DockerRegistry";

    // Execution-scoped state using TaskCompletionSource pattern (Aspire pattern)
    internal TaskCompletionSource<SSHConnectionContext> SSHContextTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<Dictionary<string, string>> ImageTagsTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<RegistryConfiguration> RegistryConfigTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<string> DashboardServiceNameTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DockerComposeEnvironmentResource DockerComposeEnvironment { get; } = dockerComposeEnvironmentResource;

    public string OutputPath => pipelineOutputService.GetOutputDirectory();

    public IEnumerable<PipelineStep> CreateSteps(PipelineStepFactoryContext context)
    {
        // Base prerequisite step
        var prereqs = new PipelineStep { Name = $"ssh-prereq-{DockerComposeEnvironment.Name}", Action = CheckPrerequisitesConcurrently };

        // Step sequence mirroring the logical deployment flow
        var prepareSshContext = new PipelineStep { Name = $"prepare-ssh-context-{DockerComposeEnvironment.Name}", Action = PrepareSSHContextStep };

        var configureRegistry = new PipelineStep { Name = $"configure-registry-{DockerComposeEnvironment.Name}", Action = ConfigureRegistryStep };

        var pushImages = new PipelineStep { Name = $"push-images-{DockerComposeEnvironment.Name}", Action = PushImagesStep, DependsOnSteps = [$"prepare-{DockerComposeEnvironment.Name}"] };
        pushImages.DependsOn(configureRegistry);

        var establishSsh = new PipelineStep { Name = $"establish-ssh-{DockerComposeEnvironment.Name}", Action = EstablishSSHConnectionStep }; // tests connectivity
        establishSsh.DependsOn(prepareSshContext);

        var prepareRemote = new PipelineStep { Name = $"prepare-remote-{DockerComposeEnvironment.Name}", Action = PrepareRemoteEnvironmentStep };
        prepareRemote.DependsOn(establishSsh);

        var mergeEnv = new PipelineStep { Name = $"merge-environment-{DockerComposeEnvironment.Name}", Action = MergeEnvironmentFileStep, DependsOnSteps = [$"prepare-{DockerComposeEnvironment.Name}"] };
        mergeEnv.DependsOn(prepareRemote);
        mergeEnv.DependsOn(pushImages);

        var transferFiles = new PipelineStep { Name = $"transfer-files-{DockerComposeEnvironment.Name}", Action = TransferDeploymentFilesPipelineStep };
        transferFiles.DependsOn(mergeEnv);

        var deploy = new PipelineStep { Name = $"docker-via-ssh-{DockerComposeEnvironment.Name}", Action = DeployApplicationStep };
        deploy.DependsOn(transferFiles);

        // Post-deploy: Extract dashboard login token from logs
        var extractDashboardToken = new PipelineStep { Name = $"extract-dashboard-token-{DockerComposeEnvironment.Name}", Action = ExtractDashboardLoginTokenStep };
        extractDashboardToken.DependsOn(deploy);

        // Final cleanup step to close SSH/SCP connections
        var cleanup = new PipelineStep { Name = $"cleanup-ssh-{DockerComposeEnvironment.Name}", Action = CleanupSSHConnectionStep };
        cleanup.DependsOn(extractDashboardToken);

        var deploySshStep = new PipelineStep { Name = $"deploy-docker-ssh-{DockerComposeEnvironment.Name}", Action = context => Task.CompletedTask };
        deploySshStep.DependsOn(cleanup);
        deploySshStep.RequiredBy(WellKnownPipelineSteps.Deploy);

        return [prereqs, prepareSshContext, configureRegistry, pushImages, establishSsh, prepareRemote, mergeEnv, transferFiles, deploy, extractDashboardToken, cleanup, deploySshStep];
    }

    public Task ConfigurePipelineAsync(PipelineConfigurationContext context)
    {
        var dockerComposeUpStep = context.Steps.FirstOrDefault(s => s.Name == $"docker-compose-up-{DockerComposeEnvironment.Name}");

        var deployStep = context.Steps.FirstOrDefault(s => s.Name == WellKnownPipelineSteps.Deploy);

        // Remove docker compose up from the deployment pipeline
        // not needed for SSH deployment
        deployStep?.DependsOnSteps.Remove($"docker-compose-up-{DockerComposeEnvironment.Name}");
        dockerComposeUpStep?.RequiredBySteps.Remove(WellKnownPipelineSteps.Deploy);

        // No additional configuration needed at this time
        return Task.CompletedTask;
    }


    #region Deploy Step Helpers

    private async Task EstablishAndTestSSHConnectionStepAsync(SSHConnectionContext sshContext, PipelineStepContext context)
    {
        var step = context.ReportingStep;
        await _sshConnectionManager.EstablishConnectionAsync(sshContext, step, context.CancellationToken);
        await step.SucceedAsync("SSH connection established and tested successfully");
    }

    private async Task PrepareRemoteEnvironmentStepAsync(SSHConnectionContext sshContext, PipelineStepContext context)
    {
        var step = context.ReportingStep;
        await PrepareRemoteEnvironment(context, sshContext.RemoteDeployPath, step, context.CancellationToken);
        await step.SucceedAsync("Remote environment ready for deployment");
    }

    private async Task TransferDeploymentFilesStepAsync(SSHConnectionContext sshContext, PipelineStepContext context)
    {
        var step = context.ReportingStep;
        await TransferDeploymentFiles(sshContext.RemoteDeployPath, context, step, context.CancellationToken);
        await step.SucceedAsync("File transfer completed");
    }

    private async Task DeployOnRemoteServerStepAsync(SSHConnectionContext sshContext, Dictionary<string, string> imageTags, PipelineStepContext context)
    {
        var step = context.ReportingStep;
        var deploymentInfo = await DeployOnRemoteServer(context, sshContext.RemoteDeployPath, imageTags, step, context.CancellationToken);
        await step.SucceedAsync($"Application deployed successfully! {deploymentInfo}");
    }

    private async Task CleanupSSHConnectionStep(PipelineStepContext context)
    {
        context.Logger.LogDebug("Starting SSH connection cleanup");
        await _sshConnectionManager.DisconnectAsync();
        context.Logger.LogDebug("SSH connection cleanup completed");
    }
    #endregion

    #region Pipeline Step Implementations
    private async Task PrepareSSHContextStep(PipelineStepContext context)
    {
        try
        {
            var interactionService = context.Services.GetRequiredService<IInteractionService>();
            var configuration = context.Services.GetRequiredService<IConfiguration>();
            var configDefaults = ConfigurationUtility.GetConfigurationDefaults(configuration);

            var sshContext = await PrepareSSHConnectionContext(context, configDefaults, interactionService);
            SSHContextTask.TrySetResult(sshContext);
        }
        catch (Exception ex)
        {
            SSHContextTask.TrySetException(ex);
            throw;
        }
    }

    private async Task ConfigureRegistryStep(PipelineStepContext context)
    {
        try
        {
            var configuration = context.Services.GetRequiredService<IConfiguration>();
            var interactionService = context.Services.GetRequiredService<IInteractionService>();

            var step = context.ReportingStep;

            var section = configuration.GetSection(RegistryContextKey);

            string registryUrl = section["RegistryUrl"] ?? "";
            string? repositoryPrefix = section["RepositoryPrefix"];
            string? registryUsername = section["RegistryUsername"];
            string? registryPassword = section["RegistryPassword"];

            if (!string.IsNullOrWhiteSpace(registryUrl) && !string.IsNullOrWhiteSpace(repositoryPrefix))
            {
                var registryConfig = new RegistryConfiguration(registryUrl, repositoryPrefix, registryUsername, registryPassword);
                RegistryConfigTask.TrySetResult(registryConfig);
                return;
            }

            var configDefaults = ConfigurationUtility.GetConfigurationDefaults(configuration);

            var inputs = new InteractionInput[]
            {
                new() { Name = "registryUrl", Required = true, InputType = InputType.Text, Label = "Container Registry URL", Value = "docker.io" },
                new() { Name = "repositoryPrefix", InputType = InputType.Text, Label = "Image Repository Prefix", Value = configDefaults.RepositoryPrefix },
                new() { Name = "registryUsername", InputType = InputType.Text, Label = "Registry Username", Value = configDefaults.RegistryUsername },
                new() { Name = "registryPassword", InputType = InputType.SecretText, Label = "Registry Password/Token" }
            };

            context.Logger.LogInformation("Collecting registry settings...");

            var result = await interactionService.PromptInputsAsync(
                "Container Registry Configuration",
                "Provide container registry details (leave credentials blank for anonymous access).\n",
                inputs,
                cancellationToken: context.CancellationToken);

            if (result.Canceled)
            {
                throw new InvalidOperationException("Registry configuration was canceled");
            }

            registryUrl = result.Data["registryUrl"].Value ?? throw new InvalidOperationException("Registry URL is required");
            repositoryPrefix = result.Data["repositoryPrefix"].Value?.Trim();
            registryUsername = result.Data["registryUsername"].Value;
            registryPassword = result.Data["registryPassword"].Value;

            if (!string.IsNullOrEmpty(registryUsername) && !string.IsNullOrEmpty(registryPassword))
            {
                await using var loginTask = await step.CreateTaskAsync("Authenticating", context.CancellationToken);
                var loginResult = await _dockerCommandExecutor.ExecuteDockerLogin(registryUrl, registryUsername, registryPassword, context.CancellationToken);
                if (loginResult.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Docker login failed: {loginResult.Error}");
                }
                await loginTask.SucceedAsync($"Authenticated with {registryUrl}", context.CancellationToken);
            }
            else
            {
                context.Logger.LogInformation("Skipping authentication (no credentials provided)");
            }

            var finalRegistryConfig = new RegistryConfiguration(registryUrl, repositoryPrefix, registryUsername, registryPassword);
            RegistryConfigTask.TrySetResult(finalRegistryConfig);

            // Persist
            try
            {
                var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
                var registryStateSection = await deploymentStateManager.AcquireSectionAsync(RegistryContextKey, context.CancellationToken);
                registryStateSection.Data["RegistryUrl"] = registryUrl;
                registryStateSection.Data["RepositoryPrefix"] = repositoryPrefix ?? string.Empty;
                registryStateSection.Data["RegistryUsername"] = registryUsername ?? string.Empty;
                registryStateSection.Data["RegistryPassword"] = registryPassword ?? string.Empty;

                await deploymentStateManager.SaveSectionAsync(registryStateSection, context.CancellationToken);
            }
            catch (Exception ex)
            {
                context.Logger.LogDebug("Failed to persist registry configuration: {Message}", ex.Message);
            }

            await step.SucceedAsync("Registry configured");
        }
        catch (Exception ex)
        {
            RegistryConfigTask.TrySetException(ex);
            throw;
        }
    }

    private async Task PushImagesStep(PipelineStepContext context)
    {
        try
        {
            var registryConfig = await RegistryConfigTask.Task;
            var imageTags = await PushContainerImagesToRegistry(context, registryConfig, context.CancellationToken);
            ImageTagsTask.TrySetResult(imageTags);
        }
        catch (Exception ex)
        {
            ImageTagsTask.TrySetException(ex);
            throw;
        }
    }

    private async Task EstablishSSHConnectionStep(PipelineStepContext context)
    {
        var sshContext = await SSHContextTask.Task;
        await EstablishAndTestSSHConnectionStepAsync(sshContext, context);

        // Save the SSH context to deployment state for future runs
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var sshSection = await deploymentStateManager.AcquireSectionAsync(SshContextKey, context.CancellationToken);

        // Save SSH context to state for future use
        sshSection.Data[nameof(SSHConnectionContext.TargetHost)] = sshContext.TargetHost;
        sshSection.Data[nameof(SSHConnectionContext.SshUsername)] = sshContext.SshUsername;
        sshSection.Data[nameof(SSHConnectionContext.SshPassword)] = sshContext.SshPassword ?? "";
        sshSection.Data[nameof(SSHConnectionContext.SshKeyPath)] = sshContext.SshKeyPath ?? "";
        sshSection.Data[nameof(SSHConnectionContext.SshPort)] = sshContext.SshPort;
        sshSection.Data[nameof(SSHConnectionContext.RemoteDeployPath)] = sshContext.RemoteDeployPath;
        await deploymentStateManager.SaveSectionAsync(sshSection, context.CancellationToken);

    }

    private async Task PrepareRemoteEnvironmentStep(PipelineStepContext context)
    {
        var sshContext = await SSHContextTask.Task;
        await PrepareRemoteEnvironmentStepAsync(sshContext, context);
    }

    private async Task MergeEnvironmentFileStep(PipelineStepContext context)
    {
        var sshContext = await SSHContextTask.Task;
        var imageTags = await ImageTagsTask.Task;
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        await MergeAndUpdateEnvironmentFile(sshContext.RemoteDeployPath, imageTags, context, interactionService, context.CancellationToken);
    }

    private async Task TransferDeploymentFilesPipelineStep(PipelineStepContext context)
    {
        var sshContext = await SSHContextTask.Task;
        await TransferDeploymentFilesStepAsync(sshContext, context);
    }

    private async Task DeployApplicationStep(PipelineStepContext context)
    {
        var sshContext = await SSHContextTask.Task;
        var imageTags = await ImageTagsTask.Task;
        await DeployOnRemoteServerStepAsync(sshContext, imageTags, context);
    }
    #endregion

    private async Task ExtractDashboardLoginTokenStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        // We'll attempt to locate the dashboard service logs (service name convention: <composeName>-dashboard)
        var serviceName = await DashboardServiceNameTask.Task;

        if (string.IsNullOrEmpty(serviceName))
        {
            await step.WarnAsync("Dashboard service not found, skipping token extraction.", context.CancellationToken);
            return;
        }

        // Use the RemoteServiceInspectionService to extract the token
        var token = await _remoteServiceInspectionService.ExtractDashboardTokenAsync(
            serviceName,
            TimeSpan.FromSeconds(10),
            context.CancellationToken);

        if (token is null)
        {
            await step.WarnAsync("Dashboard login token not detected within 10s polling window.", context.CancellationToken);
            return;
        }

        // Persist token to local output directory
        var tokenFile = Path.Combine(OutputPath, "dashboard-login-token.txt");
        await File.WriteAllTextAsync(tokenFile, token + Environment.NewLine, context.CancellationToken);
        await step.SucceedAsync($"Dashboard login token written to {tokenFile}");
    }

    private async Task CheckPrerequisitesConcurrently(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        // Create all prerequisite check tasks
        var dockerTask = _dockerCommandExecutor.CheckDockerAvailability(step, context.CancellationToken);
        var dockerComposeTask = _dockerCommandExecutor.CheckDockerCompose(step, context.CancellationToken);

        // Run all prerequisite checks concurrently
        await Task.WhenAll(dockerTask, dockerComposeTask);
        await step.SucceedAsync("All prerequisites verified successfully");
    }

    private async Task<SSHConnectionContext> PrepareSSHConnectionContext(PipelineStepContext context, DockerSSHConfiguration configDefaults, IInteractionService interactionService)
    {
        // Local function to build host options for selection
        static List<KeyValuePair<string, string>> BuildHostOptions(DockerSSHConfiguration configDefaults, SSHConfiguration sshConfig)
        {
            var hostOptions = new List<KeyValuePair<string, string>>();

            // Add config default first if available
            if (!string.IsNullOrEmpty(configDefaults.SshHost))
            {
                hostOptions.Add(new KeyValuePair<string, string>(configDefaults.SshHost, $"{configDefaults.SshHost} (configured)"));
            }

            return hostOptions;
        }

        // Local function to build SSH key options for selection
        static List<KeyValuePair<string, string>> BuildSshKeyOptions(SSHConfiguration sshConfig)
        {
            var sshKeyOptions = new List<KeyValuePair<string, string>>();

            // Add discovered SSH keys
            foreach (var keyPath in sshConfig.AvailableKeyPaths)
            {
                var keyName = Path.GetFileName(keyPath);
                sshKeyOptions.Add(new KeyValuePair<string, string>(keyPath, keyName));
            }

            // Add password authentication option
            sshKeyOptions.Add(new KeyValuePair<string, string>("", "Password Authentication (no key)"));

            return sshKeyOptions;
        }

        // Local function for choice prompts
        async Task<string> PromptForChoice(string title, string description, string label, List<KeyValuePair<string, string>> options, string? defaultValue = null)
        {
            var inputs = new InteractionInput[]
            {
                new() {
                    Name = label,
                    InputType = InputType.Choice,
                    AllowCustomChoice = true,
                    Label = label,
                    Options = options,
                    Value = defaultValue
                }
            };

            var result = await interactionService.PromptInputsAsync(title, description, inputs);

            if (result.Canceled)
            {
                throw new InvalidOperationException($"{title} was canceled");
            }

            return result.Data[0].Value ?? "";
        }

        // Local function for single text input prompts
        async Task<string> PromptForSingleText(string title, string description, string label, bool required = true, bool secret = false)
        {
            var inputs = new InteractionInput[]
            {
                new() {
                    Name = label,
                    Required = required,
                    InputType = secret ? InputType.SecretText: InputType.Text,
                    Label = label
                }
            };

            var result = await interactionService.PromptInputsAsync(title, description, inputs);

            if (result.Canceled)
            {
                throw new InvalidOperationException($"{title} was canceled");
            }

            var value = result.Data[0].Value;
            if (required && string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"{label} is required");
            }

            return value ?? "";
        }

        // Local function to prompt for target host
        async Task<string> PromptForTargetHost(List<KeyValuePair<string, string>> hostOptions)
        {
            // First prompt: Host selection, it might be an IP address, so keep it secret
            return await PromptForSingleText(
                    "Target Host Configuration",
                    "No configured or known hosts found. Please enter the target server host for deployment.",
                    "Target Server Host"
                );
        }

        // Local function to prompt for SSH key path
        async Task<string?> PromptForSshKeyPath(List<KeyValuePair<string, string>> sshKeyOptions, DockerSSHConfiguration configDefaults, SSHConfiguration sshConfig)
        {
            // First prompt: SSH authentication method selection
            var defaultKeyPath = !string.IsNullOrEmpty(configDefaults.SshKeyPath) ? configDefaults.SshKeyPath : (sshConfig.DefaultKeyPath ?? "");

            var selectedKeyPath = await PromptForChoice(
                "SSH Authentication Method",
                "Please select how you want to authenticate with the SSH server.",
                "SSH Authentication Method",
                sshKeyOptions,
                defaultKeyPath
            );

            // Return the selected key path (could be empty for password auth, or an actual path)
            return string.IsNullOrEmpty(selectedKeyPath) ? null : selectedKeyPath;
        }

        // Local function to prompt for SSH details
        async Task<(string SshUsername, string? SshPassword, string SshPort, string RemoteDeployPath)> PromptForSshDetails(
            string targetHost, string? sshKeyPath, DockerSSHConfiguration configDefaults, SSHConfiguration sshConfig)
        {
            var inputs = new InteractionInput[]
            {
                new() {
                    Name = "sshUsername",
                    Required = true,
                    InputType = InputType.Text,
                    Label = "SSH Username",
                    Value = configDefaults.SshUsername ?? "root"
                },
                new() {
                    Name = "sshPassword",
                    InputType = InputType.SecretText,
                    Label = "SSH Password",

                },
                new() {
                    Name = "sshPort",
                    InputType = InputType.Text,
                    Label = "SSH Port",
                    Value = !string.IsNullOrEmpty(configDefaults.SshPort) && configDefaults.SshPort != "22" ? configDefaults.SshPort : "22"
                },
                new() {
                    Name = "remoteDeployPath",
                    InputType = InputType.Text,
                    Label = "Remote Deploy Path",
                    Value = !string.IsNullOrEmpty(configDefaults.RemoteDeployPath) ? configDefaults.RemoteDeployPath : sshConfig.DefaultDeployPath
                }
            };

            var result = await interactionService.PromptInputsAsync(
                $"SSH Configuration for {targetHost}",
                $"Please provide the SSH configuration details for connecting to {targetHost}.\n",
                inputs
            );

            if (result.Canceled)
            {
                throw new InvalidOperationException("SSH configuration was canceled");
            }

            var sshUsername = result.Data["sshUsername"].Value ?? throw new InvalidOperationException("SSH username is required");
            var sshPassword = string.IsNullOrEmpty(result.Data["sshPassword"].Value) ? null : result.Data["sshPassword"].Value;
            var sshPort = result.Data["sshPort"].Value ?? "22";
            var remoteDeployPath = result.Data["remoteDeployPath"].Value ?? $"/home/{sshUsername}/aspire-app";

            return (sshUsername, sshPassword, sshPort, remoteDeployPath);
        }

        // Try to load the stated SSH context if available
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var section = configuration.GetSection(SshContextKey);

        var targetHost = section[nameof(SSHConnectionContext.TargetHost)];
        var sshUsername = section[nameof(SSHConnectionContext.SshUsername)];
        var sshPort = section[nameof(SSHConnectionContext.SshPort)];
        var sshPassword = section[nameof(SSHConnectionContext.SshPassword)];
        var sshKeyPath = section[nameof(SSHConnectionContext.SshKeyPath)];
        var remoteDeployPath = section[nameof(SSHConnectionContext.RemoteDeployPath)];

        if (!string.IsNullOrEmpty(targetHost) &&
            !string.IsNullOrEmpty(sshUsername) &&
            !string.IsNullOrEmpty(sshPort))
        {
            return new SSHConnectionContext
            {
                TargetHost = targetHost,
                SshUsername = sshUsername,
                SshPassword = string.IsNullOrEmpty(sshPassword) ? null : sshPassword,
                SshKeyPath = string.IsNullOrEmpty(sshKeyPath) ? null : sshKeyPath,
                SshPort = string.IsNullOrEmpty(sshPort) ? "22" : sshPort,
                RemoteDeployPath = string.IsNullOrEmpty(remoteDeployPath) ? $"/home/{sshUsername}/aspire-app" : remoteDeployPath
            };
        }

        // Main method logic starts here
        // Discover SSH configuration
        var sshConfig = await _sshConfigurationDiscovery.DiscoverSSHConfiguration(context);

        // Build host options for selection
        var hostOptions = BuildHostOptions(configDefaults, sshConfig);

        // Get target host through progressive prompting
        targetHost ??= await PromptForTargetHost(hostOptions);

        // Build SSH key options for selection
        var sshKeyOptions = BuildSshKeyOptions(sshConfig);

        // Get SSH key path through progressive prompting
        sshKeyPath ??= await PromptForSshKeyPath(sshKeyOptions, configDefaults, sshConfig);

        // Get SSH configuration details
        var sshDetails = await PromptForSshDetails(targetHost, sshKeyPath, configDefaults, sshConfig);

        // Validate SSH authentication method
        if (string.IsNullOrEmpty(sshDetails.SshPassword) && string.IsNullOrEmpty(sshKeyPath))
        {
            throw new InvalidOperationException("Either SSH password or SSH private key path must be provided");
        }

        // Return the final SSH connection context

        var sshContext = new SSHConnectionContext
        {
            TargetHost = targetHost,
            SshUsername = sshDetails.SshUsername,
            SshPassword = sshDetails.SshPassword,
            SshKeyPath = sshKeyPath,
            SshPort = sshDetails.SshPort,
            RemoteDeployPath = ExpandRemotePath(sshDetails.RemoteDeployPath)
        };

        return sshContext;
    }

    private async Task PrepareRemoteEnvironment(PipelineStepContext context, string deployPath, IReportingStep step, CancellationToken cancellationToken)
    {
        // Prepare deployment directory
        await using var createDirTask = await step.CreateTaskAsync("Creating deployment directory", cancellationToken);
        var createdPath = await _remoteDockerEnvironmentService.PrepareDeploymentDirectoryAsync(deployPath, cancellationToken);
        await createDirTask.SucceedAsync($"Directory created: {createdPath}", cancellationToken: cancellationToken);

        // Validate Docker environment
        await using var dockerCheckTask = await step.CreateTaskAsync("Verifying Docker installation", cancellationToken);
        var dockerInfo = await _remoteDockerEnvironmentService.ValidateDockerEnvironmentAsync(cancellationToken);
        await dockerCheckTask.SucceedAsync(
            $"Docker {dockerInfo.DockerVersion}, Server {dockerInfo.ServerVersion}, Compose {dockerInfo.ComposeVersion}",
            cancellationToken: cancellationToken);

        // Check deployment state
        await using var permissionsTask = await step.CreateTaskAsync("Checking permissions and resources", cancellationToken);
        var deploymentState = await _remoteDockerEnvironmentService.GetDeploymentStateAsync(deployPath, cancellationToken);

        if (!dockerInfo.HasPermissions)
        {
            throw new InvalidOperationException("User does not have permission to run Docker commands. Add user to 'docker' group and restart the session.");
        }

        await permissionsTask.SucceedAsync(
            $"Permissions and resources validated. Existing containers: {deploymentState.ExistingContainerCount}",
            cancellationToken: cancellationToken);
    }

    private async Task TransferDeploymentFiles(string deployPath, PipelineStepContext context, IReportingStep step, CancellationToken cancellationToken)
    {
        // prepare-env step consistently outputs docker-compose.yaml
        const string dockerComposeFile = "docker-compose.yaml";
        var localPath = Path.Combine(OutputPath, dockerComposeFile);

        context.Logger.LogInformation("Scanning files for transfer...");
        if (!File.Exists(localPath))
        {
            throw new InvalidOperationException($"Required file not found: {dockerComposeFile} at {localPath}. Ensure prepare-{DockerComposeEnvironment.Name} step has run.");
        }

        context.Logger.LogDebug("Found {DockerComposeFile}, .env file handled separately", dockerComposeFile);

        await using var copyTask = await step.CreateTaskAsync("Copying and verifying docker-compose.yaml", cancellationToken);

        var remotePath = $"{deployPath}/{dockerComposeFile}";
        var transferResult = await _remoteFileService.TransferWithVerificationAsync(localPath, remotePath, cancellationToken);

        if (!transferResult.Success || !transferResult.Verified)
        {
            throw new InvalidOperationException($"File transfer verification failed: {dockerComposeFile}");
        }

        await copyTask.SucceedAsync($"✓ {dockerComposeFile} verified ({transferResult.BytesTransferred} bytes)", cancellationToken: cancellationToken);
    }

    private async Task<string> DeployOnRemoteServer(PipelineStepContext context, string deployPath, Dictionary<string, string> imageTags, IReportingStep step, CancellationToken cancellationToken)
    {
        // Stop existing containers
        await using var stopTask = await step.CreateTaskAsync("Stopping existing containers", cancellationToken);
        var stopResult = await _remoteDockerComposeService.StopAsync(deployPath, cancellationToken);

        if (stopResult.Success && !string.IsNullOrEmpty(stopResult.Output))
        {
            await stopTask.SucceedAsync($"Existing containers stopped\n{stopResult.Output.Trim()}", cancellationToken: cancellationToken);
        }
        else
        {
            await stopTask.SucceedAsync("No containers to stop or stop completed", cancellationToken: cancellationToken);
        }

        // Pull latest images
        await using var pullTask = await step.CreateTaskAsync("Pulling latest images", cancellationToken);
        context.Logger.LogDebug("Pulling latest container images...");

        var pullResult = await _remoteDockerComposeService.PullImagesAsync(deployPath, cancellationToken);

        if (!string.IsNullOrEmpty(pullResult.Output))
        {
            await pullTask.SucceedAsync($"Latest images pulled\n{pullResult.Output.Trim()}", cancellationToken: cancellationToken);
        }
        else
        {
            await pullTask.SucceedAsync("Image pull completed (no output or using local images)", cancellationToken: cancellationToken);
        }

        // Start services
        await using var startTask = await step.CreateTaskAsync("Starting new containers", cancellationToken);
        context.Logger.LogDebug("Starting application containers...");

        var startResult = await _remoteDockerComposeService.StartAsync(deployPath, cancellationToken);
        await startTask.SucceedAsync("New containers started", cancellationToken: cancellationToken);

        // Use the new HealthCheckUtility to check each service individually
        await HealthCheckUtility.CheckServiceHealth(deployPath, _sshConnectionManager.SshClient!, step, cancellationToken);

        // Get deployment status with health info and URLs
        var deploymentStatus = await _remoteDeploymentMonitorService.GetStatusAsync(deployPath, cancellationToken);

        var dashboardServiceName = deploymentStatus.ServiceUrls.Keys.FirstOrDefault(
            s => s.Contains(DockerComposeEnvironment.Name + "-dashboard", StringComparison.OrdinalIgnoreCase));
        DashboardServiceNameTask.TrySetResult(dashboardServiceName ?? "");

        // Format port information as a nice table
        var serviceUrlsForTable = deploymentStatus.ServiceUrls.ToDictionary(
            kvp => kvp.Key,
            kvp => new List<string> { kvp.Value });
        var serviceTable = PortInformationUtility.FormatServiceUrlsAsTable(serviceUrlsForTable);

        return $"Services running: {deploymentStatus.HealthyServices} of {deploymentStatus.TotalServices} containers healthy.\n{serviceTable}";
    }

    public async ValueTask DisposeAsync()
    {
        await _sshConnectionManager.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<Dictionary<string, string>> PushContainerImagesToRegistry(PipelineStepContext context, RegistryConfiguration registryConfig, CancellationToken cancellationToken)
    {
        var imageTags = new Dictionary<string, string>();
        var registryUrl = registryConfig.RegistryUrl;
        var repositoryPrefix = registryConfig.RepositoryPrefix;

        // Create the progress step for container image pushing
        var step = context.ReportingStep;

        // Get the environment name
        var hostEnvironment = context.Services.GetRequiredService<IHostEnvironment>();
        var environmentName = hostEnvironment.EnvironmentName;

        // Read the environment-specific .env file to find all built images
        var envFilePath = Path.Combine(OutputPath, $".env.{environmentName}");
        if (!File.Exists(envFilePath))
        {
            throw new InvalidOperationException($".env.{environmentName} file not found at {envFilePath}. Ensure prepare-{DockerComposeEnvironment.Name} step has run.");
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

        // Tag images for registry (one task per tag operation)
        foreach (var (envKey, localImageName) in imageVars)
        {
            // Extract service name from env key (e.g., "APISERVICE_IMAGE" -> "apiservice")
            var serviceName = envKey.Substring(0, envKey.Length - "_IMAGE".Length).ToLowerInvariant();

            await using var tagTask = await step.CreateTaskAsync($"Tagging {serviceName} image", cancellationToken);

            // Construct the target image name
            var targetImageName = !string.IsNullOrEmpty(repositoryPrefix)
                ? $"{registryUrl}/{repositoryPrefix}/{serviceName}:{imageTag}"
                : $"{registryUrl}/{serviceName}:{imageTag}";

            // Tag the image
            var tagResult = await _dockerCommandExecutor.ExecuteDockerCommand($"tag {localImageName} {targetImageName}", cancellationToken);

            if (tagResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to tag image {localImageName}: {tagResult.Error}");
            }

            imageTags[serviceName] = targetImageName;
            await tagTask.SucceedAsync($"Successfully tagged {serviceName} image: {targetImageName}", cancellationToken: cancellationToken);
        }


        async Task PushContainerImageAsync(IReportingStep step, string serviceName, string targetImageName, CancellationToken cancellationToken)
        {
            await using var pushTask = await step.CreateTaskAsync($"Pushing {serviceName} image", cancellationToken);

            var pushResult = await _dockerCommandExecutor.ExecuteDockerCommand($"push {targetImageName}", cancellationToken);

            if (pushResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to push image {targetImageName}: {pushResult.Error}");
            }

            await pushTask.SucceedAsync($"Successfully pushed {serviceName} image: {targetImageName}", cancellationToken: cancellationToken);
        }

        // Push images to registry (one task per image)
        var tasks = new List<Task>();
        foreach (var (serviceName, targetImageName) in imageTags)
        {
            tasks.Add(PushContainerImageAsync(step, serviceName, targetImageName, cancellationToken));
        }

        await Task.WhenAll(tasks);

        await step.SucceedAsync("Container images pushed successfully.");
        return imageTags;
    }

    private async Task MergeAndUpdateEnvironmentFile(string remoteDeployPath, Dictionary<string, string> imageTags, PipelineStepContext context, IInteractionService interactionService, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(OutputPath))
        {
            throw new InvalidOperationException("Output path is not set.");
        }

        // Get the environment name
        var hostEnvironment = context.Services.GetRequiredService<IHostEnvironment>();
        var environmentName = hostEnvironment.EnvironmentName;

        // Read the environment-specific .env file generated by prepare-env step
        var envFilePath = Path.Combine(OutputPath, $".env.{environmentName}");
        if (!File.Exists(envFilePath))
        {
            throw new InvalidOperationException($".env.{environmentName} file not found at {envFilePath}. Ensure prepare-{DockerComposeEnvironment.Name} step has run.");
        }

        var finalizeStep = context.ReportingStep;

        await using var envFileTask = await finalizeStep.CreateTaskAsync("Creating and transferring environment file", cancellationToken);

        // Deploy environment using the service
        var deploymentResult = await _remoteEnvironmentService.DeployEnvironmentAsync(
            envFilePath,
            remoteDeployPath,
            imageTags,
            cancellationToken);

        context.Logger.LogDebug("Processed {Count} environment variables", deploymentResult.VariableCount);

        await envFileTask.SucceedAsync($"Environment file successfully transferred to {deploymentResult.RemoteEnvPath}", cancellationToken);

        await finalizeStep.SucceedAsync($"Environment configuration finalized with {deploymentResult.VariableCount} variables");
    }

    private static string ExpandRemotePath(string path)
    {
        if (path.StartsWith("~"))
        {
            // Replace ~ with $HOME for shell expansion
            return path.Replace("~", "$HOME");
        }

        return path;
    }

    internal sealed record RegistryConfiguration(string RegistryUrl, string? RepositoryPrefix, string? RegistryUsername, string? RegistryPassword);
}
