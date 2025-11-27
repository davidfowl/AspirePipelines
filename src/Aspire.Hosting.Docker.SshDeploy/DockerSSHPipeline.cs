#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Aspire.Hosting.Docker.SshDeploy.Services;
using Aspire.Hosting.Docker.SshDeploy.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Docker;
using RegistryConfiguration = Aspire.Hosting.Docker.SshDeploy.Services.RegistryConfiguration;

internal class DockerSSHPipeline(
    DockerComposeEnvironmentResource dockerComposeEnvironmentResource,
    DockerCommandExecutor dockerCommandExecutor,
    EnvironmentFileReader environmentFileReader,
    IPipelineOutputService pipelineOutputService,
    SSHConnectionFactory sshConnectionFactory,
    DockerRegistryService dockerRegistryService,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly DockerCommandExecutor _dockerCommandExecutor = dockerCommandExecutor;
    private readonly EnvironmentFileReader _environmentFileReader = environmentFileReader;
    private readonly SSHConnectionFactory _sshConnectionFactory = sshConnectionFactory;
    private readonly DockerRegistryService _dockerRegistryService = dockerRegistryService;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _hostEnvironment = hostEnvironment;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    // Execution-scoped state (set during pipeline execution)
    private ISSHConnectionManager? _sshConnectionManager;
    private RemoteOperationsFactory? _remoteOperationsFactory;
    private string? _remoteDeployPath;
    private Dictionary<string, string>? _imageTags;
    private RegistryConfiguration? _registryConfig;
    private string? _dashboardServiceName;

    // Properties with null-checking for required state
    private RemoteOperationsFactory RemoteOperationsFactory => _remoteOperationsFactory ?? throw new InvalidOperationException("Remote operations factory not initialized. Ensure SSH connection step has completed.");
    private string RemoteDeployPath => _remoteDeployPath ?? throw new InvalidOperationException("Remote deploy path not initialized. Ensure SSH connection step has completed.");
    private Dictionary<string, string> ImageTags => _imageTags ?? throw new InvalidOperationException("Image tags not initialized. Ensure push images step has completed.");
    private RegistryConfiguration RegistryConfig => _registryConfig ?? throw new InvalidOperationException("Registry configuration not initialized. Ensure configure registry step has completed.");

    public DockerComposeEnvironmentResource DockerComposeEnvironment { get; } = dockerComposeEnvironmentResource;

    public string OutputPath => pipelineOutputService.GetOutputDirectory();

    public IEnumerable<PipelineStep> CreateSteps(PipelineStepFactoryContext context)
    {
        // Input gathering steps that must complete before any building/deploying
        // Strategy: Declare RequiredBy(WellKnownPipelineSteps.BuildPrereq) so build steps automatically wait for config
        // This creates: our steps → build-prereq → builds

        // Verifies Docker is available locally
        var prereqs = new PipelineStep { Name = $"ssh-prereq-{DockerComposeEnvironment.Name}", Action = CheckPrerequisitesConcurrently };
        prereqs.RequiredBy(WellKnownPipelineSteps.BuildPrereq);

        // Establish SSH connection and gather SSH credentials (runs in parallel with registry config)
        var establishSsh = new PipelineStep { Name = $"establish-ssh-{DockerComposeEnvironment.Name}", Action = EstablishSSHConnectionStep };
        establishSsh.RequiredBy(WellKnownPipelineSteps.BuildPrereq);

        // Gather registry configuration (runs in parallel with SSH)
        var configureRegistry = new PipelineStep { Name = $"configure-registry-{DockerComposeEnvironment.Name}", Action = ConfigureRegistryStep };
        configureRegistry.RequiredBy(WellKnownPipelineSteps.BuildPrereq);

        // Configure deployment path (depends on SSH being established)
        var configureDeployment = new PipelineStep { Name = $"configure-deployment-{DockerComposeEnvironment.Name}", Action = ConfigureDeploymentStep };
        configureDeployment.DependsOn(establishSsh);
        configureDeployment.RequiredBy(WellKnownPipelineSteps.BuildPrereq);

        // Prepare remote environment (depends on deployment being configured)
        var prepareRemote = new PipelineStep { Name = $"prepare-remote-{DockerComposeEnvironment.Name}", Action = PrepareRemoteEnvironmentStep };
        prepareRemote.DependsOn(configureDeployment);

        // Push images (depends on prepare step which builds images, registry config, and remote being ready)
        var pushImages = new PipelineStep { Name = $"push-images-{DockerComposeEnvironment.Name}", Action = PushImagesStep, DependsOnSteps = [$"prepare-{DockerComposeEnvironment.Name}"] };
        pushImages.DependsOn(configureRegistry);
        pushImages.DependsOn(prepareRemote); // Don't push until we know remote is ready

        // Merge environment file (depends on both prepare and push completing)
        var mergeEnv = new PipelineStep { Name = $"merge-environment-{DockerComposeEnvironment.Name}", Action = MergeEnvironmentFileStep, DependsOnSteps = [$"prepare-{DockerComposeEnvironment.Name}"] };
        mergeEnv.DependsOn(pushImages);

        // Transfer files (depends on environment being merged)
        var transferFiles = new PipelineStep { Name = $"transfer-files-{DockerComposeEnvironment.Name}", Action = TransferDeploymentFilesPipelineStep };
        transferFiles.DependsOn(mergeEnv);

        // Deploy (depends on files being transferred)
        var deploy = new PipelineStep { Name = $"remote-docker-deploy-{DockerComposeEnvironment.Name}", Action = DeployApplicationStep };
        deploy.DependsOn(transferFiles);

        // Extract dashboard login token from logs
        var extractDashboardToken = new PipelineStep { Name = $"extract-dashboard-token-{DockerComposeEnvironment.Name}", Action = ExtractDashboardLoginTokenStep };
        extractDashboardToken.DependsOn(deploy);

        // Cleanup SSH/SCP connections
        var cleanup = new PipelineStep { Name = $"cleanup-ssh-{DockerComposeEnvironment.Name}", Action = CleanupSSHConnectionStep };
        cleanup.DependsOn(extractDashboardToken);

        // Final coordination step
        var deploySshStep = new PipelineStep { Name = $"deploy-docker-ssh-{DockerComposeEnvironment.Name}", Action = context => Task.CompletedTask };
        deploySshStep.DependsOn(cleanup);
        deploySshStep.RequiredBy(WellKnownPipelineSteps.Deploy);

        return [prereqs, establishSsh, configureRegistry, configureDeployment, prepareRemote, pushImages, mergeEnv, transferFiles, deploy, extractDashboardToken, cleanup, deploySshStep];
    }

    public Task ConfigurePipelineAsync(PipelineConfigurationContext context)
    {
        var dockerComposeUpStep = context.Steps.FirstOrDefault(s => s.Name == $"docker-compose-up-{DockerComposeEnvironment.Name}");
        var deployStep = context.Steps.FirstOrDefault(s => s.Name == WellKnownPipelineSteps.Deploy);
        var prepareStep = context.Steps.FirstOrDefault(s => s.Name == $"prepare-{DockerComposeEnvironment.Name}");

        // Remove docker compose up from the deployment pipeline
        // not needed for SSH deployment
        deployStep?.DependsOnSteps.Remove($"docker-compose-up-{DockerComposeEnvironment.Name}");
        dockerComposeUpStep?.RequiredBySteps.Remove(WellKnownPipelineSteps.Deploy);

        // Make the built-in prepare step depend on our prerequisites check
        // This ensures Docker is available before building images
        prepareStep?.DependsOnSteps.Add($"ssh-prereq-{DockerComposeEnvironment.Name}");

        // Note: We elegantly chain dependencies without directly modifying build steps!
        // The chain works like this:
        //   1. Our config steps declare RequiredBy(WellKnownPipelineSteps.BuildPrereq)
        //   2. Build steps already depend on build-prereq (from framework)
        // Result: our config steps → build-prereq → build steps
        // This ensures all input gathering happens before any building/deploying.

        return Task.CompletedTask;
    }


    #region Deploy Step Helpers

    private async Task CleanupSSHConnectionStep(PipelineStepContext context)
    {
        context.Logger.LogDebug("Starting SSH connection cleanup");
        if (_sshConnectionManager != null)
        {
            await _sshConnectionManager.DisconnectAsync();
        }
        context.Logger.LogDebug("SSH connection cleanup completed");
    }
    #endregion

    #region Pipeline Step Implementations
    private async Task EstablishSSHConnectionStep(PipelineStepContext context)
    {
        // Use factory to create connected manager (handles prompting, connection, and persistence)
        var step = context.ReportingStep;
        _sshConnectionManager = await _sshConnectionFactory.CreateConnectedManagerAsync(context, step, cancellationToken: context.CancellationToken);

        // Create remote operations factory with the connected manager
        _remoteOperationsFactory = new RemoteOperationsFactory(
            _sshConnectionManager,
            _environmentFileReader,
            _loggerFactory);

        // Note: Success message already reported by SSHConnectionManager
    }

    private async Task ConfigureDeploymentStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();

        // Try to load from configuration/state first
        var deploymentSection = _configuration.GetSection("Deployment");
        _remoteDeployPath = deploymentSection["RemoteDeployPath"];
        
        // Expand tilde to $HOME if present
        if (!string.IsNullOrEmpty(_remoteDeployPath))
        {
            _remoteDeployPath = PathExpansionUtility.ExpandTildeToHome(_remoteDeployPath);
        }

        // Prompt if not configured
        if (string.IsNullOrEmpty(_remoteDeployPath))
        {
            var appName = _hostEnvironment.ApplicationName.ToLowerInvariant();
            var defaultPath = $"$HOME/aspire/apps/{appName}";

            // Use default if prompting isn't available
            if (!interactionService.IsAvailable)
            {
                _remoteDeployPath = defaultPath;
                await step.SucceedAsync($"Deployment configured: {_remoteDeployPath}");
                return;
            }

            var inputs = new InteractionInput[]
            {
                new()
                {
                    Name = "remoteDeployPath",
                    Required = true,
                    InputType = InputType.Text,
                    Label = "Remote Deployment Path",
                    Value = defaultPath
                }
            };

            var result = await interactionService.PromptInputsAsync(
                "Deployment Configuration",
                "Specify the remote directory where the application will be deployed.",
                inputs,
                cancellationToken: context.CancellationToken);

            if (result.Canceled)
            {
                throw new InvalidOperationException("Deployment configuration was canceled");
            }

            _remoteDeployPath = result.Data["remoteDeployPath"].Value ?? throw new InvalidOperationException("Remote deployment path is required");
            
            // Expand tilde to $HOME if present
            _remoteDeployPath = PathExpansionUtility.ExpandTildeToHome(_remoteDeployPath);

            // Persist the deployment path
            try
            {
                var deploymentState = await deploymentStateManager.AcquireSectionAsync("Deployment", context.CancellationToken);
                deploymentState.Data["RemoteDeployPath"] = _remoteDeployPath;
                await deploymentStateManager.SaveSectionAsync(deploymentState, context.CancellationToken);
            }
            catch (Exception ex)
            {
                context.Logger.LogDebug(ex, "Failed to persist deployment state");
            }
        }

        await step.SucceedAsync($"Deployment configured: {_remoteDeployPath}");
    }

    private async Task ConfigureRegistryStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;
        _registryConfig = await _dockerRegistryService.ConfigureRegistryAsync(context, step, context.CancellationToken);
        await step.SucceedAsync("Registry configured");
    }

    private async Task PushImagesStep(PipelineStepContext context)
    {
        _imageTags = await _dockerRegistryService.TagAndPushImagesAsync(
            RegistryConfig,
            OutputPath,
            _hostEnvironment.EnvironmentName,
            context.ReportingStep,
            context.CancellationToken);
    }

    private async Task PrepareRemoteEnvironmentStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;
        await PrepareRemoteEnvironment(context, RemoteDeployPath, step, RemoteOperationsFactory, context.CancellationToken);
        await step.SucceedAsync("Remote environment ready for deployment");
    }

    private async Task MergeEnvironmentFileStep(PipelineStepContext context)
    {
        await MergeAndUpdateEnvironmentFile(RemoteDeployPath, ImageTags, context, RemoteOperationsFactory, context.CancellationToken);
    }

    private async Task TransferDeploymentFilesPipelineStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;
        await TransferDeploymentFiles(RemoteDeployPath, context, step, RemoteOperationsFactory, context.CancellationToken);
        await step.SucceedAsync("File transfer completed");
    }

    private async Task DeployApplicationStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;
        var status = await DeployOnRemoteServer(context, RemoteDeployPath, ImageTags, step, RemoteOperationsFactory, context.CancellationToken);

        // Format and log the service URLs table
        var targetHost = _sshConnectionManager?.SshClient?.ConnectionInfo?.Host
            ?? throw new InvalidOperationException("SSH connection not established");
        var serviceUrlsForTable = status.ServiceUrls.ToDictionary(
            kvp => kvp.Key,
            kvp => new List<string> { kvp.Value });

        // Mask IP addresses in URLs by default for security
        if (!ServiceUrlFormatter.CanShowTargetHost(_configuration, targetHost))
        {
            context.Logger.LogWarning("Target host is an IP address and will be masked. Set UNSAFE_SHOW_TARGET_HOST=true to show the IP address, or use a domain name instead.");
            serviceUrlsForTable = ServiceUrlFormatter.MaskUrlHosts(serviceUrlsForTable, customDomain: null);
        }

        var serviceTable = ServiceUrlFormatter.FormatServiceUrlsAsTable(serviceUrlsForTable);
        context.Logger.LogInformation("Services running: {HealthyServices} of {TotalServices} containers healthy.\n{ServiceTable}",
            status.HealthyServices, status.TotalServices, serviceTable);

        await step.SucceedAsync($"Application deployed successfully! Services running: {status.HealthyServices} of {status.TotalServices} containers healthy.");
    }
    #endregion

    private async Task ExtractDashboardLoginTokenStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        // We'll attempt to locate the dashboard service logs (service name convention: <composeName>-dashboard)
        if (string.IsNullOrEmpty(_dashboardServiceName))
        {
            await step.WarnAsync("Dashboard service not found, skipping token extraction.", context.CancellationToken);
            return;
        }

        // Use the RemoteServiceInspectionService to extract the token
        var token = await RemoteOperationsFactory.ServiceInspectionService.ExtractDashboardTokenAsync(
            _dashboardServiceName,
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

    private async Task PrepareRemoteEnvironment(PipelineStepContext context, string deployPath, IReportingStep step, RemoteOperationsFactory factory, CancellationToken cancellationToken)
    {
        // Prepare deployment directory
        context.Logger.LogDebug("Creating deployment directory: {DeployPath}", deployPath);
        var createdPath = await factory.DockerEnvironmentService.PrepareDeploymentDirectoryAsync(deployPath, cancellationToken);
        context.Logger.LogDebug("Directory created: {CreatedPath}", createdPath);

        // Validate Docker environment
        context.Logger.LogDebug("Verifying Docker installation");
        var dockerInfo = await factory.DockerEnvironmentService.ValidateDockerEnvironmentAsync(cancellationToken);
        context.Logger.LogDebug("Docker {DockerVersion}, Server {ServerVersion}, Compose {ComposeVersion}",
            dockerInfo.DockerVersion, dockerInfo.ServerVersion, dockerInfo.ComposeVersion);

        // Check deployment state
        context.Logger.LogDebug("Checking permissions and resources");
        var deploymentState = await factory.DockerEnvironmentService.GetDeploymentStateAsync(deployPath, cancellationToken);

        if (!dockerInfo.HasPermissions)
        {
            throw new InvalidOperationException("User does not have permission to run Docker commands. Add user to 'docker' group and restart the session.");
        }

        context.Logger.LogDebug("Permissions and resources validated. Existing containers: {ExistingContainerCount}",
            deploymentState.ExistingContainerCount);

        await step.SucceedAsync("Remote environment ready for deployment");
    }

    private async Task TransferDeploymentFiles(string deployPath, PipelineStepContext context, IReportingStep step, RemoteOperationsFactory factory, CancellationToken cancellationToken)
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
        var transferResult = await factory.FileService.TransferWithVerificationAsync(localPath, remotePath, cancellationToken);

        if (!transferResult.Success || !transferResult.Verified)
        {
            throw new InvalidOperationException($"File transfer verification failed: {dockerComposeFile}");
        }

        await copyTask.SucceedAsync($"✓ {dockerComposeFile} verified ({transferResult.BytesTransferred} bytes)", cancellationToken: cancellationToken);
    }

    private async Task<ComposeStatus> DeployOnRemoteServer(PipelineStepContext context, string deployPath, Dictionary<string, string> imageTags, IReportingStep step, RemoteOperationsFactory factory, CancellationToken cancellationToken)
    {
        // Stop existing containers
        await using var stopTask = await step.CreateTaskAsync("Stopping existing containers", cancellationToken);
        var stopResult = await factory.DockerComposeService.StopAsync(deployPath, cancellationToken);

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

        var pullResult = await factory.DockerComposeService.PullImagesAsync(deployPath, cancellationToken);

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

        var startResult = await factory.DockerComposeService.StartAsync(deployPath, cancellationToken);
        await startTask.SucceedAsync("New containers started", cancellationToken: cancellationToken);

        // Get target host for URLs
        var targetHost = _sshConnectionManager?.SshClient?.ConnectionInfo?.Host
            ?? throw new InvalidOperationException("SSH connection not established");

        // Monitor service health with real-time reporting
        await HealthCheckUtility.CheckServiceHealth(deployPath, targetHost, factory.DockerComposeService, step, context.Logger, cancellationToken);

        // Get final deployment status
        var status = await factory.DockerComposeService.GetStatusAsync(deployPath, targetHost, cancellationToken);

        // Find dashboard container name (docker logs needs container name, not service name)
        var dashboardService = status.Services.FirstOrDefault(
            s => s.Service.Contains(DockerComposeEnvironment.Name + "-dashboard", StringComparison.OrdinalIgnoreCase));
        _dashboardServiceName = dashboardService?.Name;

        return status;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sshConnectionManager != null)
        {
            await _sshConnectionManager.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }


    private async Task MergeAndUpdateEnvironmentFile(string remoteDeployPath, Dictionary<string, string> imageTags, PipelineStepContext context, RemoteOperationsFactory factory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(OutputPath))
        {
            throw new InvalidOperationException("Output path is not set.");
        }

        // Read the environment-specific .env file generated by prepare-env step
        var envFilePath = Path.Combine(OutputPath, $".env.{_hostEnvironment.EnvironmentName}");
        if (!File.Exists(envFilePath))
        {
            throw new InvalidOperationException($".env.{_hostEnvironment.EnvironmentName} file not found at {envFilePath}. Ensure prepare-{DockerComposeEnvironment.Name} step has run.");
        }

        var finalizeStep = context.ReportingStep;

        await using var envFileTask = await finalizeStep.CreateTaskAsync("Creating and transferring environment file", cancellationToken);

        // Deploy environment using the service
        var deploymentResult = await factory.EnvironmentService.DeployEnvironmentAsync(
            envFilePath,
            remoteDeployPath,
            imageTags,
            cancellationToken);

        context.Logger.LogDebug("Processed {Count} environment variables", deploymentResult.VariableCount);

        await envFileTask.SucceedAsync($"Environment file successfully transferred to {deploymentResult.RemoteEnvPath}", cancellationToken);

        await finalizeStep.SucceedAsync($"Environment configuration finalized with {deploymentResult.VariableCount} variables");
    }
}
