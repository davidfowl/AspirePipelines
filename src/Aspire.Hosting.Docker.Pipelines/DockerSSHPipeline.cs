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

internal class DockerSSHPipeline : IAsyncDisposable
{
    private SshClient? _sshClient = null;
    private ScpClient? _scpClient = null;

    public async Task Deploy(DeployingContext context)
    {
        // Step 0: Prerequisites check
        await CheckPrerequisitesConcurrently(context);

        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        var configuration = context.Services.GetRequiredService<IConfiguration>();

        // Get configuration defaults once
        var configDefaults = ConfigurationUtility.GetConfigurationDefaults(configuration);

        // Step 1: Prepare SSH connection context (includes user prompting)
        var sshContext = await PrepareSSHConnectionContext(context, configDefaults, interactionService);

        // Step 1: Verify deployment files exist
        await using var verifyStep = await context.ProgressReporter.CreateStepAsync("Verify deployment files", context.CancellationToken);

        try
        {
            await using var verifyTask = await verifyStep.CreateTaskAsync("Checking for required files", context.CancellationToken);

            var envFileExists = await DeploymentFileUtility.VerifyDeploymentFiles(context);
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

        // Step 2: Push container images to registry
        var imageTags = await PushContainerImagesToRegistry(context, interactionService, configDefaults, context.CancellationToken);

        // Step 3: Establish and test SSH connection
        await using var sshTestStep = await context.ProgressReporter.CreateStepAsync("Establish and test SSH connection", context.CancellationToken);

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

        // Step 4: Prepare remote environment
        await using var prepareStep = await context.ProgressReporter.CreateStepAsync("Prepare remote environment", context.CancellationToken);

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

        await MergeAndUpdateEnvironmentFile(sshContext.RemoteDeployPath, imageTags, context, interactionService, context.CancellationToken);

        // Step 5: Transfer files to target server
        await using var transferStep = await context.ProgressReporter.CreateStepAsync("Transfer deployment files", context.CancellationToken);

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

        // Step 6: Deploy on target server
        await using var deployStep = await context.ProgressReporter.CreateStepAsync("Deploy application", context.CancellationToken);

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
        finally
        {
            // Clean up SSH connection
            await CleanupSSHConnection();
        }
    }

    private async Task CheckPrerequisitesConcurrently(DeployingContext context)
    {
        await using var prerequisiteStep = await context.ProgressReporter.CreateStepAsync("Checking deployment prerequisites", context.CancellationToken);

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

            // Add discovered known hosts (avoid duplicates)
            foreach (var host in sshConfig.KnownHosts)
            {
                if (string.IsNullOrEmpty(configDefaults.SshHost) || host != configDefaults.SshHost)
                {
                    hostOptions.Add(new KeyValuePair<string, string>(host, host));
                }
            }

            // Add custom host option
            hostOptions.Add(new KeyValuePair<string, string>("CUSTOM", "Enter custom host..."));

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

            // Add custom key option
            sshKeyOptions.Add(new KeyValuePair<string, string>("CUSTOM", "Enter custom key path..."));

            return sshKeyOptions;
        }

        // Local function for choice prompts
        async Task<string> PromptForChoice(string title, string description, string label, List<KeyValuePair<string, string>> options, string? defaultValue = null)
        {
            var inputs = new InteractionInput[]
            {
                new() {
                    InputType = InputType.Choice,
                    Label = label,
                    Options = options,
                    Value = defaultValue ?? options.FirstOrDefault().Key
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
        async Task<string> PromptForSingleText(string title, string description, string label, bool required = true)
        {
            var inputs = new InteractionInput[]
            {
                new() {
                    Required = required,
                    InputType = InputType.Text,
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
            // First prompt: Target host selection
            var selectedHost = await PromptForChoice(
                "Target Host Selection",
                "Please select the target server for deployment.",
                "Target Server Host",
                hostOptions
            );

            // Second prompt: Custom host if needed
            if (selectedHost == "CUSTOM")
            {
                return await PromptForSingleText(
                    "Custom Host Configuration",
                    "Please enter the custom host details.",
                    "Custom Host"
                );
            }

            if (string.IsNullOrEmpty(selectedHost))
            {
                throw new InvalidOperationException("Target server host is required");
            }

            return selectedHost;
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

            // Second prompt: Custom key path if needed
            if (selectedKeyPath == "CUSTOM")
            {
                return await PromptForSingleText(
                    "Custom SSH Key Configuration",
                    "Please enter the path to your SSH private key file.",
                    "Custom SSH Key Path"
                );
            }

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
                    Required = true,
                    InputType = InputType.Text,
                    Label = "SSH Username",
                    Value = configDefaults.SshUsername ?? "root"
                },
                new() {
                    InputType = InputType.SecretText,
                    Label = "SSH Password",

                },
                new() {
                    InputType = InputType.Text,
                    Label = "SSH Port",
                    Value = !string.IsNullOrEmpty(configDefaults.SshPort) && configDefaults.SshPort != "22" ? configDefaults.SshPort : "22"
                },
                new() {
                    InputType = InputType.Text,
                    Label = "Remote Deploy Path",
                    Value = !string.IsNullOrEmpty(configDefaults.RemoteDeployPath) ? configDefaults.RemoteDeployPath : sshConfig.DefaultDeployPath
                }
            };

            var result = await interactionService.PromptInputsAsync(
                $"SSH Configuration for {targetHost}",
                $"Please provide the SSH configuration details for connecting to {targetHost}.",
                inputs
            );

            if (result.Canceled)
            {
                throw new InvalidOperationException("SSH configuration was canceled");
            }

            var sshUsername = result.Data[0].Value ?? throw new InvalidOperationException("SSH username is required");
            var sshPassword = string.IsNullOrEmpty(result.Data[1].Value) ? null : result.Data[1].Value;
            var sshPort = result.Data[2].Value ?? "22";
            var remoteDeployPath = result.Data[3].Value ?? $"/home/{sshUsername}/aspire-app";

            return (sshUsername, sshPassword, sshPort, remoteDeployPath);
        }

        // Main method logic starts here
        // Discover SSH configuration
        var sshConfig = await SSHConfigurationDiscovery.DiscoverSSHConfiguration(context);

        // Build host options for selection
        var hostOptions = BuildHostOptions(configDefaults, sshConfig);

        // Get target host through progressive prompting
        var targetHost = await PromptForTargetHost(hostOptions);

        // Build SSH key options for selection
        var sshKeyOptions = BuildSshKeyOptions(sshConfig);

        // Get SSH key path through progressive prompting
        var sshKeyPath = await PromptForSshKeyPath(sshKeyOptions, configDefaults, sshConfig);

        // Get SSH configuration details
        var sshDetails = await PromptForSshDetails(targetHost, sshKeyPath, configDefaults, sshConfig);

        // Validate SSH authentication method
        if (string.IsNullOrEmpty(sshDetails.SshPassword) && string.IsNullOrEmpty(sshKeyPath))
        {
            throw new InvalidOperationException("Either SSH password or SSH private key path must be provided");
        }

        // Return the final SSH connection context
        return new SSHConnectionContext
        {
            TargetHost = targetHost,
            SshUsername = sshDetails.SshUsername,
            SshPassword = sshDetails.SshPassword,
            SshKeyPath = sshKeyPath,
            SshPort = sshDetails.SshPort,
            RemoteDeployPath = ExpandRemotePath(sshDetails.RemoteDeployPath)
        };
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
        if (string.IsNullOrEmpty(context.OutputPath))
        {
            throw new InvalidOperationException("Deployment output path not available");
        }

        await using var scanTask = await step.CreateTaskAsync("Scanning files for transfer", cancellationToken);

        // Check for both .yml and .yaml extensions for docker-compose file
        var dockerComposeFile = File.Exists(Path.Combine(context.OutputPath, "docker-compose.yml"))
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
            var localPath = Path.Combine(context.OutputPath, file);
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
            var localPath = Path.Combine(context.OutputPath, file);
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
            var localPath = Path.Combine(context.OutputPath, file);
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

        await startTask.SucceedAsync($"New containers started\nCommand: cd {deployPath} && (docker compose up -d || docker-compose up -d)\nOutput: {startResult.Output.Trim()}", cancellationToken: cancellationToken);

        // Use the new HealthCheckUtility to check each service individually
        await HealthCheckUtility.CheckServiceHealth(deployPath, _sshClient!, step, cancellationToken);

        // Get final service status for summary
        var finalServiceStatuses = await HealthCheckUtility.GetServiceStatuses(deployPath, _sshClient!, cancellationToken);
        var healthyServices = finalServiceStatuses.Count(s => s.IsHealthy);

        // Try to extract port information
        var serviceUrls = await PortInformationUtility.ExtractPortInformation(deployPath, _sshClient!, cancellationToken);

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

        await verifyTask.SucceedAsync($"Remote system access verified\nCommand: {infoCommand}\nSystem info: {infoResult.Output.Trim()}", cancellationToken: cancellationToken);
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

    private async Task<Dictionary<string, string>> PushContainerImagesToRegistry(DeployingContext context, IInteractionService interactionService, DockerSSHConfiguration configDefaults, CancellationToken cancellationToken)
    {
        var imageTags = new Dictionary<string, string>();

        var registryInputs = new InteractionInput[]
        {
            new() {
                Required = true,
                InputType = InputType.Text,
                Label = "Container Registry URL",
                Value = !string.IsNullOrEmpty(configDefaults.RegistryUrl) ? configDefaults.RegistryUrl : "docker.io"
            },
            new() {
                InputType = InputType.Text,
                Label = "Image Repository Prefix",
                Value = configDefaults.RepositoryPrefix
            },
            new() {
                InputType = InputType.Text,
                Label = "Registry Username",
                Value = configDefaults.RegistryUsername
            },
            new() {
                InputType = InputType.SecretText,
                Label = "Registry Password/Token",
            }
        };

        var registryResult = await interactionService.PromptInputsAsync(
            "Container Registry Configuration",
            "Please provide the container registry details for pushing images.",
            registryInputs,
            cancellationToken: cancellationToken
        );

        if (registryResult.Canceled)
        {
            throw new InvalidOperationException("Registry configuration was canceled");
        }

        var registryUrl = registryResult.Data[0].Value ?? throw new InvalidOperationException("Registry URL is required");
        var repositoryPrefix = registryResult.Data[1].Value?.Trim();
        var registryUsername = registryResult.Data[2].Value;
        var registryPassword = registryResult.Data[3].Value;

        // Create the progress step for container image pushing
        await using var step = await context.ProgressReporter.CreateStepAsync("Push container images to registry", cancellationToken);

        try
        {
            // Task 2: Docker login (only if credentials are provided)
            if (!string.IsNullOrEmpty(registryUsername) && !string.IsNullOrEmpty(registryPassword))
            {
                await using var loginTask = await step.CreateTaskAsync("Authenticating with container registry", cancellationToken);

                try
                {
                    var loginResult = await DockerCommandUtility.ExecuteDockerLogin(registryUrl, registryUsername, registryPassword, cancellationToken);

                    if (loginResult.ExitCode != 0)
                    {
                        await loginTask.FailAsync($"Docker login failed: {loginResult.Error}\nOutput: {loginResult.Output}", cancellationToken);
                        throw new InvalidOperationException($"Docker login failed: {loginResult.Error}");
                    }

                    await loginTask.SucceedAsync($"Successfully authenticated with {registryUrl}", cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    await loginTask.FailAsync($"Docker login failed: {ex.Message}", cancellationToken);
                    throw new InvalidOperationException($"Docker login failed: {ex.Message}", ex);
                }
            }
            else
            {
                await using var skipLoginTask = await step.CreateTaskAsync("Skipping registry authentication", cancellationToken);
                await skipLoginTask.SucceedAsync("No credentials provided - using existing Docker authentication or public registry", cancellationToken: cancellationToken);
            }

            // Generate timestamp-based tag
            var imageTag = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

            if (string.IsNullOrEmpty(context.OutputPath))
            {
                throw new InvalidOperationException("Deployment output path not available");
            }

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

            // Task 4: Push images to registry (one task per image)
            foreach (var (serviceName, targetImageName) in imageTags)
            {
                await using var pushTask = await step.CreateTaskAsync($"Pushing {serviceName} image", cancellationToken);

                var pushResult = await DockerCommandUtility.ExecuteDockerCommand($"push {targetImageName}", cancellationToken);

                if (pushResult.ExitCode != 0)
                {
                    await pushTask.FailAsync($"Failed to push image {targetImageName}: {pushResult.Error}\nOutput: {pushResult.Output}", cancellationToken);
                    throw new InvalidOperationException($"Failed to push image {targetImageName}: {pushResult.Error}");
                }

                await pushTask.SucceedAsync($"Successfully pushed {serviceName} image: {targetImageName}", cancellationToken: cancellationToken);
            }

            await step.SucceedAsync($"Container images pushed successfully. Tags: {string.Join(", ", imageTags.Select(kvp => $"{kvp.Key}:{kvp.Value}"))}");
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
        if (string.IsNullOrEmpty(context.OutputPath))
        {
            return; // No local environment to work with
        }

        var localEnvPath = Path.Combine(context.OutputPath, ".env");
        if (!File.Exists(localEnvPath))
        {
            return; // No local .env file to merge
        }

        // Step 1: Read local environment file
        var localEnvVars = await EnvironmentFileUtility.ReadEnvironmentFile(localEnvPath);

        // Step 2: Get remote environment file (if it exists)
        var remoteEnvVars = new Dictionary<string, string>();
        var remoteEnvPath = $"{remoteDeployPath}/.env";

        try
        {
            var remoteEnvResult = await ExecuteSSHCommandWithOutput($"cat '{remoteEnvPath}' 2>/dev/null || echo 'FILE_NOT_FOUND'", cancellationToken);
            if (remoteEnvResult.ExitCode == 0 && !remoteEnvResult.Output.Contains("FILE_NOT_FOUND"))
            {
                remoteEnvVars = EnvironmentFileUtility.ParseEnvironmentContent(remoteEnvResult.Output);
            }
        }
        catch
        {
            // Remote env file doesn't exist or can't be read - continue with empty remote vars
        }

        // Step 3: Merge environment variables
        var mergedEnvVars = new Dictionary<string, string>(remoteEnvVars, StringComparer.OrdinalIgnoreCase);

        // Update with image tags for each service
        foreach (var (serviceName, imageTag) in imageTags)
        {
            var imageEnvKey = $"{serviceName.ToUpperInvariant()}_IMAGE";
            mergedEnvVars[imageEnvKey] = imageTag;
        }

        // Add any new variables from local that don't exist in remote
        foreach (var (key, value) in localEnvVars)
        {
            if (!mergedEnvVars.ContainsKey(key))
            {
                mergedEnvVars[key] = value;
            }
        }

        // Step 4: Create input prompts for user to review/modify values
        var envInputs = new List<InteractionInput>();
        var sortedEnvVars = mergedEnvVars.OrderBy(kvp => kvp.Key).ToList();

        foreach (var (key, value) in sortedEnvVars)
        {
            var isImageVar = key.EndsWith("_IMAGE", StringComparison.OrdinalIgnoreCase);
            var isSensitive = EnvironmentFileUtility.IsSensitiveEnvironmentVariable(key);

            envInputs.Add(new InteractionInput
            {
                InputType = isSensitive ? InputType.SecretText : InputType.Text,
                Label = $"Environment Variable: {key}",
                Value = string.IsNullOrEmpty(value) ? null : value,
                Required = string.IsNullOrEmpty(value) || !isImageVar
            });
        }

        if (envInputs.Count == 0)
        {
            return; // Nothing to configure
        }

        // Step 4: Prompt user for environment values
        var envResult = await interactionService.PromptInputsAsync(
            "Environment Configuration",
            "Please review and update the environment variables for deployment.\nImage variables have been automatically populated from the registry.\n",
            [.. envInputs],
            cancellationToken: cancellationToken
        );

        if (envResult.Canceled)
        {
            throw new InvalidOperationException("Environment configuration was canceled");
        }

        // Step 5: Process user input and create final environment file
        await using var finalizeStep = await context.ProgressReporter.CreateStepAsync("Finalizing environment configuration", cancellationToken);

        await using var envFileTask = await finalizeStep.CreateTaskAsync("Creating and transferring environment file", cancellationToken);

        try
        {
            // Step 5: Prepare final environment content and write to remote
            var finalEnvVars = new Dictionary<string, string>();
            for (int i = 0; i < sortedEnvVars.Count; i++)
            {
                var key = sortedEnvVars[i].Key;
                var newValue = envResult.Data[i].Value ?? "";
                if (!string.IsNullOrEmpty(newValue))
                {
                    finalEnvVars[key] = newValue;
                }
            }

            await envFileTask.UpdateAsync($"Processed {finalEnvVars.Count} environment variables", cancellationToken);

            // Create environment file content
            var envContent = string.Join("\n", finalEnvVars.Select(kvp => $"{kvp.Key}={kvp.Value}"));

            // Write to remote environment file
            var tempFile = Path.GetTempFileName();
            try
            {
                await envFileTask.UpdateAsync("Writing environment file to temporary location...", cancellationToken);
                await File.WriteAllTextAsync(tempFile, envContent, cancellationToken);

                // Ensure the remote directory exists before transferring
                await envFileTask.UpdateAsync($"Ensuring remote directory exists: {remoteDeployPath}", cancellationToken);
                await ExecuteSSHCommand($"mkdir -p '{remoteDeployPath}'", cancellationToken);

                await envFileTask.UpdateAsync($"Transferring environment file to remote path: {remoteEnvPath}", cancellationToken);
                await TransferFile(tempFile, remoteEnvPath, cancellationToken);

                await envFileTask.SucceedAsync($"Environment file successfully transferred to {remoteEnvPath}", cancellationToken);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }

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
}
