#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting;
using Aspire.Hosting.Docker.Pipelines.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Aspire.Hosting.Docker.Pipelines.Models;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Docker;

internal class DockerSSHPipeline(DockerComposeEnvironmentResource dockerComposeEnvironmentResource) : IAsyncDisposable
{
    private SshClient? _sshClient = null;
    private ScpClient? _scpClient = null;

    // Deployment state keys
    private const string SshContextKey = "DockerSSH";
    private const string RegistryContextKey = "DockerRegistry";

    // Local state storage since IDeploymentStateManager does not expose generic Set/TryGet APIs here
    private SSHConnectionContext? _sshContext;
    private Dictionary<string, string>? _imageTags;
    private RegistryConfiguration? _registryConfig;

    public DockerComposeEnvironmentResource DockerComposeEnvironment { get; } = dockerComposeEnvironmentResource;

    private string? _outputPath;

    private string? _dashboardServiceName;

    public string OutputPath => _outputPath ?? throw new InvalidOperationException("OutputPath is not set. Ensure the pipeline step to prepare the temporary directory has run.");

    public IEnumerable<PipelineStep> CreateSteps(PipelineStepFactoryContext context)
    {
#pragma warning disable ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        _outputPath = context.PipelineContext.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory();
#pragma warning restore ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

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
        await EstablishAndTestSSHConnection(context, sshContext.TargetHost, sshContext.SshUsername, sshContext.SshPassword, sshContext.SshKeyPath, sshContext.SshPort, step, context.CancellationToken);
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
        await CleanupSSHConnection(context);
        context.Logger.LogDebug("SSH connection cleanup completed");
    }
    #endregion

    #region Pipeline Step Implementations
    private async Task PrepareSSHContextStep(PipelineStepContext context)
    {
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var configDefaults = ConfigurationUtility.GetConfigurationDefaults(configuration);

        _sshContext = await PrepareSSHConnectionContext(context, configDefaults, interactionService);
    }

    private async Task ConfigureRegistryStep(PipelineStepContext context)
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
            _registryConfig = new RegistryConfiguration(registryUrl, repositoryPrefix, registryUsername, registryPassword);
            return;
        }
        else
        {
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
        }

        if (!string.IsNullOrEmpty(registryUsername) && !string.IsNullOrEmpty(registryPassword))
        {
            await using var loginTask = await step.CreateTaskAsync("Authenticating", context.CancellationToken);
            var loginResult = await DockerCommandUtility.ExecuteDockerLogin(registryUrl, registryUsername, registryPassword, context.CancellationToken);
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

        _registryConfig = new RegistryConfiguration(registryUrl, repositoryPrefix, registryUsername, registryPassword);

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

    private async Task PushImagesStep(PipelineStepContext context)
    {
        if (_registryConfig is null)
        {
            throw new InvalidOperationException("Registry not configured before pushing images");
        }
        _imageTags = await PushContainerImagesToRegistry(context, _registryConfig, context.CancellationToken);
    }

    private async Task EstablishSSHConnectionStep(PipelineStepContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for establishing connection");
        }
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
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for preparing remote environment");
        }
        await PrepareRemoteEnvironmentStepAsync(sshContext, context);
    }

    private async Task MergeEnvironmentFileStep(PipelineStepContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for environment merge");
        }
        var imageTags = _imageTags;
        if (imageTags is null)
        {
            throw new InvalidOperationException("Image tags not available for environment merge");
        }
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        await MergeAndUpdateEnvironmentFile(sshContext.RemoteDeployPath, imageTags, context, interactionService, context.CancellationToken);
    }

    private async Task TransferDeploymentFilesPipelineStep(PipelineStepContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for file transfer");
        }
        await TransferDeploymentFilesStepAsync(sshContext, context);
    }

    private async Task DeployApplicationStep(PipelineStepContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for deployment");
        }
        var imageTags = _imageTags;
        if (imageTags is null)
        {
            throw new InvalidOperationException("Image tags not available for deployment");
        }
        await DeployOnRemoteServerStepAsync(sshContext, imageTags, context);
    }
    #endregion

    private async Task ExtractDashboardLoginTokenStep(PipelineStepContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for dashboard token extraction");
        }

        var step = context.ReportingStep;

        // We'll attempt to locate the dashboard service logs (service name convention: <composeName>-dashboard)
        var serviceName = _dashboardServiceName ?? throw new InvalidOperationException("Dashboard service name not identified during deployment");

        // Poll up to 10 seconds (e.g. 5 attempts @ 2s) since the token may appear shortly after container start
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? token = null;
        // Simplified regex: we're already filtering to lines that contain the phrase, so just capture after ?t=
        var urlPattern = @"\?t=(?<tok>[A-Za-z0-9\-_.:]+)";

        int attempt = 0;
        while (DateTime.UtcNow < deadline && token is null && !context.CancellationToken.IsCancellationRequested)
        {
            attempt++;
            await using var attemptTask = await step.CreateTaskAsync($"Log scan attempt {attempt}", context.CancellationToken);

            // Use docker logs directly (serviceName is the container name). Tail a limited number of recent lines.
            var logCommand = $"docker logs --tail 50 {serviceName}";
            var result = await ExecuteSSHCommandWithOutput(context, logCommand, context.CancellationToken);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            {
                context.Logger.LogDebug("No logs yet or command failed; will retry");
            }
            else
            {
                // Look at individual lines to find the phrase and extract token
                var lines = result.Output.Split('\n');
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line.IndexOf("Login to the dashboard at", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Attempt regex on this specific line
                        var lineMatch = System.Text.RegularExpressions.Regex.Match(line, urlPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (lineMatch.Success && lineMatch.Groups["tok"].Success)
                        {
                            token = lineMatch.Groups["tok"].Value.Trim();
                            break;
                        }
                        // Fallback: if line contains '?t=' extract substring after '?t='
                        var idx = line.IndexOf("?t=", StringComparison.OrdinalIgnoreCase);
                        if (token is null && idx >= 0)
                        {
                            var candidate = line[(idx + 3)..];
                            // Trim trailing punctuation/spaces
                            candidate = new string(candidate.TakeWhile(c => !char.IsWhiteSpace(c) && c != '\r').ToArray());
                            if (candidate.Length > 0)
                            {
                                token = candidate;
                                break;
                            }
                        }
                    }
                }

                if (token is not null)
                {
                    await attemptTask.SucceedAsync("Token found", context.CancellationToken);
                    break;
                }
                context.Logger.LogDebug("Token not found in current log snapshot");
            }

            if (token is null)
            {
                // Delay before next attempt
                try { await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken); } catch { break; }
                await attemptTask.SucceedAsync("Retrying", context.CancellationToken);
            }
        }

        if (token is null)
        {
            await step.WarnAsync("Dashboard login token not detected within 20s polling window.", context.CancellationToken);
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
        var dockerTask = DockerCommandUtility.CheckDockerAvailability(step, context.CancellationToken);
        var dockerComposeTask = DockerCommandUtility.CheckDockerCompose(step, context.CancellationToken);

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
        var sshConfig = await SSHConfigurationDiscovery.DiscoverSSHConfiguration(context);

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
        await using var createDirTask = await step.CreateTaskAsync("Creating deployment directory", cancellationToken);

        // Create deployment directory
        await ExecuteSSHCommand(context, $"mkdir -p {deployPath}", cancellationToken);

        await createDirTask.SucceedAsync($"Directory created: {deployPath}", cancellationToken: cancellationToken);

        await using var dockerCheckTask = await step.CreateTaskAsync("Verifying Docker installation", cancellationToken);

        // Check if Docker is installed and get version info
        var dockerVersionCheck = await ExecuteSSHCommandWithOutput(context, "docker --version", cancellationToken);
        if (dockerVersionCheck.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker is not installed on the target server. Error: {dockerVersionCheck.Error}");
        }

        context.Logger.LogDebug("Verifying Docker daemon status...");

        // Check if Docker daemon is running
        var dockerInfoCheck = await ExecuteSSHCommandWithOutput(context, "docker info --format '{{.ServerVersion}}' 2>/dev/null", cancellationToken);
        if (dockerInfoCheck.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker daemon is not running on the target server. Error: {dockerInfoCheck.Error}");
        }

        context.Logger.LogDebug("Checking Docker Compose availability...");

        // Check if Docker Compose is available
        var composeCheck = await ExecuteSSHCommandWithOutput(context, "docker compose version", cancellationToken);
        if (composeCheck.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker Compose is not available on the target server. " +
                $"Command 'docker compose version' failed with exit code {composeCheck.ExitCode}. " +
                $"Error: {composeCheck.Error}");
        }

        await dockerCheckTask.SucceedAsync("Docker and Docker Compose verified", cancellationToken: cancellationToken);

        await using var permissionsTask = await step.CreateTaskAsync("Checking permissions and resources", cancellationToken);

        // Check if user can run Docker commands without sudo
        var dockerPermCheck = await ExecuteSSHCommandWithOutput(context, "docker ps > /dev/null 2>&1 && echo 'OK' || echo 'SUDO_REQUIRED'", cancellationToken);
        if (dockerPermCheck.Output.Trim() == "SUDO_REQUIRED")
        {
            throw new InvalidOperationException($"User does not have permission to run Docker commands. Add user to 'docker' group and restart the session.");
        }

        // Check if there are any existing containers that might conflict
        var existingContainersCheck = await ExecuteSSHCommandWithOutput(context, $"cd {deployPath} 2>/dev/null && (docker compose ps -q 2>/dev/null || docker-compose ps -q 2>/dev/null) | wc -l || echo '0'", cancellationToken);
        var existingContainers = existingContainersCheck.Output?.Trim() ?? "0";

        await permissionsTask.SucceedAsync($"Permissions and resources validated. Existing containers: {existingContainers}", cancellationToken: cancellationToken);
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

        await using var copyTask = await step.CreateTaskAsync("Copying docker-compose.yaml to remote server", cancellationToken);

        var remotePath = $"{deployPath}/{dockerComposeFile}";
        await TransferFile(context, localPath, remotePath, cancellationToken);

        await copyTask.SucceedAsync($"{dockerComposeFile} transferred successfully", cancellationToken: cancellationToken);

        await using var verifyTask = await step.CreateTaskAsync("Verifying file on remote server", cancellationToken);

        // Check if file exists and get its size
        var verifyResult = await ExecuteSSHCommandWithOutput(context, $"ls -la '{remotePath}' 2>/dev/null || echo 'FILE_NOT_FOUND'", cancellationToken);

        if (verifyResult.ExitCode != 0 || verifyResult.Output.Contains("FILE_NOT_FOUND"))
        {
            throw new InvalidOperationException($"File transfer verification failed: {dockerComposeFile} not found on remote server");
        }

        // Get local file size for comparison
        var localFileInfo = new FileInfo(localPath);

        // Extract remote file size from ls output (5th column in ls -la output)
        var lsOutput = verifyResult.Output.Trim();
        var parts = lsOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 5 && long.TryParse(parts[4], out var remoteSize))
        {
            if (localFileInfo.Length != remoteSize)
            {
                throw new InvalidOperationException($"File transfer verification failed: Size mismatch for {dockerComposeFile}");
            }

            await verifyTask.SucceedAsync($"✓ {dockerComposeFile} verified ({localFileInfo.Length} bytes)", cancellationToken: cancellationToken);
        }
        else
        {
            // Fallback: just check file exists with a simpler test
            var existsResult = await ExecuteSSHCommandWithOutput(context, $"test -f '{remotePath}' && echo 'EXISTS' || echo 'NOT_FOUND'", cancellationToken);
            if (existsResult.Output.Trim() == "EXISTS")
            {
                await verifyTask.SucceedAsync($"✓ {dockerComposeFile} verified", cancellationToken: cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"File transfer verification failed: {dockerComposeFile} not found on remote server");
            }
        }
    }

    private async Task<string> DeployOnRemoteServer(PipelineStepContext context, string deployPath, Dictionary<string, string> imageTags, IReportingStep step, CancellationToken cancellationToken)
    {
        await using var stopTask = await step.CreateTaskAsync("Stopping existing containers", cancellationToken);

        // Check if any containers are currently running in this deployment
        var existingCheck = await ExecuteSSHCommandWithOutput(context,
            $"cd {deployPath} && (docker compose ps -q || docker-compose ps -q || true) 2>/dev/null | wc -l", cancellationToken);

        // Stop existing containers if any
        var stopResult = await ExecuteSSHCommandWithOutput(context,
            $"cd {deployPath} && (docker compose down || docker-compose down || true)", cancellationToken);

        if (stopResult.ExitCode == 0 && !string.IsNullOrEmpty(stopResult.Output))
        {
            await stopTask.SucceedAsync($"Existing containers stopped\nCommand: cd {deployPath} && (docker compose down || docker-compose down || true)\nOutput: {stopResult.Output.Trim()}", cancellationToken: cancellationToken);
        }
        else
        {
            await stopTask.SucceedAsync("No containers to stop or stop completed", cancellationToken: cancellationToken);
        }

        // Note: Image configuration is now handled through environment variables in MergeAndUpdateEnvironmentFile

        await using var pullTask = await step.CreateTaskAsync("Pulling latest images", cancellationToken);

        context.Logger.LogDebug("Pulling latest container images...");

        // Pull latest images (if using registry) - non-fatal if fails
        var pullResult = await ExecuteSSHCommandWithOutput(context,
            $"cd {deployPath} && (docker compose pull || docker-compose pull || true)", cancellationToken);

        if (!string.IsNullOrEmpty(pullResult.Output))
        {
            await pullTask.SucceedAsync($"Latest images pulled\nCommand: cd {deployPath} && (docker compose pull || docker-compose pull || true)\nOutput: {pullResult.Output.Trim()}", cancellationToken: cancellationToken);
        }
        else
        {
            await pullTask.SucceedAsync("Image pull completed (no output or using local images)", cancellationToken: cancellationToken);
        }

        await using var startTask = await step.CreateTaskAsync("Starting new containers", cancellationToken);

        context.Logger.LogDebug("Starting application containers...");

        // Start services
        var startResult = await ExecuteSSHCommandWithOutput(context,
            $"cd {deployPath} && (docker compose up -d || docker-compose up -d)", cancellationToken);

        if (startResult.ExitCode != 0)
        {
            // Try to get more detailed error information
            var logsResult = await ExecuteSSHCommandWithOutput(context,
                $"cd {deployPath} && (docker compose logs --tail=50 || docker-compose logs --tail=50 || true)", cancellationToken);

            var errorDetails = string.IsNullOrEmpty(logsResult.Output) ? startResult.Error : logsResult.Output;
            throw new InvalidOperationException($"Failed to start containers: {startResult.Error}\n\nContainer logs:\n{errorDetails}");
        }

        await startTask.SucceedAsync("New containers started", cancellationToken: cancellationToken);

        // Use the new HealthCheckUtility to check each service individually
        await HealthCheckUtility.CheckServiceHealth(deployPath, _sshClient!, step, cancellationToken);

        // Get final service status for summary
        var finalServiceStatuses = await HealthCheckUtility.GetServiceStatuses(deployPath, _sshClient!, cancellationToken);
        var healthyServices = finalServiceStatuses.Count(s => s.IsHealthy);

        // Try to extract port information
        var serviceUrls = await PortInformationUtility.ExtractPortInformation(deployPath, _sshClient!, cancellationToken);

        _dashboardServiceName = serviceUrls.FirstOrDefault(s => s.Key.Contains(DockerComposeEnvironment.Name + "-dashboard", StringComparison.OrdinalIgnoreCase)).Key;

        // Format port information as a nice table
        var serviceTable = PortInformationUtility.FormatServiceUrlsAsTable(serviceUrls);

        return $"Services running: {healthyServices} of {finalServiceStatuses.Count} containers healthy.\n{serviceTable}";
    }

    private async Task TransferFile(PipelineStepContext context, string localPath, string remotePath, CancellationToken cancellationToken)
    {
        context.Logger.LogDebug("Transferring file {LocalPath} to {RemotePath}", localPath, remotePath);

        try
        {
            if (_scpClient == null || !_scpClient.IsConnected)
            {
                throw new InvalidOperationException("SCP connection not established");
            }

            using var fileStream = File.OpenRead(localPath);
            await Task.Run(() => _scpClient.Upload(fileStream, remotePath), cancellationToken);
            context.Logger.LogDebug("File transfer completed successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"File transfer failed for {localPath}: {ex.Message}", ex);
        }
    }

    private async Task ExecuteSSHCommand(PipelineStepContext context, string command, CancellationToken cancellationToken)
    {
        var result = await ExecuteSSHCommandWithOutput(context, command, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"SSH command failed: {result.Error}");
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> ExecuteSSHCommandWithOutput(PipelineStepContext context, string command, CancellationToken cancellationToken)
    {
        context.Logger.LogDebug("Executing SSH command: {Command}", command);

        var startTime = DateTime.UtcNow;

        try
        {
            if (_sshClient == null || !_sshClient.IsConnected)
            {
                throw new InvalidOperationException("SSH connection not established");
            }

            using var sshCommand = _sshClient.CreateCommand(command);
            await sshCommand.ExecuteAsync(cancellationToken);
            var result = sshCommand.Result ?? "";

            var endTime = DateTime.UtcNow;
            var exitCode = sshCommand.ExitStatus ?? -1;
            context.Logger.LogDebug("SSH command completed in {Duration:F1}s, exit code: {ExitCode}", (endTime - startTime).TotalSeconds, exitCode);

            if (exitCode != 0)
            {
                context.Logger.LogDebug("SSH error output: {Error}", sshCommand.Error);
            }

            return (exitCode, result, sshCommand.Error ?? "");
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            context.Logger.LogDebug("SSH command failed in {Duration:F1}s: {Message}", (endTime - startTime).TotalSeconds, ex.Message);
            return (-1, "", ex.Message);
        }
    }

    private async Task EstablishAndTestSSHConnection(PipelineStepContext context, string host, string username, string? password, string? keyPath, string port, IReportingStep step, CancellationToken cancellationToken)
    {
        context.Logger.LogDebug("Establishing SSH connection using SSH.NET");

        // Task 1: Establish SSH connection
        await using var connectTask = await step.CreateTaskAsync("Establishing SSH connection", cancellationToken);

        try
        {
            context.Logger.LogDebug("Creating SSH and SCP connections...");

            var connectionInfo = SSHUtility.CreateConnectionInfo(host, username, password, keyPath, port);
            _sshClient = await SSHUtility.CreateSSHClient(connectionInfo, cancellationToken);
            _scpClient = await SSHUtility.CreateSCPClient(connectionInfo, cancellationToken);

            context.Logger.LogDebug("SSH and SCP connections established successfully");
            await connectTask.SucceedAsync("SSH and SCP connections established", cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug("Failed to establish SSH connection: {Message}", ex.Message);
            await CleanupSSHConnection(context);
            throw;
        }

        // Task 2: Test basic SSH connectivity
        await using var testTask = await step.CreateTaskAsync("Testing SSH connectivity", cancellationToken);

        // First test basic connectivity
        var testCommand = "echo 'SSH connection successful'";
        var result = await ExecuteSSHCommandWithOutput(context, testCommand, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"SSH connection test failed: {result.Error}");
        }

        await testTask.SucceedAsync($"Connection tested successfully\nCommand: {testCommand}\nOutput: {result.Output}", cancellationToken: cancellationToken);

        // Task 3: Verify remote system access
        await using var verifyTask = await step.CreateTaskAsync("Verifying remote system access", cancellationToken);

        // Test if we can get basic system information
        var infoCommand = "whoami && pwd && ls -la";
        var infoResult = await ExecuteSSHCommandWithOutput(context, infoCommand, cancellationToken);

        if (infoResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"SSH system info check failed: {infoResult.Error}");
        }

        await verifyTask.SucceedAsync($"Remote system access verified", cancellationToken: cancellationToken);
    }

    private async Task CleanupSSHConnection(PipelineStepContext context)
    {
        try
        {
            if (_sshClient != null)
            {
                if (_sshClient.IsConnected)
                {
                    _sshClient.Disconnect();
                }
                _sshClient.Dispose();
                _sshClient = null;
                context.Logger.LogDebug("SSH client connection cleaned up");
            }

            if (_scpClient != null)
            {
                if (_scpClient.IsConnected)
                {
                    _scpClient.Disconnect();
                }
                _scpClient.Dispose();
                _scpClient = null;
                context.Logger.LogDebug("SCP client connection cleaned up");
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug("Error cleaning up SSH connections: {Message}", ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_sshClient != null)
            {
                if (_sshClient.IsConnected) _sshClient.Disconnect();
                _sshClient.Dispose();
                _sshClient = null;
            }

            if (_scpClient != null)
            {
                if (_scpClient.IsConnected) _scpClient.Disconnect();
                _scpClient.Dispose();
                _scpClient = null;
            }
        }
        catch
        {
            // Suppress exceptions during disposal
        }

        GC.SuppressFinalize(this);
    }

    private async Task<Dictionary<string, string>> PushContainerImagesToRegistry(PipelineStepContext context, RegistryConfiguration registryConfig, CancellationToken cancellationToken)
    {
        var imageTags = new Dictionary<string, string>();
        var registryUrl = registryConfig.RegistryUrl;
        var repositoryPrefix = registryConfig.RepositoryPrefix;

        // Create the progress step for container image pushing
        var step = context.ReportingStep;

        // Read the .env.Production file to find all built images
        var envProductionPath = Path.Combine(OutputPath, ".env.Production");
        if (!File.Exists(envProductionPath))
        {
            throw new InvalidOperationException($".env.Production file not found at {envProductionPath}. Ensure prepare-{DockerComposeEnvironment.Name} step has run.");
        }

        var envVars = await EnvironmentFileUtility.ReadEnvironmentFile(envProductionPath);

        // Find all *_IMAGE variables
        var imageVars = envVars.Where(kvp => kvp.Key.EndsWith("_IMAGE", StringComparison.OrdinalIgnoreCase)).ToList();

        if (imageVars.Count == 0)
        {
            await step.WarnAsync("No container images found in .env.Production file to push.");
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
            var tagResult = await DockerCommandUtility.ExecuteDockerCommand($"tag {localImageName} {targetImageName}", cancellationToken);

            if (tagResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to tag image {localImageName}: {tagResult.Error}");
            }

            imageTags[serviceName] = targetImageName;
            await tagTask.SucceedAsync($"Successfully tagged {serviceName} image: {targetImageName}", cancellationToken: cancellationToken);
        }


        static async Task PushContainerImageAsync(IReportingStep step, string serviceName, string targetImageName, CancellationToken cancellationToken)
        {
            await using var pushTask = await step.CreateTaskAsync($"Pushing {serviceName} image", cancellationToken);

            var pushResult = await DockerCommandUtility.ExecuteDockerCommand($"push {targetImageName}", cancellationToken);

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

        // Read the .env.Production file generated by prepare-env step
        var envProductionPath = Path.Combine(OutputPath, ".env.Production");
        if (!File.Exists(envProductionPath))
        {
            throw new InvalidOperationException($".env.Production file not found at {envProductionPath}. Ensure prepare-{DockerComposeEnvironment.Name} step has run.");
        }

        // Read environment variables from .env.Production (already filled in by prepare-env)
        var envVars = await EnvironmentFileUtility.ReadEnvironmentFile(envProductionPath);

        // Update *_IMAGE variables with registry-tagged images
        foreach (var (serviceName, registryImageTag) in imageTags)
        {
            var imageEnvKey = $"{serviceName.ToUpperInvariant()}_IMAGE";
            envVars[imageEnvKey] = registryImageTag;
        }

        var finalEnvVars = envVars.OrderBy(kvp => kvp.Key).ToList();

        var finalizeStep = context.ReportingStep;

        await using var envFileTask = await finalizeStep.CreateTaskAsync("Creating and transferring environment file", cancellationToken);

        context.Logger.LogDebug("Processed {Count} environment variables", finalEnvVars.Count);

        // Create environment file content
        var envContent = string.Join("\n", finalEnvVars.Select(kvp => $"{kvp.Key}={kvp.Value}"));

        // Write to temporary local file
        var tempFile = Path.Combine(OutputPath, "remote.env");
        var remoteEnvPath = $"{remoteDeployPath}/.env";

        context.Logger.LogDebug("Writing environment file to {TempFile}...", tempFile);
        await File.WriteAllTextAsync(tempFile, envContent, cancellationToken);

        // Ensure the remote directory exists before transferring
        context.Logger.LogDebug("Ensuring remote directory exists: {RemoteDeployPath}", remoteDeployPath);
        await ExecuteSSHCommand(context, $"mkdir -p '{remoteDeployPath}'", cancellationToken);

        context.Logger.LogDebug("Transferring environment file to remote path: {RemoteEnvPath}", remoteEnvPath);
        await TransferFile(context, tempFile, remoteEnvPath, cancellationToken);

        await envFileTask.SucceedAsync($"Environment file successfully transferred to {remoteEnvPath}", cancellationToken);

        await finalizeStep.SucceedAsync($"Environment configuration finalized with {finalEnvVars.Count} variables");
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

    internal class SSHConnectionContext
    {
        // User-selected values
        public required string TargetHost { get; set; }
        public required string SshUsername { get; set; }
        public string? SshPassword { get; set; }
        public string? SshKeyPath { get; set; }
        public required string SshPort { get; set; }
        public required string RemoteDeployPath { get; set; }
    }

    internal sealed record RegistryConfiguration(string RegistryUrl, string? RepositoryPrefix, string? RegistryUsername, string? RegistryPassword);
}
