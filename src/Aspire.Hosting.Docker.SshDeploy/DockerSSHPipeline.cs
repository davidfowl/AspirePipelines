#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIRECOMPUTE001

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
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
    ISSHConnectionFactory sshConnectionFactory,
    DockerRegistryService dockerRegistryService,
    GitHubActionsGeneratorService gitHubActionsGeneratorService,
    ISshKeyDiscoveryService sshKeyDiscoveryService,
    IProcessExecutor processExecutor,
    IConfiguration configuration,
    IHostEnvironment hostEnvironment,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly DockerCommandExecutor _dockerCommandExecutor = dockerCommandExecutor;
    private readonly EnvironmentFileReader _environmentFileReader = environmentFileReader;
    private readonly ISSHConnectionFactory _sshConnectionFactory = sshConnectionFactory;
    private readonly DockerRegistryService _dockerRegistryService = dockerRegistryService;
    private readonly GitHubActionsGeneratorService _gitHubActionsGeneratorService = gitHubActionsGeneratorService;
    private readonly ISshKeyDiscoveryService _sshKeyDiscoveryService = sshKeyDiscoveryService;
    private readonly IProcessExecutor _processExecutor = processExecutor;
    private readonly IConfiguration _configuration = configuration;
    private readonly IHostEnvironment _hostEnvironment = hostEnvironment;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger _logger = loggerFactory.CreateLogger<DockerSSHPipeline>();

    // Execution-scoped state (set during pipeline execution)
    private ISSHConnectionManager? _sshConnectionManager;
    private RemoteOperationsFactory? _remoteOperationsFactory;
    private string? _remoteDeployPath;
    private Dictionary<string, string>? _imageTags;
    private RegistryConfiguration? _registryConfig;
    private string? _dashboardServiceName;
    private bool _pruneImagesAfterDeploy;

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

        // Transfer extra files configured via WithFileTransfer (depends on core files being transferred)
        var transferExtraFiles = new PipelineStep { Name = $"transfer-extra-files-{DockerComposeEnvironment.Name}", Action = TransferExtraFilesStep };
        transferExtraFiles.DependsOn(transferFiles);

        // Deploy (depends on all files being transferred)
        var deploy = new PipelineStep { Name = $"remote-docker-deploy-{DockerComposeEnvironment.Name}", Action = DeployApplicationStep };
        deploy.DependsOn(transferExtraFiles);

        // Health check (depends on deploy, verifies containers are healthy)
        var healthCheck = new PipelineStep { Name = $"health-check-{DockerComposeEnvironment.Name}", Action = HealthCheckStep };
        healthCheck.DependsOn(deploy);

        // Extract dashboard login token from logs
        var extractDashboardToken = new PipelineStep { Name = $"extract-dashboard-token-{DockerComposeEnvironment.Name}", Action = ExtractDashboardLoginTokenStep };
        extractDashboardToken.DependsOn(healthCheck);

        // Cleanup SSH/SCP connections
        var cleanup = new PipelineStep { Name = $"cleanup-ssh-{DockerComposeEnvironment.Name}", Action = CleanupSSHConnectionStep };
        cleanup.DependsOn(extractDashboardToken);

        // Final coordination step
        var deploySshStep = new PipelineStep { Name = $"deploy-docker-ssh-{DockerComposeEnvironment.Name}", Action = context => Task.CompletedTask };
        deploySshStep.DependsOn(cleanup);
        deploySshStep.RequiredBy(WellKnownPipelineSteps.Deploy);

        // Orphan step for GitHub Actions workflow generation (invoked via `aspire do gh-action-{name}`)
        // This step has no dependencies and is not part of the normal deploy flow
        var generateGitHubWorkflow = new PipelineStep { Name = $"gh-action-{DockerComposeEnvironment.Name}", Action = GenerateGitHubActionsWorkflowStep };

        // Orphan step for tearing down the environment (invoked via `aspire do teardown-{name}`)
        // Depends on SSH connection and deployment configuration
        var teardown = new PipelineStep { Name = $"teardown-{DockerComposeEnvironment.Name}", Action = TeardownEnvironmentStep };
        teardown.DependsOn(establishSsh);
        teardown.DependsOn(configureDeployment);

        return [prereqs, establishSsh, configureRegistry, configureDeployment, prepareRemote, pushImages, mergeEnv, transferFiles, transferExtraFiles, deploy, healthCheck, extractDashboardToken, cleanup, deploySshStep, generateGitHubWorkflow, teardown];
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
            _processExecutor,
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

        // Read optional prune setting (defaults to true)
        var pruneSetting = deploymentSection["PruneImagesAfterDeploy"];
        _pruneImagesAfterDeploy = string.IsNullOrEmpty(pruneSetting) || !string.Equals(pruneSetting, "false", StringComparison.OrdinalIgnoreCase);

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
        // Gather custom image tags from resource annotations
        var customImageTags = await GetCustomImageTagsAsync(context);

        _imageTags = await _dockerRegistryService.TagAndPushImagesAsync(
            RegistryConfig,
            OutputPath,
            _hostEnvironment.EnvironmentName,
            customImageTags,
            context.ReportingStep,
            context.CancellationToken);
    }

    /// <summary>
    /// Gathers custom image tags from DeploymentImageTagCallbackAnnotation on compute resources.
    /// </summary>
    private async Task<Dictionary<string, string>?> GetCustomImageTagsAsync(PipelineStepContext context)
    {
        Dictionary<string, string>? customTags = null;

        // Only iterate over compute resources (projects, containers, etc.)
        foreach (var resource in context.Model.GetComputeResources())
        {
            // Check if the resource has a DeploymentImageTagCallbackAnnotation
            if (resource.TryGetLastAnnotation<DeploymentImageTagCallbackAnnotation>(out var annotation))
            {
                var callbackContext = new DeploymentImageTagCallbackAnnotationContext
                {
                    Resource = resource,
                    CancellationToken = context.CancellationToken
                };

                var tag = await annotation.Callback(callbackContext);

                // Only add if tag is not null or empty
                if (!string.IsNullOrEmpty(tag))
                {
                    customTags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    customTags[resource.Name.ToLowerInvariant()] = tag;
                    context.Logger.LogDebug("Using custom image tag '{Tag}' for resource '{Resource}'", tag, resource.Name);
                }
            }
        }

        return customTags;
    }

    private async Task PrepareRemoteEnvironmentStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        // Prepare deployment directory
        context.Logger.LogDebug("Creating deployment directory: {DeployPath}", RemoteDeployPath);
        var createdPath = await RemoteOperationsFactory.DockerEnvironmentService.PrepareDeploymentDirectoryAsync(RemoteDeployPath, context.CancellationToken);
        context.Logger.LogDebug("Directory created: {CreatedPath}", createdPath);

        // Validate Docker environment
        context.Logger.LogDebug("Verifying Docker installation");
        var dockerInfo = await RemoteOperationsFactory.DockerEnvironmentService.ValidateDockerEnvironmentAsync(context.CancellationToken);
        context.Logger.LogDebug("Docker {DockerVersion}, Server {ServerVersion}, Compose {ComposeVersion}",
            dockerInfo.DockerVersion, dockerInfo.ServerVersion, dockerInfo.ComposeVersion);

        // Check deployment state
        context.Logger.LogDebug("Checking permissions and resources");
        var deploymentState = await RemoteOperationsFactory.DockerEnvironmentService.GetDeploymentStateAsync(RemoteDeployPath, context.CancellationToken);

        if (!dockerInfo.HasPermissions)
        {
            throw new InvalidOperationException("User does not have permission to run Docker commands. Add user to 'docker' group and restart the session.");
        }

        context.Logger.LogDebug("Permissions and resources validated. Existing containers: {ExistingContainerCount}",
            deploymentState.ExistingContainerCount);

        await step.SucceedAsync("Remote environment ready for deployment");
    }

    private async Task MergeEnvironmentFileStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;

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

        await using var envFileTask = await step.CreateTaskAsync("Creating and transferring environment file", context.CancellationToken);

        var deploymentResult = await RemoteOperationsFactory.EnvironmentService.DeployEnvironmentAsync(
            envFilePath,
            RemoteDeployPath,
            ImageTags,
            context.CancellationToken);

        context.Logger.LogDebug("Processed {Count} environment variables", deploymentResult.VariableCount);

        await envFileTask.SucceedAsync($"Environment file transferred to {deploymentResult.RemoteEnvPath}", context.CancellationToken);
        await step.SucceedAsync($"Environment configuration finalized with {deploymentResult.VariableCount} variables");
    }

    private async Task TransferDeploymentFilesPipelineStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        const string dockerComposeFile = "docker-compose.yaml";
        var localPath = Path.Combine(OutputPath, dockerComposeFile);

        context.Logger.LogInformation("Scanning files for transfer...");
        if (!File.Exists(localPath))
        {
            throw new InvalidOperationException($"Required file not found: {dockerComposeFile} at {localPath}. Ensure prepare-{DockerComposeEnvironment.Name} step has run.");
        }

        context.Logger.LogDebug("Found {DockerComposeFile}, .env file handled separately", dockerComposeFile);

        await using var copyTask = await step.CreateTaskAsync("Copying and verifying docker-compose.yaml", context.CancellationToken);

        var remotePath = $"{RemoteDeployPath}/{dockerComposeFile}";
        var transferResult = await RemoteOperationsFactory.FileService.TransferWithVerificationAsync(localPath, remotePath, context.CancellationToken);

        if (!transferResult.Success || !transferResult.Verified)
        {
            throw new InvalidOperationException($"File transfer verification failed: {dockerComposeFile}");
        }

        await copyTask.SucceedAsync($"✓ {dockerComposeFile} verified ({transferResult.BytesTransferred} bytes)", context.CancellationToken);
        await step.SucceedAsync("File transfer completed");
    }

    private async Task TransferExtraFilesStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        // Get all file transfer annotations
        var fileTransfers = DockerComposeEnvironment.Annotations.OfType<FileTransferAnnotation>().ToList();

        if (fileTransfers.Count == 0)
        {
            await step.SucceedAsync("No extra files configured for transfer");
            return;
        }

        var totalFilesTransferred = 0;

        foreach (var transfer in fileTransfers)
        {
            // Resolve the remote path from the value provider
            var resolvedPath = await transfer.RemotePath.GetValueAsync(context.CancellationToken);
            if (string.IsNullOrEmpty(resolvedPath))
            {
                context.Logger.LogWarning("Remote path resolved to empty value, skipping transfer for {LocalPath}", transfer.LocalPath);
                continue;
            }

            // If relative to deploy path, prepend RemoteDeployPath
            var remotePath = transfer.IsRelativeToDeployPath
                ? $"{RemoteDeployPath}/{resolvedPath}"
                : resolvedPath;

            // Resolve local path relative to AppHost directory
            var localPath = Path.IsPathRooted(transfer.LocalPath)
                ? transfer.LocalPath
                : Path.Combine(_hostEnvironment.ContentRootPath, transfer.LocalPath);

            if (!Directory.Exists(localPath))
            {
                context.Logger.LogWarning("Local directory not found: {LocalPath}, skipping", localPath);
                continue;
            }

            await using var transferTask = await step.CreateTaskAsync($"Transferring files to {remotePath}", context.CancellationToken);

            // Create remote directory (for absolute paths, this creates via mkdir -p)
            context.Logger.LogDebug("Creating remote directory: {RemotePath}", remotePath);
            await RemoteOperationsFactory.DockerEnvironmentService.PrepareDeploymentDirectoryAsync(remotePath, context.CancellationToken);

            // Transfer all files from the local directory
            var files = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(localPath, file);
                var remoteFilePath = $"{remotePath}/{relativePath.Replace(Path.DirectorySeparatorChar, '/')}";

                // Ensure remote subdirectory exists
                var remoteDir = Path.GetDirectoryName(remoteFilePath)?.Replace(Path.DirectorySeparatorChar, '/');
                if (!string.IsNullOrEmpty(remoteDir) && remoteDir != remotePath)
                {
                    await RemoteOperationsFactory.DockerEnvironmentService.PrepareDeploymentDirectoryAsync(remoteDir, context.CancellationToken);
                }

                var result = await RemoteOperationsFactory.FileService.TransferWithVerificationAsync(file, remoteFilePath, context.CancellationToken);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Failed to transfer file: {relativePath}");
                }

                totalFilesTransferred++;
                context.Logger.LogDebug("Transferred {File} -> {RemotePath}", relativePath, remoteFilePath);
            }

            await transferTask.SucceedAsync($"Transferred {files.Length} file(s) to {remotePath}", context.CancellationToken);
        }

        await step.SucceedAsync($"Extra file transfer completed: {totalFilesTransferred} file(s)");
    }

    private async Task DeployApplicationStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        // Authenticate with registry on remote (for private repos)
        if (!string.IsNullOrEmpty(RegistryConfig.RegistryUsername) &&
            !string.IsNullOrEmpty(RegistryConfig.RegistryPassword))
        {
            await using var loginTask = await step.CreateTaskAsync("Authenticating with registry on remote", context.CancellationToken);
            context.Logger.LogDebug("Logging into {RegistryUrl} on remote server...", RegistryConfig.RegistryUrl);

            var loginResult = await RemoteOperationsFactory.DockerComposeService.LoginToRegistryAsync(
                RegistryConfig.RegistryUrl,
                RegistryConfig.RegistryUsername,
                RegistryConfig.RegistryPassword,
                context.CancellationToken);

            if (!loginResult.Success)
            {
                throw new InvalidOperationException($"Failed to authenticate with {RegistryConfig.RegistryUrl} on remote server: {loginResult.Error}");
            }

            await loginTask.SucceedAsync($"Authenticated with {RegistryConfig.RegistryUrl}", context.CancellationToken);
        }

        // Deploy with minimal downtime using docker compose up --pull always --remove-orphans
        // This pulls images and recreates only changed containers without explicitly stopping first
        await using var deployTask = await step.CreateTaskAsync("Deploying containers", context.CancellationToken);
        context.Logger.LogDebug("Deploying containers with pull...");

        var deployResult = await RemoteOperationsFactory.DockerComposeService.UpWithPullAsync(RemoteDeployPath, context.CancellationToken);

        if (!string.IsNullOrEmpty(deployResult.Output))
        {
            await deployTask.SucceedAsync($"Containers deployed\n{deployResult.Output.Trim()}", context.CancellationToken);
        }
        else
        {
            await deployTask.SucceedAsync("Containers deployed successfully", context.CancellationToken);
        }

        // Optionally prune unused images to prevent disk space accumulation
        if (_pruneImagesAfterDeploy)
        {
            await using var pruneTask = await step.CreateTaskAsync("Cleaning up unused images", context.CancellationToken);
            context.Logger.LogDebug("Pruning unused Docker images...");

            var pruneResult = await RemoteOperationsFactory.DockerComposeService.PruneImagesAsync(context.CancellationToken);

            if (pruneResult.Success && !string.IsNullOrEmpty(pruneResult.Output))
            {
                await pruneTask.SucceedAsync($"Image cleanup completed\n{pruneResult.Output.Trim()}", context.CancellationToken);
            }
            else
            {
                await pruneTask.SucceedAsync("Image cleanup completed", context.CancellationToken);
            }
        }

        await step.SucceedAsync("Deployment completed successfully");
    }

    private async Task HealthCheckStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        // Get target host for URLs (use original configured host, not resolved IP)
        var targetHost = _sshConnectionManager?.TargetHost
            ?? throw new InvalidOperationException("SSH connection not established");

        // Monitor service health (waits for minimum polls to catch early crashes)
        await HealthCheckUtility.CheckServiceHealth(RemoteDeployPath, targetHost, RemoteOperationsFactory.DockerComposeService, context.Logger, context.CancellationToken);

        // Get final deployment status
        var status = await RemoteOperationsFactory.DockerComposeService.GetStatusAsync(RemoteDeployPath, targetHost, context.CancellationToken);

        // Check for failed containers and show their logs
        var failedServices = status.Services.Where(s => s.IsFailed).ToList();
        if (failedServices.Count > 0)
        {
            context.Logger.LogError("{FailedCount} container(s) failed:", failedServices.Count);

            foreach (var failed in failedServices)
            {
                context.Logger.LogError("  - {Service}: {State} (exit code {ExitCode})",
                    failed.Service, failed.State, failed.ExitCode);

                // Get logs for the failed container
                try
                {
                    var logs = await RemoteOperationsFactory.DockerComposeService.GetServiceLogsAsync(
                        failed.Name, 50, context.CancellationToken);

                    if (!string.IsNullOrWhiteSpace(logs))
                    {
                        context.Logger.LogError("Logs for {Service}:\n{Logs}", failed.Service, logs);
                    }
                }
                catch (Exception ex)
                {
                    context.Logger.LogDebug(ex, "Failed to get logs for {Service}", failed.Service);
                }
            }

            throw new InvalidOperationException(
                $"Deployment failed: {failedServices.Count} container(s) exited with errors. " +
                $"Services: {string.Join(", ", failedServices.Select(s => $"{s.Service} (exit {s.ExitCode})"))}");
        }

        // Find dashboard container name (docker logs needs container name, not service name)
        var dashboardService = status.Services.FirstOrDefault(
            s => s.Service.Contains(DockerComposeEnvironment.Name + "-dashboard", StringComparison.OrdinalIgnoreCase));
        _dashboardServiceName = dashboardService?.Name;

        // Format and log the service status table with uptime information
        var serviceUrlsForTable = status.ServiceUrls;

        if (!ServiceUrlFormatter.CanShowTargetHost(_configuration, targetHost))
        {
            context.Logger.LogWarning("Target host is masked for security. Set UNSAFE_SHOW_TARGET_HOST=true to show it.");
            serviceUrlsForTable = ServiceUrlFormatter.MaskUrlHosts(serviceUrlsForTable, customDomain: null);
        }

        var serviceTable = ServiceUrlFormatter.FormatServiceStatusAsTable(status, serviceUrlsForTable);
        context.Logger.LogInformation("Services running: {HealthyServices} of {TotalServices} containers healthy.\n{ServiceTable}",
            status.HealthyServices, status.TotalServices, serviceTable);

        await step.SucceedAsync($"All services healthy: {status.HealthyServices} of {status.TotalServices} containers running.");
    }
    #endregion

    private async Task TeardownEnvironmentStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;
        var interactionService = context.Services.GetRequiredService<IInteractionService>();

        // Get target host for display
        var targetHost = _sshConnectionManager?.TargetHost ?? "unknown";

        // Get current deployment status
        await using var statusTask = await step.CreateTaskAsync("Checking deployment status", context.CancellationToken);
        var status = await RemoteOperationsFactory.DockerComposeService.GetStatusAsync(RemoteDeployPath, targetHost, context.CancellationToken);

        if (status.TotalServices == 0)
        {
            await statusTask.SucceedAsync("No containers found", context.CancellationToken);
            await step.SucceedAsync($"No deployment found at {RemoteDeployPath} on {targetHost}");

            // Cleanup SSH connection
            if (_sshConnectionManager != null)
            {
                await _sshConnectionManager.DisconnectAsync();
            }
            return;
        }

        await statusTask.SucceedAsync($"Found {status.TotalServices} container(s)", context.CancellationToken);

        // Build service list for display
        var serviceList = status.Services
            .Select(s => $"  - {s.Service}: {s.State} ({s.Status})")
            .ToList();

        var message = $"The following containers are running at {RemoteDeployPath} on {targetHost}:\n\n{string.Join("\n", serviceList)}\n\nThis will stop and remove all containers. Are you sure you want to proceed?";

        // Confirm with user
        var confirmResult = await interactionService.PromptNotificationAsync(
            "Confirm Teardown",
            message,
            new NotificationInteractionOptions
            {
                Intent = MessageIntent.Confirmation,
                ShowSecondaryButton = true,
                ShowDismiss = false,
                PrimaryButtonText = "Yes, tear down",
                SecondaryButtonText = "Cancel"
            },
            context.CancellationToken);

        if (confirmResult.Canceled || !confirmResult.Data)
        {
            // Cleanup SSH connection
            if (_sshConnectionManager != null)
            {
                await _sshConnectionManager.DisconnectAsync();
            }

            throw new OperationCanceledException("Teardown canceled by user");
        }

        // Stop and remove all containers
        await using var stopTask = await step.CreateTaskAsync("Stopping and removing containers", context.CancellationToken);
        var stopResult = await RemoteOperationsFactory.DockerComposeService.StopAsync(RemoteDeployPath, context.CancellationToken);

        if (!stopResult.Success)
        {
            throw new InvalidOperationException($"Failed to stop containers: {stopResult.Error}");
        }

        if (!string.IsNullOrEmpty(stopResult.Output))
        {
            await stopTask.SucceedAsync($"Containers stopped\n{stopResult.Output.Trim()}", context.CancellationToken);
        }
        else
        {
            await stopTask.SucceedAsync("Containers stopped", context.CancellationToken);
        }

        // Cleanup SSH connection
        if (_sshConnectionManager != null)
        {
            await _sshConnectionManager.DisconnectAsync();
        }

        await step.SucceedAsync($"Environment '{DockerComposeEnvironment.Name}' torn down successfully");
    }

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

    private async Task GenerateGitHubActionsWorkflowStep(PipelineStepContext context)
    {
        var step = context.ReportingStep;
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        var parameterProcessor = context.Services.GetRequiredService<ParameterProcessor>();
        var ct = context.CancellationToken;

        // 1. Check prerequisites and get repo root
        await using var prereqTask = await step.CreateTaskAsync("Checking prerequisites", ct);
        var repoRoot = await _gitHubActionsGeneratorService.CheckGitHubCliAsync(ct);
        await prereqTask.SucceedAsync("GitHub CLI is available and authenticated", ct);

        // The environment name comes from the hosting environment (e.g., "Production")
        var environmentName = _hostEnvironment.EnvironmentName;

        // 2. Query existing state from GitHub FIRST (before prompting for parameters)
        await using var queryTask = await step.CreateTaskAsync("Checking existing GitHub configuration", ct);
        var existingSecrets = await _gitHubActionsGeneratorService.GetEnvironmentSecretsAsync(environmentName, ct);
        var existingVariables = await _gitHubActionsGeneratorService.GetEnvironmentVariablesAsync(environmentName, ct);
        await queryTask.SucceedAsync($"Found {existingSecrets.Count} secrets, {existingVariables.Count} variables", ct);

        // Build parameter infos from the model (without resolving values yet)
        // Use ParameterCollector to find all referenced parameters, including those in env vars and args
        var executionContext = context.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var parameters = await ParameterCollector.CollectAllReferencedParametersAsync(
            context.Model, executionContext, _logger, ct);
        var parameterInfos = parameters.Select(p =>
        {
            var info = new ParameterInfo(p, null, ExistsInGitHub: false);
            var existsInGitHub = p.Secret
                ? existingSecrets.Contains(info.GitHubName)
                : existingVariables.Contains(info.GitHubName);
            return info with { ExistsInGitHub = existsInGitHub };
        }).ToList();

        // Only initialize parameters that don't already exist in GitHub
        var missingParameters = parameterInfos.Where(p => !p.ExistsInGitHub).ToList();
        if (missingParameters.Count > 0)
        {
            await using var paramTask = await step.CreateTaskAsync("Collecting parameter values", ct);
            await parameterProcessor.InitializeParametersAsync(context.Model, waitForResolution: true, ct);
            await paramTask.SucceedAsync($"Collected {missingParameters.Count} parameter value(s)", ct);

            // Now get the resolved values for missing parameters
            for (var i = 0; i < parameterInfos.Count; i++)
            {
                var info = parameterInfos[i];
                if (!info.ExistsInGitHub)
                {
                    var value = await info.Parameter.GetValueAsync(ct);
                    parameterInfos[i] = info with { Value = value };
                }
            }
        }

        // 3. Detect or prompt for SSH authentication method
        SshAuthType sshAuthType;
        var detectedAuthType = DetectSshAuthType(existingSecrets);

        if (detectedAuthType.HasValue)
        {
            // Auth type already configured - use existing
            sshAuthType = detectedAuthType.Value;
            var authLabel = sshAuthType switch
            {
                SshAuthType.Key => "SSH Key (no passphrase)",
                SshAuthType.KeyWithPassphrase => "SSH Key (with passphrase)",
                SshAuthType.Password => "Password",
                _ => "Unknown"
            };
            _logger.LogInformation("Detected existing SSH auth type: {AuthType}", authLabel);
        }
        else
        {
            // No auth configured - prompt user
            await using var authTask = await step.CreateTaskAsync("Selecting SSH authentication method", ct);
            var authOptions = new List<KeyValuePair<string, string>>
            {
                new("key", "SSH Key (no passphrase)"),
                new("key-passphrase", "SSH Key (with passphrase)"),
                new("password", "Password")
            };

            var authMethodResult = await interactionService.PromptInputsAsync(
                "SSH Authentication Method",
                "How do you want to authenticate to the remote server?",
                [
                    new InteractionInput
                    {
                        Name = "authMethod",
                        Label = "Authentication Method",
                        InputType = InputType.Choice,
                        Required = true,
                        Options = authOptions
                    }
                ],
                cancellationToken: ct);

            if (authMethodResult.Canceled)
            {
                throw new OperationCanceledException("Configuration canceled");
            }

            var authMethodValue = authMethodResult.Data["authMethod"].Value;
            sshAuthType = authMethodValue switch
            {
                "key" => SshAuthType.Key,
                "key-passphrase" => SshAuthType.KeyWithPassphrase,
                "password" => SshAuthType.Password,
                _ => SshAuthType.Key
            };
            await authTask.SucceedAsync($"Selected: {authOptions.First(o => o.Key == authMethodValue).Value}", ct);
        }

        // 4. Determine required and orphaned values
        var requiredInfraSecrets = GetRequiredInfraSecrets(sshAuthType);
        var requiredInfraVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TARGET_HOST" };

        var requiredSecrets = requiredInfraSecrets
            .Union(parameterInfos.Where(p => p.IsSecret).Select(p => p.GitHubName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requiredVariables = requiredInfraVariables
            .Union(parameterInfos.Where(p => !p.IsSecret).Select(p => p.GitHubName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Find orphaned values (existing but no longer needed)
        var orphanedSecrets = existingSecrets
            .Where(s => AllPossibleInfraSecrets.Contains(s) || s.StartsWith("PARAMETERS_", StringComparison.OrdinalIgnoreCase) || s.StartsWith("CONNECTIONSTRINGS_", StringComparison.OrdinalIgnoreCase))
            .Where(s => !requiredSecrets.Contains(s))
            .ToList();

        var orphanedVariables = existingVariables
            .Where(v => v.Equals("TARGET_HOST", StringComparison.OrdinalIgnoreCase) || v.StartsWith("PARAMETERS_", StringComparison.OrdinalIgnoreCase) || v.StartsWith("CONNECTIONSTRINGS_", StringComparison.OrdinalIgnoreCase))
            .Where(v => !requiredVariables.Contains(v))
            .ToList();

        // 5. Determine what values need to be collected
        var missingInfraSecrets = requiredInfraSecrets.Where(s => !existingSecrets.Contains(s)).ToList();
        var missingInfraVariables = requiredInfraVariables.Where(v => !existingVariables.Contains(v)).ToList();
        // Get actual names from GitHub (preserves casing/format from GitHub)
        var existingRequiredInfraSecrets = existingSecrets.Where(s => requiredInfraSecrets.Contains(s)).ToList();
        var existingRequiredInfraVariables = existingVariables.Where(v => requiredInfraVariables.Contains(v)).ToList();

        // Ask if user wants to overwrite existing values
        var overwriteExisting = false;
        if (existingRequiredInfraSecrets.Count > 0 || existingRequiredInfraVariables.Count > 0)
        {
            var existingList = existingRequiredInfraSecrets.Select(s => $"  - Secret: {s}")
                .Concat(existingRequiredInfraVariables.Select(v => $"  - Variable: {v}"))
                .ToList();

            var overwriteResult = await interactionService.PromptNotificationAsync(
                "Existing GitHub Values",
                $"The following values already exist in environment '{environmentName}':\n{string.Join("\n", existingList)}\n\nDo you want to overwrite them?",
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Confirmation,
                    ShowSecondaryButton = true,
                    ShowDismiss = false,
                    PrimaryButtonText = "Yes",
                    SecondaryButtonText = "No"
                },
                ct);

            if (overwriteResult.Canceled)
            {
                throw new OperationCanceledException("Configuration canceled");
            }

            overwriteExisting = overwriteResult.Data;
        }

        var valuesToPrompt = overwriteExisting
            ? requiredInfraSecrets.Union(requiredInfraVariables).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : missingInfraSecrets.Union(missingInfraVariables).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 6. Prompt for infrastructure values
        await using var infraTask = await step.CreateTaskAsync("Configuring infrastructure", ct);

        Dictionary<string, string> collectedValues = [];

        if (valuesToPrompt.Count > 0)
        {
            var infraInputs = new List<InteractionInput>();

            if (valuesToPrompt.Contains("TARGET_HOST"))
            {
                infraInputs.Add(new InteractionInput { Name = "targetHost", Label = "Target Host", InputType = InputType.Text, Required = true });
            }

            if (valuesToPrompt.Contains("SSH_USERNAME"))
            {
                infraInputs.Add(new InteractionInput { Name = "sshUsername", Label = "SSH Username", InputType = InputType.Text, Required = true, Value = "root" });
            }

            if (valuesToPrompt.Contains("SSH_PRIVATE_KEY"))
            {
                // Discover available SSH keys
                var discoveredKeys = _sshKeyDiscoveryService.DiscoverKeys();

                if (discoveredKeys.Count > 0)
                {
                    var keyOptions = discoveredKeys
                        .Select(k => new KeyValuePair<string, string>(k.FullPath, k.KeyType != null ? $"{k.DisplayPath} ({k.KeyType})" : k.DisplayPath))
                        .ToList();

                    infraInputs.Add(new InteractionInput
                    {
                        Name = "sshPrivateKeyPath",
                        Label = "SSH Private Key",
                        InputType = InputType.Choice,
                        Required = true,
                        Options = keyOptions,
                        AllowCustomChoice = true
                    });
                }
                else
                {
                    // No keys found, fall back to text input
                    infraInputs.Add(new InteractionInput { Name = "sshPrivateKeyPath", Label = "SSH Private Key Path", InputType = InputType.Text, Required = true, Value = "~/.ssh/id_rsa" });
                }
            }

            if (valuesToPrompt.Contains("SSH_KEY_PASSPHRASE"))
            {
                infraInputs.Add(new InteractionInput { Name = "sshKeyPassphrase", Label = "SSH Key Passphrase", InputType = InputType.SecretText, Required = true });
            }

            if (valuesToPrompt.Contains("SSH_PASSWORD"))
            {
                infraInputs.Add(new InteractionInput { Name = "sshPassword", Label = "SSH Password", InputType = InputType.SecretText, Required = true });
            }

            var infraResult = await interactionService.PromptInputsAsync(
                "GitHub Actions - Infrastructure",
                "Provide SSH deployment configuration. These will be stored as GitHub environment secrets/variables.",
                infraInputs,
                cancellationToken: ct);

            if (infraResult.Canceled)
            {
                throw new OperationCanceledException("Configuration canceled");
            }

            // Map the results (only for values we prompted for)
            if (valuesToPrompt.Contains("TARGET_HOST") && !string.IsNullOrEmpty(infraResult.Data["targetHost"].Value))
            {
                collectedValues["TARGET_HOST"] = infraResult.Data["targetHost"].Value!;
            }

            if (valuesToPrompt.Contains("SSH_USERNAME") && !string.IsNullOrEmpty(infraResult.Data["sshUsername"].Value))
            {
                collectedValues["SSH_USERNAME"] = infraResult.Data["sshUsername"].Value!;
            }

            if (valuesToPrompt.Contains("SSH_PRIVATE_KEY") && !string.IsNullOrEmpty(infraResult.Data["sshPrivateKeyPath"].Value))
            {
                var keyPath = infraResult.Data["sshPrivateKeyPath"].Value!;
                collectedValues["SSH_PRIVATE_KEY"] = await _sshKeyDiscoveryService.ReadKeyAsync(keyPath, ct);
            }

            if (valuesToPrompt.Contains("SSH_KEY_PASSPHRASE") && !string.IsNullOrEmpty(infraResult.Data["sshKeyPassphrase"].Value))
            {
                collectedValues["SSH_KEY_PASSPHRASE"] = infraResult.Data["sshKeyPassphrase"].Value!;
            }

            if (valuesToPrompt.Contains("SSH_PASSWORD") && !string.IsNullOrEmpty(infraResult.Data["sshPassword"].Value))
            {
                collectedValues["SSH_PASSWORD"] = infraResult.Data["sshPassword"].Value!;
            }

            await infraTask.SucceedAsync($"Collected {collectedValues.Count} infrastructure value(s)", ct);
        }
        else
        {
            await infraTask.SucceedAsync("All infrastructure values already configured", ct);
        }

        // 7. Handle orphaned values
        if (orphanedSecrets.Count > 0 || orphanedVariables.Count > 0)
        {
            var orphanList = orphanedSecrets.Select(s => $"  - Secret: {s}")
                .Concat(orphanedVariables.Select(v => $"  - Variable: {v}"))
                .ToList();

            var deleteResult = await interactionService.PromptNotificationAsync(
                "Orphaned GitHub Values",
                $"The following secrets/variables are no longer needed:\n{string.Join("\n", orphanList)}\n\nDelete them?",
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Confirmation,
                    ShowSecondaryButton = true,
                    ShowDismiss = false,
                    PrimaryButtonText = "Yes",
                    SecondaryButtonText = "No"
                },
                ct);

            if (!deleteResult.Canceled && deleteResult.Data)
            {
                await using var deleteTask = await step.CreateTaskAsync("Deleting orphaned values", ct);
                foreach (var secret in orphanedSecrets)
                {
                    await _gitHubActionsGeneratorService.DeleteEnvironmentSecretAsync(environmentName, secret, ct);
                }

                foreach (var variable in orphanedVariables)
                {
                    await _gitHubActionsGeneratorService.DeleteEnvironmentVariableAsync(environmentName, variable, ct);
                }

                await deleteTask.SucceedAsync($"Deleted {orphanedSecrets.Count + orphanedVariables.Count} orphaned value(s)", ct);
            }
        }

        // 8. Create/update GitHub environment and set values
        await using var ghTask = await step.CreateTaskAsync($"Configuring GitHub environment '{environmentName}'", ct);

        // Create the environment first (idempotent)
        await _gitHubActionsGeneratorService.CreateEnvironmentAsync(environmentName, ct);

        // Set only the values that were collected (all infrastructure values are secrets)
        foreach (var (name, value) in collectedValues)
        {
            await _gitHubActionsGeneratorService.SetEnvironmentValueAsync(environmentName, name, value, isSecret: true, ct);
        }

        // Set parameter values in GitHub (only for parameters that don't already exist)
        foreach (var info in parameterInfos)
        {
            if (!info.ExistsInGitHub && !string.IsNullOrEmpty(info.Value))
            {
                await _gitHubActionsGeneratorService.SetEnvironmentValueAsync(environmentName, info.GitHubName, info.Value, info.IsSecret, ct);
            }
        }

        await ghTask.SucceedAsync($"GitHub environment '{environmentName}' configured", ct);

        // 9. Generate workflow YAML (with overwrite confirmation)
        var workflowDir = Path.Combine(repoRoot, ".github", "workflows");
        Directory.CreateDirectory(workflowDir);

        var outputPath = Path.Combine(workflowDir, $"deploy-{environmentName}.yml");

        if (File.Exists(outputPath))
        {
            var overwriteFileResult = await interactionService.PromptNotificationAsync(
                "Workflow File Exists",
                $"The workflow file already exists at:\n{outputPath}\n\nOverwrite it?",
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Confirmation,
                    ShowSecondaryButton = true,
                    ShowDismiss = false,
                    PrimaryButtonText = "Yes",
                    SecondaryButtonText = "No"
                },
                ct);

            if (overwriteFileResult.Canceled || !overwriteFileResult.Data)
            {
                await step.SucceedAsync($"Workflow generation skipped (file exists at {outputPath})");
                return;
            }
        }

        var appHostPath = Path.GetRelativePath(repoRoot, _hostEnvironment.ContentRootPath);
        var options = new WorkflowGenerationOptions(environmentName, "10.0.x", appHostPath, parameterInfos, sshAuthType);

        var content = _gitHubActionsGeneratorService.GenerateStandaloneWorkflow(options);
        await File.WriteAllTextAsync(outputPath, content, ct);

        await step.SucceedAsync($"Generated workflow at {outputPath}");
    }

    // Infrastructure secrets required for each auth type
    private static HashSet<string> GetRequiredInfraSecrets(SshAuthType authType) => authType switch
    {
        SshAuthType.Key => new(["SSH_USERNAME", "SSH_PRIVATE_KEY"], StringComparer.OrdinalIgnoreCase),
        SshAuthType.KeyWithPassphrase => new(["SSH_USERNAME", "SSH_PRIVATE_KEY", "SSH_KEY_PASSPHRASE"], StringComparer.OrdinalIgnoreCase),
        SshAuthType.Password => new(["SSH_USERNAME", "SSH_PASSWORD"], StringComparer.OrdinalIgnoreCase),
        _ => new(StringComparer.OrdinalIgnoreCase)
    };

    // All possible infrastructure secrets (for orphan detection)
    private static readonly HashSet<string> AllPossibleInfraSecrets =
        new(["SSH_USERNAME", "SSH_PRIVATE_KEY", "SSH_KEY_PASSPHRASE", "SSH_PASSWORD"], StringComparer.OrdinalIgnoreCase);

    // Detect SSH auth type from existing secrets
    private static SshAuthType? DetectSshAuthType(HashSet<string> existingSecrets)
    {
        var hasPrivateKey = existingSecrets.Contains("SSH_PRIVATE_KEY");
        var hasPassphrase = existingSecrets.Contains("SSH_KEY_PASSPHRASE");
        var hasPassword = existingSecrets.Contains("SSH_PASSWORD");

        if (hasPrivateKey && hasPassphrase)
        {
            return SshAuthType.KeyWithPassphrase;
        }

        if (hasPrivateKey)
        {
            return SshAuthType.Key;
        }

        if (hasPassword)
        {
            return SshAuthType.Password;
        }

        return null;
    }

    private async Task CheckPrerequisitesConcurrently(PipelineStepContext context)
    {
        var step = context.ReportingStep;

        // Log local environment info for diagnostics
        await LogLocalEnvironmentAsync(step, context.CancellationToken);

        // Create all prerequisite check tasks
        var dockerTask = _dockerCommandExecutor.CheckDockerAvailability(step, context.CancellationToken);
        var dockerComposeTask = _dockerCommandExecutor.CheckDockerCompose(step, context.CancellationToken);

        // Run all prerequisite checks concurrently
        await Task.WhenAll(dockerTask, dockerComposeTask);
        await step.SucceedAsync("All prerequisites verified successfully");
    }

    private async Task LogLocalEnvironmentAsync(IReportingStep step, CancellationToken cancellationToken)
    {
        await using var task = await step.CreateTaskAsync("Checking local environment", cancellationToken);

        // Local OS
        var localOs = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        _logger.LogInformation("Local OS: {LocalOs}", localOs);

        // Local SSH version
        var sshResult = await _processExecutor.ExecuteAsync("ssh", "-V", cancellationToken: cancellationToken);
        // ssh -V writes to stderr
        var localSsh = !string.IsNullOrEmpty(sshResult.Error) ? sshResult.Error.Trim() : sshResult.Output.Trim();
        _logger.LogInformation("Local SSH: {LocalSsh}", localSsh);

        await task.SucceedAsync($"Local: {localOs}, SSH: {localSsh}", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sshConnectionManager != null)
        {
            await _sshConnectionManager.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }
}
