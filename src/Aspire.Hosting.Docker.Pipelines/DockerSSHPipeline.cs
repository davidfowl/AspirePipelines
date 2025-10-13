#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPUBLISHERS001

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Docker.Pipelines.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using Aspire.Hosting.Docker.Pipelines.Models;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Docker;
using System.Reflection;

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

    public IEnumerable<PipelineStep> CreateSteps()
    {
        var prepareTempDir = new PipelineStep
        {
            Name = "Prepare Temporary Directory",
            Action = async context =>
            {
                _outputPath = context.OutputPath ?? Directory.CreateTempSubdirectory("aspire-deploy").FullName;
            }
        };

        // Base prerequisite step
        var prereqs = new PipelineStep { Name = "Docker SSH Prerequisites Check", Action = CheckPrerequisitesConcurrently };
        prereqs.DependsOn(prepareTempDir);

        // Step sequence mirroring the logical deployment flow
        var prepareSshContext = new PipelineStep { Name = "Prepare SSH Context", Action = PrepareSSHContextStep };

        var emitDeploymentFiles = new PipelineStep { Name = "Emit Deployment Files", Action = EmitDockerComposeFileStep };

        var configureRegistry = new PipelineStep { Name = "Configure Container Registry", Action = ConfigureRegistryStep };

        var pushImages = new PipelineStep { Name = "Push Container Images", Action = PushImagesStep };
        pushImages.DependsOn(configureRegistry);
        pushImages.DependsOn(emitDeploymentFiles);

        var establishSsh = new PipelineStep { Name = "Establish SSH Connection", Action = EstablishSSHConnectionStep }; // tests connectivity
        establishSsh.DependsOn(prepareSshContext);

        var prepareRemote = new PipelineStep { Name = "Prepare Remote Environment", Action = PrepareRemoteEnvironmentStep };
        prepareRemote.DependsOn(establishSsh);

        var mergeEnv = new PipelineStep { Name = "Merge Environment File", Action = MergeEnvironmentFileStep };
        mergeEnv.DependsOn(prepareRemote);

        var transferFiles = new PipelineStep { Name = "Transfer Deployment Files", Action = TransferDeploymentFilesPipelineStep };
        transferFiles.DependsOn(mergeEnv);

        var deploy = new PipelineStep { Name = "Deploy Application", Action = DeployApplicationStep };
        deploy.DependsOn(transferFiles);

        // Post-deploy: Extract dashboard login token from logs
        var extractDashboardToken = new PipelineStep { Name = "Extract Dashboard Login Token", Action = ExtractDashboardLoginTokenStep };
        extractDashboardToken.DependsOn(deploy);

        // Final cleanup step to close SSH/SCP connections
        var cleanup = new PipelineStep { Name = "Cleanup SSH Connection", Action = CleanupSSHConnectionStep };
        cleanup.DependsOn(extractDashboardToken);

        return [prepareTempDir, prereqs, prepareSshContext, emitDeploymentFiles, configureRegistry, pushImages, establishSsh, prepareRemote, mergeEnv, transferFiles, deploy, extractDashboardToken, cleanup];
    }


    #region Deploy Step Helpers
    private async Task VerifyDeploymentFilesAsync(DeployingContext context)
    {
        await using var verifyStep = await context.ActivityReporter.CreateStepAsync("Verify deployment files", context.CancellationToken);
        try
        {
            await using var verifyTask = await verifyStep.CreateTaskAsync("Checking for required files", context.CancellationToken);
            var envFileExists = await DeploymentFileUtility.VerifyDeploymentFiles(OutputPath);
            if (envFileExists)
            {
                await verifyTask.SucceedAsync("docker-compose.yml and .env found");
                await verifyStep.SucceedAsync("Deployment files verified successfully");
            }
            else
            {
                await verifyTask.SucceedAsync("docker-compose.yml found, .env file missing but optional");
                await verifyStep.SucceedAsync("Deployment files verified (optional .env missing)");
            }
        }
        catch (Exception ex)
        {
            await verifyStep.FailAsync($"Failed to verify deployment files: {ex.Message}");
            throw;
        }
    }

    private async Task EstablishAndTestSSHConnectionStepAsync(SSHConnectionContext sshContext, DeployingContext context)
    {
        await using var sshTestStep = await context.ActivityReporter.CreateStepAsync("Establish and test SSH connection", context.CancellationToken);
        try
        {
            await EstablishAndTestSSHConnection(sshContext.TargetHost, sshContext.SshUsername, sshContext.SshPassword, sshContext.SshKeyPath, sshContext.SshPort, sshTestStep, context.CancellationToken);
            await sshTestStep.SucceedAsync("SSH connection established and tested successfully");
        }
        catch (Exception ex)
        {
            await sshTestStep.FailAsync($"SSH connection failed: {ex.Message}");
            throw;
        }
    }

    private async Task PrepareRemoteEnvironmentStepAsync(SSHConnectionContext sshContext, DeployingContext context)
    {
        await using var prepareStep = await context.ActivityReporter.CreateStepAsync("Prepare remote environment", context.CancellationToken);
        try
        {
            await PrepareRemoteEnvironment(sshContext.RemoteDeployPath, prepareStep, context.CancellationToken);
            await prepareStep.SucceedAsync("Remote environment ready for deployment");
        }
        catch (Exception ex)
        {
            await prepareStep.FailAsync($"Failed to prepare remote environment: {ex.Message}");
            throw;
        }
    }

    private async Task TransferDeploymentFilesStepAsync(SSHConnectionContext sshContext, DeployingContext context)
    {
        await using var transferStep = await context.ActivityReporter.CreateStepAsync("Transfer deployment files", context.CancellationToken);
        try
        {
            await TransferDeploymentFiles(sshContext.RemoteDeployPath, context, transferStep, context.CancellationToken);
            await transferStep.SucceedAsync("File transfer completed");
        }
        catch (Exception ex)
        {
            await transferStep.FailAsync($"Failed to transfer files: {ex.Message}");
            throw;
        }
    }

    private async Task DeployOnRemoteServerStepAsync(SSHConnectionContext sshContext, Dictionary<string, string> imageTags, DeployingContext context)
    {
        await using var deployStep = await context.ActivityReporter.CreateStepAsync("Deploy application", context.CancellationToken);
        try
        {
            var deploymentInfo = await DeployOnRemoteServer(sshContext.RemoteDeployPath, imageTags, deployStep, context.CancellationToken);
            await deployStep.SucceedAsync($"Application deployed successfully! {deploymentInfo}");
        }
        catch (Exception ex)
        {
            await deployStep.FailAsync($"Failed to deploy on server: {ex.Message}");
            throw;
        }
    }

    private async Task CleanupSSHConnectionStep(DeployingContext context)
    {
        await using var cleanupStep = await context.ActivityReporter.CreateStepAsync("Cleanup SSH connection", context.CancellationToken);
        await CleanupSSHConnection();
    }
    #endregion

    #region Pipeline Step Implementations
    private async Task PrepareSSHContextStep(DeployingContext context)
    {
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var configDefaults = ConfigurationUtility.GetConfigurationDefaults(configuration);

        _sshContext = await PrepareSSHConnectionContext(context, configDefaults, interactionService);
    }

    private async Task EmitDockerComposeFileStep(DeployingContext context)
    {
        if (DockerComposeEnvironment.TryGetLastAnnotation<PublishingCallbackAnnotation>(out var annotation))
        {
            // Publish the docker compose file
            await annotation.Callback(new PublishingContext(context.Model, context.ExecutionContext, context.Services, context.Logger, context.CancellationToken, OutputPath));
        }

        await VerifyDeploymentFilesAsync(context);
    }

    private async Task ConfigureRegistryStep(DeployingContext context)
    {
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var interactionService = context.Services.GetRequiredService<IInteractionService>();

        await using var step = await context.ActivityReporter.CreateStepAsync("Configure registry", context.CancellationToken);

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

            await using var promptTask = await step.CreateTaskAsync("Collecting registry settings", context.CancellationToken);
            var result = await interactionService.PromptInputsAsync(
                "Container Registry Configuration",
                "Provide container registry details (leave credentials blank for anonymous access).\n",
                inputs,
                cancellationToken: context.CancellationToken);

            if (result.Canceled)
            {
                await promptTask.FailAsync("Canceled", context.CancellationToken);
                throw new InvalidOperationException("Registry configuration was canceled");
            }

            registryUrl = result.Data["registryUrl"].Value ?? throw new InvalidOperationException("Registry URL is required");
            repositoryPrefix = result.Data["repositoryPrefix"].Value?.Trim();
            registryUsername = result.Data["registryUsername"].Value;
            registryPassword = result.Data["registryPassword"].Value;
            await promptTask.SucceedAsync("Collected", context.CancellationToken);
        }

        if (!string.IsNullOrEmpty(registryUsername) && !string.IsNullOrEmpty(registryPassword))
        {
            await using var loginTask = await step.CreateTaskAsync("Authenticating", context.CancellationToken);
            var loginResult = await DockerCommandUtility.ExecuteDockerLogin(registryUrl, registryUsername, registryPassword, context.CancellationToken);
            if (loginResult.ExitCode != 0)
            {
                await loginTask.FailAsync($"Docker login failed: {loginResult.Error}", context.CancellationToken);
                throw new InvalidOperationException($"Docker login failed: {loginResult.Error}");
            }
            await loginTask.SucceedAsync($"Authenticated with {registryUrl}", context.CancellationToken);
        }
        else
        {
            await using var skipLogin = await step.CreateTaskAsync("Skipping authentication", context.CancellationToken);
            await skipLogin.SucceedAsync("No credentials provided", cancellationToken: context.CancellationToken);
        }

        _registryConfig = new RegistryConfiguration(registryUrl, repositoryPrefix, registryUsername, registryPassword);

        // Persist
        try
        {
            var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
            var state = await deploymentStateManager.LoadStateAsync(context.CancellationToken);
            var registryStateSection = state.Prop(RegistryContextKey);
            registryStateSection["RegistryUrl"] = registryUrl;
            registryStateSection["RepositoryPrefix"] = repositoryPrefix ?? string.Empty;
            registryStateSection["RegistryUsername"] = registryUsername ?? string.Empty;
            registryStateSection["RegistryPassword"] = registryPassword ?? string.Empty;

            await deploymentStateManager.SaveStateAsync(state, context.CancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Failed to persist registry configuration: {ex.Message}");
        }

        await step.SucceedAsync("Registry configured");
    }

    private async Task PushImagesStep(DeployingContext context)
    {
        if (_registryConfig is null)
        {
            throw new InvalidOperationException("Registry not configured before pushing images");
        }
        _imageTags = await PushContainerImagesToRegistry(context, _registryConfig, context.CancellationToken);
    }

    private async Task EstablishSSHConnectionStep(DeployingContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for establishing connection");
        }
        await EstablishAndTestSSHConnectionStepAsync(sshContext, context);

        // Save the SSH context to deployment state for future runs
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var state = await deploymentStateManager.LoadStateAsync(context.CancellationToken);
        var sshSection = state.Prop(SshContextKey);

        // Save SSH context to state for future use
        sshSection[nameof(SSHConnectionContext.TargetHost)] = sshContext.TargetHost;
        sshSection[nameof(SSHConnectionContext.SshUsername)] = sshContext.SshUsername;
        sshSection[nameof(SSHConnectionContext.SshPassword)] = sshContext.SshPassword ?? "";
        sshSection[nameof(SSHConnectionContext.SshKeyPath)] = sshContext.SshKeyPath ?? "";
        sshSection[nameof(SSHConnectionContext.SshPort)] = sshContext.SshPort;
        sshSection[nameof(SSHConnectionContext.RemoteDeployPath)] = sshContext.RemoteDeployPath;

        await deploymentStateManager.SaveStateAsync(state, context.CancellationToken);

    }

    private async Task PrepareRemoteEnvironmentStep(DeployingContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for preparing remote environment");
        }
        await PrepareRemoteEnvironmentStepAsync(sshContext, context);
    }

    private async Task MergeEnvironmentFileStep(DeployingContext context)
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

    private async Task TransferDeploymentFilesPipelineStep(DeployingContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for file transfer");
        }
        await TransferDeploymentFilesStepAsync(sshContext, context);
    }

    private async Task DeployApplicationStep(DeployingContext context)
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

    private async Task ExtractDashboardLoginTokenStep(DeployingContext context)
    {
        var sshContext = _sshContext;
        if (sshContext is null)
        {
            throw new InvalidOperationException("SSH context not available for dashboard token extraction");
        }

        await using var step = await context.ActivityReporter.CreateStepAsync("Extract dashboard login token", context.CancellationToken);

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
            var result = await ExecuteSSHCommandWithOutput(logCommand, context.CancellationToken);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            {
                await attemptTask.UpdateAsync("No logs yet or command failed; will retry", context.CancellationToken);
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
                await attemptTask.UpdateAsync("Token not found in current log snapshot", context.CancellationToken);
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

    private async Task CheckPrerequisitesConcurrently(DeployingContext context)
    {
        await using var prerequisiteStep = await context.ActivityReporter.CreateStepAsync("Checking deployment prerequisites", context.CancellationToken);

        // Create all prerequisite check tasks
        var dockerTask = DockerCommandUtility.CheckDockerAvailability(prerequisiteStep, context.CancellationToken);
        var dockerComposeTask = DockerCommandUtility.CheckDockerCompose(prerequisiteStep, context.CancellationToken);

        // Run all prerequisite checks concurrently
        try
        {
            await Task.WhenAll(dockerTask, dockerComposeTask);
            await prerequisiteStep.SucceedAsync("All prerequisites verified successfully");
        }
        catch (Exception ex)
        {
            await prerequisiteStep.FailAsync($"Prerequisites check failed: {ex.Message}");
            throw;
        }
    }

    private async Task<SSHConnectionContext> PrepareSSHConnectionContext(DeployingContext context, DockerSSHConfiguration configDefaults, IInteractionService interactionService)
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

    private async Task PrepareRemoteEnvironment(string deployPath, IPublishingStep step, CancellationToken cancellationToken)
    {
        await using var createDirTask = await step.CreateTaskAsync("Creating deployment directory", cancellationToken);

        // Create deployment directory
        await ExecuteSSHCommand($"mkdir -p {deployPath}", cancellationToken);

        await createDirTask.SucceedAsync($"Directory created: {deployPath}", cancellationToken: cancellationToken);

        await using var dockerCheckTask = await step.CreateTaskAsync("Verifying Docker installation", cancellationToken);

        // Check if Docker is installed and get version info
        var dockerVersionCheck = await ExecuteSSHCommandWithOutput("docker --version", cancellationToken);
        if (dockerVersionCheck.ExitCode != 0)
        {
            await dockerCheckTask.FailAsync($"Docker is not installed on the target server. Error: {dockerVersionCheck.Error}\nCommand: docker --version\nOutput: {dockerVersionCheck.Output}", cancellationToken);
            throw new InvalidOperationException($"Docker is not installed on the target server. Error: {dockerVersionCheck.Error}");
        }

        await dockerCheckTask.UpdateAsync("Verifying Docker daemon status...", cancellationToken);

        // Check if Docker daemon is running
        var dockerInfoCheck = await ExecuteSSHCommandWithOutput("docker info --format '{{.ServerVersion}}' 2>/dev/null", cancellationToken);
        if (dockerInfoCheck.ExitCode != 0)
        {
            await dockerCheckTask.FailAsync($"Docker daemon is not running on the target server. Error: {dockerInfoCheck.Error}\nCommand: docker info --format '{{{{.ServerVersion}}}}' 2>/dev/null\nOutput: {dockerInfoCheck.Output}", cancellationToken);
            throw new InvalidOperationException($"Docker daemon is not running on the target server. Error: {dockerInfoCheck.Error}");
        }

        await dockerCheckTask.UpdateAsync("Checking Docker Compose availability...", cancellationToken);

        // Check if Docker Compose is available
        var composeCheck = await ExecuteSSHCommandWithOutput("docker compose version", cancellationToken);
        if (composeCheck.ExitCode != 0)
        {
            await dockerCheckTask.FailAsync($"Docker Compose is not available on the target server. " +
                $"Command 'docker compose version' failed with exit code {composeCheck.ExitCode}. " +
                $"Error: {composeCheck.Error}", cancellationToken);
            throw new InvalidOperationException($"Docker Compose is not available on the target server. " +
                $"Command 'docker compose version' failed with exit code {composeCheck.ExitCode}. " +
                $"Error: {composeCheck.Error}");
        }

        await dockerCheckTask.SucceedAsync("Docker and Docker Compose verified", cancellationToken: cancellationToken);

        await using var permissionsTask = await step.CreateTaskAsync("Checking permissions and resources", cancellationToken);

        // Check if user can run Docker commands without sudo
        var dockerPermCheck = await ExecuteSSHCommandWithOutput("docker ps > /dev/null 2>&1 && echo 'OK' || echo 'SUDO_REQUIRED'", cancellationToken);
        if (dockerPermCheck.Output.Trim() == "SUDO_REQUIRED")
        {
            await permissionsTask.FailAsync($"User does not have permission to run Docker commands. Add user to 'docker' group and restart the session.\nCommand: docker ps > /dev/null 2>&1 && echo 'OK' || echo 'SUDO_REQUIRED'\nOutput: {dockerPermCheck.Output}\nError: {dockerPermCheck.Error}", cancellationToken);
            throw new InvalidOperationException($"User does not have permission to run Docker commands. Add user to 'docker' group and restart the session.");
        }

        // Check if there are any existing containers that might conflict
        var existingContainersCheck = await ExecuteSSHCommandWithOutput($"cd {deployPath} 2>/dev/null && (docker compose ps -q 2>/dev/null || docker-compose ps -q 2>/dev/null) | wc -l || echo '0'", cancellationToken);
        var existingContainers = existingContainersCheck.Output?.Trim() ?? "0";

        await permissionsTask.SucceedAsync($"Permissions and resources validated. Existing containers: {existingContainers}", cancellationToken: cancellationToken);
    }

    private async Task TransferDeploymentFiles(string deployPath, DeployingContext context, IPublishingStep step, CancellationToken cancellationToken)
    {
        await using var scanTask = await step.CreateTaskAsync("Scanning files for transfer", cancellationToken);

        // Check for both .yml and .yaml extensions for docker-compose file
        var dockerComposeFile = File.Exists(Path.Combine(OutputPath, "docker-compose.yml"))
            ? "docker-compose.yml"
            : "docker-compose.yaml";

        var filesToTransfer = new[]
        {
            (dockerComposeFile, true)   // Required (either .yml or .yaml)
            // Note: .env file is now handled separately in MergeAndUpdateEnvironmentFile
        };

        var transferredFiles = new List<string>();
        var skippedFiles = new List<string>();

        foreach (var (file, required) in filesToTransfer)
        {
            var localPath = Path.Combine(OutputPath, file);
            if (File.Exists(localPath))
            {
                transferredFiles.Add(file);
            }
            else if (required)
            {
                await scanTask.FailAsync($"Required file not found: {file} at {localPath}", cancellationToken);
                throw new InvalidOperationException($"Required file not found: {file} at {localPath}");
            }
            else
            {
                skippedFiles.Add(file);
            }
        }

        await scanTask.SucceedAsync($"Files scanned: {transferredFiles.Count} to transfer, .env file handled separately", cancellationToken: cancellationToken);

        await using var copyTask = await step.CreateTaskAsync("Copying files to remote server", cancellationToken);

        foreach (var file in transferredFiles)
        {
            var localPath = Path.Combine(OutputPath, file);
            await copyTask.UpdateAsync($"Transferring {file}...", cancellationToken);

            var remotePath = $"{deployPath}/{file}";
            await TransferFile(localPath, remotePath, cancellationToken);
        }

        await copyTask.SucceedAsync($"All {transferredFiles.Count} files transferred successfully", cancellationToken: cancellationToken);

        await using var verifyTask = await step.CreateTaskAsync("Verifying files on remote server", cancellationToken);

        foreach (var file in transferredFiles)
        {
            var remotePath = $"{deployPath}/{file}";

            // Check if file exists and get its size
            var verifyResult = await ExecuteSSHCommandWithOutput($"ls -la '{remotePath}' 2>/dev/null || echo 'FILE_NOT_FOUND'", cancellationToken);

            if (verifyResult.ExitCode != 0 || verifyResult.Output.Contains("FILE_NOT_FOUND"))
            {
                await verifyTask.FailAsync($"File verification failed for {file}: File not found at {remotePath}", cancellationToken);
                throw new InvalidOperationException($"File transfer verification failed: {file} not found on remote server");
            }

            // Get local file size for comparison
            var localPath = Path.Combine(OutputPath, file);
            var localFileInfo = new FileInfo(localPath);

            // Extract remote file size from ls output (5th column in ls -la output)
            var lsOutput = verifyResult.Output.Trim();
            var parts = lsOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && long.TryParse(parts[4], out var remoteSize))
            {
                if (localFileInfo.Length != remoteSize)
                {
                    await verifyTask.FailAsync($"File size mismatch for {file}: Local={localFileInfo.Length} bytes, Remote={remoteSize} bytes", cancellationToken);
                    throw new InvalidOperationException($"File transfer verification failed: Size mismatch for {file}");
                }

                await verifyTask.UpdateAsync($"✓ {file} verified ({localFileInfo.Length} bytes)", cancellationToken);
            }
            else
            {
                // Fallback: just check file exists with a simpler test
                var existsResult = await ExecuteSSHCommandWithOutput($"test -f '{remotePath}' && echo 'EXISTS' || echo 'NOT_FOUND'", cancellationToken);
                if (existsResult.Output.Trim() == "EXISTS")
                {
                    await verifyTask.UpdateAsync($"✓ {file} verified", cancellationToken);
                }
                else
                {
                    await verifyTask.FailAsync($"File verification failed for {file}: File does not exist at {remotePath}", cancellationToken);
                    throw new InvalidOperationException($"File transfer verification failed: {file} not found on remote server");
                }
            }
        }

        await verifyTask.SucceedAsync($"All {transferredFiles.Count} files verified on remote server", cancellationToken: cancellationToken);

        await using var summaryTask = await step.CreateTaskAsync("Deployment directory summary", cancellationToken);

        // Show final directory listing with file details
        var dirListResult = await ExecuteSSHCommandWithOutput($"ls -la '{deployPath}'", cancellationToken);
        if (dirListResult.ExitCode == 0)
        {
            await summaryTask.SucceedAsync($"Deployment directory contents:\nCommand: ls -la '{deployPath}'\n{dirListResult.Output.Trim()}", cancellationToken: cancellationToken);
        }
        else
        {
            await summaryTask.WarnAsync($"Could not list deployment directory: {dirListResult.Error}", cancellationToken);
        }
    }

    private async Task<string> DeployOnRemoteServer(string deployPath, Dictionary<string, string> imageTags, IPublishingStep step, CancellationToken cancellationToken)
    {
        await using var stopTask = await step.CreateTaskAsync("Stopping existing containers", cancellationToken);

        // Check if any containers are currently running in this deployment
        var existingCheck = await ExecuteSSHCommandWithOutput(
            $"cd {deployPath} && (docker compose ps -q || docker-compose ps -q || true) 2>/dev/null | wc -l", cancellationToken);

        // Stop existing containers if any
        var stopResult = await ExecuteSSHCommandWithOutput(
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

        await pullTask.UpdateAsync("Pulling latest container images...", cancellationToken);

        // Pull latest images (if using registry) - non-fatal if fails
        var pullResult = await ExecuteSSHCommandWithOutput(
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

        await startTask.UpdateAsync("Starting application containers...", cancellationToken);

        // Start services
        var startResult = await ExecuteSSHCommandWithOutput(
            $"cd {deployPath} && (docker compose up -d || docker-compose up -d)", cancellationToken);

        if (startResult.ExitCode != 0)
        {
            // Try to get more detailed error information
            var logsResult = await ExecuteSSHCommandWithOutput(
                $"cd {deployPath} && (docker compose logs --tail=50 || docker-compose logs --tail=50 || true)", cancellationToken);

            var errorDetails = string.IsNullOrEmpty(logsResult.Output) ? startResult.Error : logsResult.Output;
            await startTask.FailAsync($"Failed to start containers: {startResult.Error}\nCommand: cd {deployPath} && (docker compose up -d || docker-compose up -d)\nOutput: {startResult.Output}\nContainer logs:\n{errorDetails}", cancellationToken);
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

    private async Task TransferFile(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Transferring file {localPath} to {remotePath}");

        try
        {
            if (_scpClient == null || !_scpClient.IsConnected)
            {
                throw new InvalidOperationException("SCP connection not established");
            }

            using var fileStream = File.OpenRead(localPath);
            await Task.Run(() => _scpClient.Upload(fileStream, remotePath), cancellationToken);
            Console.WriteLine($"[DEBUG] File transfer completed successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"File transfer failed for {localPath}: {ex.Message}", ex);
        }
    }

    private async Task ExecuteSSHCommand(string command, CancellationToken cancellationToken)
    {
        var result = await ExecuteSSHCommandWithOutput(command, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"SSH command failed: {result.Error}");
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> ExecuteSSHCommandWithOutput(string command, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Executing SSH command: {command}");

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
            Console.WriteLine($"[DEBUG] SSH command completed in {(endTime - startTime).TotalSeconds:F1}s, exit code: {exitCode}");

            if (exitCode != 0)
            {
                Console.WriteLine($"[DEBUG] SSH error output: {sshCommand.Error}");
            }

            return (exitCode, result, sshCommand.Error ?? "");
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG] SSH command failed in {(endTime - startTime).TotalSeconds:F1}s: {ex.Message}");
            return (-1, "", ex.Message);
        }
    }

    private async Task EstablishAndTestSSHConnection(string host, string username, string? password, string? keyPath, string port, IPublishingStep step, CancellationToken cancellationToken)
    {
        Console.WriteLine("[DEBUG] Establishing SSH connection using SSH.NET");

        // Task 1: Establish SSH connection
        await using var connectTask = await step.CreateTaskAsync("Establishing SSH connection", cancellationToken);

        try
        {
            await connectTask.UpdateAsync("Creating SSH and SCP connections...", cancellationToken);

            var connectionInfo = SSHUtility.CreateConnectionInfo(host, username, password, keyPath, port);
            _sshClient = await SSHUtility.CreateSSHClient(connectionInfo, cancellationToken);
            _scpClient = await SSHUtility.CreateSCPClient(connectionInfo, cancellationToken);

            Console.WriteLine("[DEBUG] SSH and SCP connections established successfully");
            await connectTask.SucceedAsync("SSH and SCP connections established", cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Failed to establish SSH connection: {ex.Message}");
            await connectTask.FailAsync($"Failed to establish SSH connection: {ex.Message}", cancellationToken);
            await CleanupSSHConnection();
            throw;
        }

        // Task 2: Test basic SSH connectivity
        await using var testTask = await step.CreateTaskAsync("Testing SSH connectivity", cancellationToken);

        // First test basic connectivity
        var testCommand = "echo 'SSH connection successful'";
        var result = await ExecuteSSHCommandWithOutput(testCommand, cancellationToken);

        if (result.ExitCode != 0)
        {
            await testTask.FailAsync($"SSH connection test failed: {result.Error}\nCommand: {testCommand}\nOutput: {result.Output}", cancellationToken);
            throw new InvalidOperationException($"SSH connection test failed: {result.Error}");
        }

        await testTask.SucceedAsync($"Connection tested successfully\nCommand: {testCommand}\nOutput: {result.Output}", cancellationToken: cancellationToken);

        // Task 3: Verify remote system access
        await using var verifyTask = await step.CreateTaskAsync("Verifying remote system access", cancellationToken);

        // Test if we can get basic system information
        var infoCommand = "whoami && pwd && ls -la";
        var infoResult = await ExecuteSSHCommandWithOutput(infoCommand, cancellationToken);

        if (infoResult.ExitCode != 0)
        {
            await verifyTask.FailAsync($"SSH system info check failed: {infoResult.Error}\nCommand: {infoCommand}\nOutput: {infoResult.Output}", cancellationToken);
            throw new InvalidOperationException($"SSH system info check failed: {infoResult.Error}");
        }

        await verifyTask.SucceedAsync($"Remote system access verified", cancellationToken: cancellationToken);
    }

    private async Task CleanupSSHConnection()
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
                Console.WriteLine("[DEBUG] SSH client connection cleaned up");
            }

            if (_scpClient != null)
            {
                if (_scpClient.IsConnected)
                {
                    _scpClient.Disconnect();
                }
                _scpClient.Dispose();
                _scpClient = null;
                Console.WriteLine("[DEBUG] SCP client connection cleaned up");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error cleaning up SSH connections: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupSSHConnection();
        GC.SuppressFinalize(this);
    }

    private async Task<Dictionary<string, string>> PushContainerImagesToRegistry(DeployingContext context, RegistryConfiguration registryConfig, CancellationToken cancellationToken)
    {
        var imageTags = new Dictionary<string, string>();
        var registryUrl = registryConfig.RegistryUrl;
        var repositoryPrefix = registryConfig.RepositoryPrefix;

        // Create the progress step for container image pushing
        await using var step = await context.ActivityReporter.CreateStepAsync("Push container images to registry", cancellationToken);

        try
        {
            // Generate timestamp-based tag
            var imageTag = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

            // Task 4: Tag images for registry (one task per tag operation)
            foreach (var cr in context.Model.GetComputeResources())
            {
                // Skip containers that are not Dockerfile builds
                if (cr.IsContainer() && !cr.HasAnnotationOfType<DockerfileBuildAnnotation>())
                    continue;

                var serviceName = cr.Name;
                var imageName = cr.Name;

                await using var tagTask = await step.CreateTaskAsync($"Tagging {serviceName} image", cancellationToken);

                // Construct the target image name
                var targetImageName = !string.IsNullOrEmpty(repositoryPrefix)
                    ? $"{registryUrl}/{repositoryPrefix}/{serviceName}:{imageTag}"
                    : $"{registryUrl}/{serviceName}:{imageTag}";

                // Tag the image
                var tagResult = await DockerCommandUtility.ExecuteDockerCommand($"tag {imageName} {targetImageName}", cancellationToken);

                if (tagResult.ExitCode != 0)
                {
                    await tagTask.FailAsync($"Failed to tag image {imageName}: {tagResult.Error}\nOutput: {tagResult.Output}", cancellationToken);
                    throw new InvalidOperationException($"Failed to tag image {imageName}: {tagResult.Error}");
                }

                imageTags[serviceName] = targetImageName;
                await tagTask.SucceedAsync($"Successfully tagged {serviceName} image: {targetImageName}", cancellationToken: cancellationToken);
            }


            static async Task PushContainerImageAsync(IPublishingStep step, string serviceName, string targetImageName, CancellationToken cancellationToken)
            {
                await using var pushTask = await step.CreateTaskAsync($"Pushing {serviceName} image", cancellationToken);

                var pushResult = await DockerCommandUtility.ExecuteDockerCommand($"push {targetImageName}", cancellationToken);

                if (pushResult.ExitCode != 0)
                {
                    await pushTask.FailAsync($"Failed to push image {targetImageName}: {pushResult.Error}", cancellationToken);
                    throw new InvalidOperationException($"Failed to push image {targetImageName}: {pushResult.Error}");
                }

                await pushTask.SucceedAsync($"Successfully pushed {serviceName} image: {targetImageName}", cancellationToken: cancellationToken);
            }

            // Task 4: Push images to registry (one task per image)
            var tasks = new List<Task>();
            foreach (var (serviceName, targetImageName) in imageTags)
            {
                tasks.Add(PushContainerImageAsync(step, serviceName, targetImageName, cancellationToken));
            }

            await Task.WhenAll(tasks);

            await step.SucceedAsync("Container images pushed successfully.");
            return imageTags;
        }
        catch (Exception ex)
        {
            await step.FailAsync($"Failed to push container images: {ex.Message}");
            throw;
        }
    }

    private async Task MergeAndUpdateEnvironmentFile(string remoteDeployPath, Dictionary<string, string> imageTags, DeployingContext context, IInteractionService interactionService, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(OutputPath))
        {
            return; // No local environment to work with
        }

        var localEnvPath = Path.Combine(OutputPath, ".env");
        if (!File.Exists(localEnvPath))
        {
            return; // No local .env file to merge
        }

        // Step 1: Read local environment file
        var localEnvVars = await EnvironmentFileUtility.ReadEnvironmentFile(localEnvPath);

        // Update with image tags for each service
        foreach (var (serviceName, imageTag) in imageTags)
        {
            var imageEnvKey = $"{serviceName.ToUpperInvariant()}_IMAGE";
            localEnvVars[imageEnvKey] = imageTag;
        }

        // HACK: We should make this public
        var capturedEnvironmentVariables = typeof(DockerComposeEnvironmentResource).GetProperty("CapturedEnvironmentVariables", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(DockerComposeEnvironment) as Dictionary<string, (string? Description, string? DefaultValue, object? Source)>
        ?? [];

        // Any keys that map to a parameter should be set in the final env file
        foreach (var (k, v) in capturedEnvironmentVariables)
        {
            var (_, _, source) = v;

            if (source is ParameterResource p)
            {
                localEnvVars[k] = await p.GetValueAsync(context.CancellationToken) ?? "";
            }
        }

        // // Step 4: Create input prompts for user to review/modify values
        // var envInputs = new List<InteractionInput>();
        var finalEnvVars = localEnvVars.OrderBy(kvp => kvp.Key).ToList();

        // Step 5: Process user input and create final environment file
        await using var finalizeStep = await context.ActivityReporter.CreateStepAsync("Finalizing environment configuration", cancellationToken);

        await using var envFileTask = await finalizeStep.CreateTaskAsync("Creating and transferring environment file", cancellationToken);

        try
        {
            // Step 5: Prepare final environment content and write to remote
            await envFileTask.UpdateAsync($"Processed {finalEnvVars.Count} environment variables", cancellationToken);

            // Create environment file content
            var envContent = string.Join("\n", finalEnvVars.Select(kvp => $"{kvp.Key}={kvp.Value}"));

            // Write to remote environment file
            var tempFile = Path.Combine(OutputPath, "remote.env");
            var remoteEnvPath = $"{remoteDeployPath}/.env";

            await envFileTask.UpdateAsync($"Writing environment file to {tempFile}...", cancellationToken);
            await File.WriteAllTextAsync(tempFile, envContent, cancellationToken);

            // Ensure the remote directory exists before transferring
            await envFileTask.UpdateAsync($"Ensuring remote directory exists: {remoteDeployPath}", cancellationToken);
            await ExecuteSSHCommand($"mkdir -p '{remoteDeployPath}'", cancellationToken);

            await envFileTask.UpdateAsync($"Transferring environment file to remote path: {remoteEnvPath}", cancellationToken);
            await TransferFile(tempFile, remoteEnvPath, cancellationToken);

            await envFileTask.SucceedAsync($"Environment file successfully transferred to {remoteEnvPath}", cancellationToken);

            await finalizeStep.SucceedAsync($"Environment configuration finalized with {finalEnvVars.Count} variables");
        }
        catch (Exception ex)
        {
            await envFileTask.FailAsync($"Failed to create or transfer environment file: {ex.Message}", cancellationToken);
            await finalizeStep.FailAsync($"Environment configuration failed: {ex.Message}");
            throw;
        }
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
