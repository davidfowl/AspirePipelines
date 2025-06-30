#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPUBLISHERS001

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using System.Diagnostics;

internal class DockerSSHPipeline : IAsyncDisposable
{
    private SshClient? _sshClient = null;
    private ScpClient? _scpClient = null;

    public async Task Deploy(DeployingContext context)
    {
        // Step 0: Prerequisites check
        await CheckPrerequisitesConcurrently(context);

        var interactionService = context.Services.GetRequiredService<IInteractionService>();

        // Get configuration defaults
        var configDefaults = GetConfigurationDefaults(context);

        // Step 1: Discover SSH configuration
        var sshConfig = await DiscoverSSHConfiguration(context);

        // Prepare target host selection options
        var targetHostOptions = new List<KeyValuePair<string, string>>();

        // Add discovered known hosts
        foreach (var host in sshConfig.KnownHosts)
        {
            targetHostOptions.Add(new KeyValuePair<string, string>(host, host));
        }

        // Add custom host option
        targetHostOptions.Add(new KeyValuePair<string, string>("CUSTOM", "Enter custom host..."));

        var sshKeyOptions = new List<KeyValuePair<string, string>>();

        // Add discovered SSH key paths
        foreach (var keyPath in sshConfig.AvailableKeyPaths)
        {
            var keyName = Path.GetFileName(keyPath);
            sshKeyOptions.Add(new KeyValuePair<string, string>(keyPath, keyName));
        }

        // Add password authentication option
        sshKeyOptions.Add(new KeyValuePair<string, string>("", "Password Authentication (no key)"));

        var inputs = new InteractionInput[]
        {
            new() {
                InputType = targetHostOptions.Count > 1 ? InputType.Choice : InputType.Text,
                Label = "Target Server Host",
                Placeholder = targetHostOptions.Count > 1 ? "Select target server or enter custom" : "Enter target server hostname or IP (e.g., 192.168.1.100)",
                Options = targetHostOptions.Count > 1 ? targetHostOptions : null,
                Value = !string.IsNullOrEmpty(configDefaults.SshHost) ? configDefaults.SshHost :
                        (targetHostOptions.Count > 1 && targetHostOptions.Any(h => h.Key != "CUSTOM") ? targetHostOptions.First(h => h.Key != "CUSTOM").Key : "")
            },
            new() { InputType = InputType.Text, Label = "Custom Host", Placeholder = "Enter custom hostname or IP (only if 'Enter custom host...' selected above)" },
            new() {
                Required = true,
                InputType = InputType.Text,
                Label = $"SSH Username",
                Placeholder = $"Enter SSH username (detected: {sshConfig.DefaultUsername})",
                Value = !string.IsNullOrEmpty(configDefaults.SshUsername) ? configDefaults.SshUsername : sshConfig.DefaultUsername
            },
            new() { InputType = InputType.SecretText, Label = "SSH Password", Placeholder = "Enter SSH password (leave blank for key-based auth)" },
            new() {
                InputType = InputType.Choice,
                Label = "SSH Authentication Method",
                Placeholder = "Select SSH authentication method",
                Value = !string.IsNullOrEmpty(configDefaults.SshKeyPath) ? configDefaults.SshKeyPath :
                        (sshConfig.DefaultKeyPath ?? ""),
                Options = sshKeyOptions
            },
            new() {
                InputType = InputType.Text,
                Label = "SSH Port",
                Placeholder = "SSH port",
                Value = !string.IsNullOrEmpty(configDefaults.SshPort) && configDefaults.SshPort != "22" ? configDefaults.SshPort : "22"
            },
            new() {
                InputType = InputType.Text,
                Label = "Remote Deploy Path",
                Placeholder = "Remote deployment directory",
                Value = !string.IsNullOrEmpty(configDefaults.RemoteDeployPath) ? configDefaults.RemoteDeployPath : sshConfig.DefaultDeployPath
            }
        };

        var result = await interactionService.PromptInputsAsync(
            "Deploying to VM with Docker via SSH",
            "Please provide the deployment configuration for Docker SSH deployment.",
            inputs
        );

        if (result.Canceled)
        {
            return;
        }

        var selectedHost = result.Data[0].Value ?? "";
        var customHost = result.Data[1].Value ?? "";
        var sshUsername = result.Data[2].Value ?? throw new InvalidOperationException("SSH username is required");
        var sshPassword = result.Data[3].Value;
        var selectedKeyPath = result.Data[4].Value ?? "";
        var sshPort = result.Data[5].Value ?? "22";
        var remoteDeployPath = result.Data[6].Value ?? $"/home/{sshUsername}/aspire-app";

        // Process target host selection
        string targetHost;
        if (selectedHost == "CUSTOM")
        {
            targetHost = customHost;
        }
        else if (!string.IsNullOrEmpty(selectedHost))
        {
            targetHost = selectedHost;
        }
        else
        {
            // Fallback for text input mode
            targetHost = selectedHost;
        }

        if (string.IsNullOrEmpty(targetHost))
        {
            throw new InvalidOperationException("Target server host is required");
        }

        // Process SSH key selection
        string? sshKeyPath = null;
        if (selectedKeyPath == "CUSTOM")
        {
            // User wants to specify custom key path - would need additional input field
            // For now, leave as null to require password auth
            sshKeyPath = null;
        }
        else if (!string.IsNullOrEmpty(selectedKeyPath))
        {
            // User selected a discovered key (selectedKeyPath is the actual path)
            sshKeyPath = selectedKeyPath;
        }
        // Empty selectedKeyPath means password authentication (sshKeyPath remains null)

        // Validate SSH authentication method
        if (string.IsNullOrEmpty(sshPassword) && string.IsNullOrEmpty(sshKeyPath))
        {
            throw new InvalidOperationException("Either SSH password or SSH private key path must be provided");
        }

        // Step 1: Verify deployment files exist
        await using var verifyStep = await context.ProgressReporter.CreateStepAsync("Verify deployment files", context.CancellationToken);

        try
        {
            await using var verifyTask = await verifyStep.CreateTaskAsync("Checking for required files", context.CancellationToken);

            var envFileExists = await VerifyDeploymentFiles(context);
            if (envFileExists)
            {
                await verifyTask.SucceedAsync("docker-compose.yml and .env found");
                await verifyStep.SucceedAsync("Deployment files verified successfully");
            }
            else
            {
                await verifyTask.WarnAsync("docker-compose.yml found, .env file missing but optional");
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
            await EstablishAndTestSSHConnection(targetHost, sshUsername, sshPassword, sshKeyPath, sshPort, sshTestStep, context.CancellationToken);
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
            await PrepareRemoteEnvironment(remoteDeployPath, prepareStep, context.CancellationToken);
            await prepareStep.SucceedAsync("Remote environment ready for deployment");
        }
        catch (Exception ex)
        {
            await prepareStep.FailAsync($"Failed to prepare remote environment: {ex.Message}");
            throw;
        }

        await MergeAndUpdateEnvironmentFile(remoteDeployPath, imageTags, context, interactionService, context.CancellationToken);

        // Step 5: Transfer files to target server
        await using var transferStep = await context.ProgressReporter.CreateStepAsync("Transfer deployment files", context.CancellationToken);

        try
        {
            await TransferDeploymentFiles(remoteDeployPath, context, transferStep, context.CancellationToken);
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
            var deploymentInfo = await DeployOnRemoteServer(remoteDeployPath, imageTags, deployStep, context.CancellationToken);
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
        var dockerTask = CheckDockerAvailability(prerequisiteStep, context.CancellationToken);
        var dockerComposeTask = CheckDockerCompose(prerequisiteStep, context.CancellationToken);

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

    private static async Task CheckDockerAvailability(PublishingStep step, CancellationToken cancellationToken)
    {
        await using var task = await step.CreateTaskAsync("Checking Docker availability", cancellationToken);

        try
        {
            await task.UpdateAsync("Checking Docker client version...", cancellationToken);

            // Check Docker version
            var versionResult = await ExecuteDockerCommand("--version", cancellationToken);

            if (versionResult.ExitCode != 0)
            {
                await task.FailAsync("Docker is not installed or not in PATH", cancellationToken);
                throw new InvalidOperationException("Docker is required for this deployment");
            }

            await task.UpdateAsync("Checking Docker daemon connectivity...", cancellationToken);

            // Check Docker daemon connectivity
            var infoResult = await ExecuteDockerCommand("info --format '{{.ServerVersion}}'", cancellationToken);

            if (infoResult.ExitCode == 0 && !string.IsNullOrEmpty(infoResult.Output.Trim()))
            {
                var clientVersion = versionResult.Output.Split(',').FirstOrDefault()?.Trim() ?? "Unknown version";
                var serverVersion = infoResult.Output.Trim();
                await task.SucceedAsync($"Docker is available - Client: {clientVersion}, Server: {serverVersion}", cancellationToken: cancellationToken);
            }
            else
            {
                await task.FailAsync("Docker daemon is not running or not accessible", cancellationToken);
                throw new InvalidOperationException("Docker daemon must be running for this deployment");
            }
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            await task.FailAsync($"Docker check failed: {ex.Message}", cancellationToken);
            throw new InvalidOperationException("Docker is required for this deployment", ex);
        }
    }

    private static async Task CheckDockerCompose(PublishingStep step, CancellationToken cancellationToken)
    {
        await using var task = await step.CreateTaskAsync("Checking Docker Compose availability", cancellationToken);

        try
        {
            var result = await ExecuteDockerCommand("compose version", cancellationToken);

            if (result.ExitCode == 0 && !string.IsNullOrEmpty(result.Output))
            {
                // Extract version info from output
                var versionLine = result.Output.Split('\n').FirstOrDefault()?.Trim();
                await task.SucceedAsync($"Docker Compose is available: {versionLine}", cancellationToken: cancellationToken);
            }
            else
            {
                // Try legacy docker-compose command
                var legacyResult = await ExecuteDockerComposeCommand("--version", cancellationToken);

                if (legacyResult.ExitCode == 0 && !string.IsNullOrEmpty(legacyResult.Output))
                {
                    var versionLine = legacyResult.Output.Split('\n').FirstOrDefault()?.Trim();
                    await task.SucceedAsync($"Docker Compose (legacy) is available: {versionLine}", cancellationToken: cancellationToken);
                }
                else
                {
                    await task.FailAsync("Docker Compose is not available (neither 'docker compose' nor 'docker-compose')", cancellationToken);
                    throw new InvalidOperationException("Docker Compose is required for this deployment");
                }
            }
        }
        catch (Exception ex)
        {
            await task.FailAsync($"Docker Compose check failed: {ex.Message}", cancellationToken);
            throw new InvalidOperationException("Docker Compose is required for this deployment", ex);
        }
    }

    private Task<SSHConfiguration> DiscoverSSHConfiguration(DeployingContext context)
    {
        var config = new SSHConfiguration();

        // Try to detect default SSH username from environment
        var currentUser = Environment.UserName;
        config.DefaultUsername = "root";
        // Look for SSH keys and known hosts in common locations
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDir = Path.Combine(homeDir, ".ssh");

        if (Directory.Exists(sshDir))
        {
            // Discover all SSH keys in the .ssh directory
            var commonKeyFiles = new[] { "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa" };

            // First, add common key files in preferred order
            var orderedKeyFiles = new List<string>();
            foreach (var commonKey in commonKeyFiles)
            {
                var keyPath = Path.Combine(sshDir, commonKey);
                if (File.Exists(keyPath))
                {
                    orderedKeyFiles.Add(keyPath);
                }
            }

            // Then add any other SSH-like keys that aren't in the common list
            var otherKeyFiles = Directory.GetFiles(sshDir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).EndsWith(".pub") && !Path.GetFileName(f).EndsWith(".ppk"))
                .Where(f => !commonKeyFiles.Contains(Path.GetFileName(f)) && IsLikelySSHKey(f))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            orderedKeyFiles.AddRange(otherKeyFiles);

            foreach (var keyPath in orderedKeyFiles)
            {
                try
                {
                    // Just add the path if the file exists and is readable
                    if (File.Exists(keyPath))
                    {
                        config.AvailableKeyPaths.Add(keyPath);
                    }
                }
                catch
                {
                    // Skip keys that can't be read
                    continue;
                }
            }

            // Set default key path to the first discovered key
            if (config.AvailableKeyPaths.Any())
            {
                config.DefaultKeyPath = config.AvailableKeyPaths.First();
            }

            // Discover known hosts from SSH known_hosts file
            var knownHostsPath = Path.Combine(sshDir, "known_hosts");
            if (File.Exists(knownHostsPath))
            {
                ParseKnownHostsFile(knownHostsPath, config.KnownHosts);
            }
        }

        config.DefaultDeployPath = $"/home/{config.DefaultUsername}/aspire-app";
        return Task.FromResult(config);

        // Local function to parse SSH known_hosts file
        // The known_hosts file format contains one host per line with format:
        // hostname[,hostname...] [hostkey_algorithm] [base64_key] [optional_comment]
        // Lines can also start with @ for special entries or be hashed with |1| prefix
        // We extract unique hostnames while handling multiple hosts per line and port specifications
        void ParseKnownHostsFile(string filePath, List<string> knownHosts)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                var hostSet = new HashSet<string>();

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    // Skip empty lines, comments, hashed entries, and special entries
                    if (string.IsNullOrEmpty(trimmedLine) ||
                        trimmedLine.StartsWith("#") ||
                        trimmedLine.StartsWith("|1|") ||
                        trimmedLine.StartsWith("@"))
                    {
                        continue;
                    }

                    // Split on whitespace to get the host part (first field)
                    var parts = trimmedLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    var hostPart = parts[0];

                    // Handle multiple hostnames separated by commas
                    var hosts = hostPart.Split(',');
                    foreach (var host in hosts)
                    {
                        var cleanHost = host.Trim();
                        if (string.IsNullOrEmpty(cleanHost)) continue;

                        // Remove port specification (e.g., [hostname]:port -> hostname)
                        if (cleanHost.StartsWith("[") && cleanHost.Contains("]:"))
                        {
                            var endBracket = cleanHost.IndexOf("]:");
                            if (endBracket > 1)
                            {
                                cleanHost = cleanHost[1..endBracket];
                            }
                        }

                        // Skip if still contains problematic characters (likely hashed or malformed)
                        if (cleanHost.Contains("|") || cleanHost.Contains("=")) continue;

                        hostSet.Add(cleanHost);
                    }
                }

                // Add unique hosts to the known hosts list
                knownHosts.AddRange(hostSet.OrderBy(h => h));
            }
            catch (Exception ex)
            {
                // Log error but don't fail the entire discovery process
                Console.WriteLine($"Warning: Could not parse known_hosts file: {ex.Message}");
            }
        }
    }

    private static Task<bool> VerifyDeploymentFiles(DeployingContext context)
    {
        if (string.IsNullOrEmpty(context.OutputPath))
        {
            throw new InvalidOperationException("Deployment output path not available");
        }

        // Check for both .yml and .yaml extensions
        var dockerComposePathYml = Path.Combine(context.OutputPath, "docker-compose.yml");
        var dockerComposePathYaml = Path.Combine(context.OutputPath, "docker-compose.yaml");
        var envPath = Path.Combine(context.OutputPath, ".env");

        if (!File.Exists(dockerComposePathYml) && !File.Exists(dockerComposePathYaml))
        {
            throw new InvalidOperationException($"docker-compose.yml or docker-compose.yaml not found in {context.OutputPath}");
        }

        // .env file is optional - return whether it exists
        var envExists = File.Exists(envPath);
        return Task.FromResult(envExists);
    }


    private async Task PrepareRemoteEnvironment(string deployPath, PublishingStep step, CancellationToken cancellationToken)
    {
        await using var createDirTask = await step.CreateTaskAsync("Creating deployment directory", cancellationToken);

        await createDirTask.UpdateAsync("Creating deployment directory...", cancellationToken);

        // Create deployment directory
        await ExecuteSSHCommand($"mkdir -p {deployPath}", cancellationToken);

        await createDirTask.SucceedAsync($"Directory created: {deployPath}", cancellationToken: cancellationToken);

        await using var dockerCheckTask = await step.CreateTaskAsync("Verifying Docker installation", cancellationToken);

        await dockerCheckTask.UpdateAsync("Checking Docker installation...", cancellationToken);

        // Check if Docker is installed and get version info
        var dockerVersionCheck = await ExecuteSSHCommandWithOutput("docker --version", cancellationToken);
        if (dockerVersionCheck.ExitCode != 0)
        {
            await dockerCheckTask.FailAsync($"Docker is not installed on the target server. Error: {dockerVersionCheck.Error}\nCommand: docker --version\nOutput: {dockerVersionCheck.Output}", cancellationToken);
            throw new InvalidOperationException($"Docker is not installed on the target server. Error: {dockerVersionCheck.Error}");
        }

        await dockerCheckTask.UpdateAsync($"Docker version: {dockerVersionCheck.Output.Trim()}", cancellationToken);

        await dockerCheckTask.UpdateAsync("Verifying Docker daemon status...", cancellationToken);

        // Check if Docker daemon is running
        var dockerInfoCheck = await ExecuteSSHCommandWithOutput("docker info --format '{{.ServerVersion}}' 2>/dev/null", cancellationToken);
        if (dockerInfoCheck.ExitCode != 0)
        {
            await dockerCheckTask.FailAsync($"Docker daemon is not running on the target server. Error: {dockerInfoCheck.Error}\nCommand: docker info --format '{{{{.ServerVersion}}}}' 2>/dev/null\nOutput: {dockerInfoCheck.Output}", cancellationToken);
            throw new InvalidOperationException($"Docker daemon is not running on the target server. Error: {dockerInfoCheck.Error}");
        }

        await dockerCheckTask.UpdateAsync($"Docker daemon version: {dockerInfoCheck.Output.Trim()}", cancellationToken);

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

        await permissionsTask.UpdateAsync($"Docker permissions check: {dockerPermCheck.Output.Trim()}", cancellationToken);

        await permissionsTask.UpdateAsync("Checking for existing containers...", cancellationToken);

        // Check if there are any existing containers that might conflict
        var existingContainersCheck = await ExecuteSSHCommandWithOutput($"cd {deployPath} 2>/dev/null && (docker compose ps -q 2>/dev/null || docker-compose ps -q 2>/dev/null) | wc -l || echo '0'", cancellationToken);
        var existingContainers = existingContainersCheck.Output?.Trim() ?? "0";

        if (existingContainers != "0" && existingContainers != "")
        {
            await permissionsTask.UpdateAsync($"Found {existingContainers} existing containers (will be stopped during deployment)", cancellationToken);
        }
        else
        {
            await permissionsTask.UpdateAsync("No existing containers found", cancellationToken);
        }

        await permissionsTask.SucceedAsync($"Permissions and resources validated. Existing containers: {existingContainers}", cancellationToken: cancellationToken);
    }

    private async Task TransferDeploymentFiles(string deployPath, DeployingContext context, PublishingStep step, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.OutputPath))
        {
            throw new InvalidOperationException("Deployment output path not available");
        }

        await using var scanTask = await step.CreateTaskAsync("Scanning files for transfer", cancellationToken);

        await scanTask.UpdateAsync("Scanning files for transfer...", cancellationToken);

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

        await verifyTask.UpdateAsync("Checking transferred files...", cancellationToken);

        foreach (var file in transferredFiles)
        {
            var remotePath = $"{deployPath}/{file}";
            await verifyTask.UpdateAsync($"Verifying {file}...", cancellationToken);

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

                await verifyTask.UpdateAsync($"✓ {file} verified - {localFileInfo.Length} bytes", cancellationToken);
            }
            else
            {
                // Fallback: just check file exists with a simpler test
                var existsResult = await ExecuteSSHCommandWithOutput($"test -f '{remotePath}' && echo 'EXISTS' || echo 'NOT_FOUND'", cancellationToken);
                if (existsResult.Output.Trim() == "EXISTS")
                {
                    await verifyTask.UpdateAsync($"✓ {file} verified - file exists", cancellationToken);
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

        await summaryTask.UpdateAsync("Listing deployment directory contents...", cancellationToken);

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

    private async Task<string> DeployOnRemoteServer(string deployPath, Dictionary<string, string> imageTags, PublishingStep step, CancellationToken cancellationToken)
    {
        await using var stopTask = await step.CreateTaskAsync("Stopping existing containers", cancellationToken);

        await stopTask.UpdateAsync("Checking existing containers...", cancellationToken);

        // Check if any containers are currently running in this deployment
        var existingCheck = await ExecuteSSHCommandWithOutput(
            $"cd {deployPath} && (docker compose ps -q || docker-compose ps -q || true) 2>/dev/null | wc -l", cancellationToken);

        await stopTask.UpdateAsync($"Found {existingCheck.Output.Trim()} existing containers", cancellationToken);

        await stopTask.UpdateAsync("Stopping existing containers...", cancellationToken);

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

        await using var healthTask = await step.CreateTaskAsync("Waiting for services to be ready", cancellationToken);

        await healthTask.UpdateAsync("Waiting for services to become ready...", cancellationToken);

        // Wait for services to be ready with timeout
        var healthyServices = 0;
        var maxWaitTime = TimeSpan.FromMinutes(5);
        var checkInterval = TimeSpan.FromSeconds(10);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            await Task.Delay(checkInterval, cancellationToken);

            await healthTask.UpdateAsync($"Checking service health... ({(DateTime.UtcNow - startTime).TotalSeconds:F0}s elapsed)", cancellationToken);

            var healthCheck = await ExecuteSSHCommandWithOutput(
                $"cd {deployPath} && (docker compose ps --filter status=running || docker-compose ps --filter status=running || true)", cancellationToken);

            if (healthCheck.ExitCode == 0)
            {
                var runningContainers = healthCheck.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Contains("Up") || line.Contains("running"))
                    .Count();

                if (runningContainers > 0)
                {
                    healthyServices = runningContainers;
                    await healthTask.UpdateAsync($"Found {runningContainers} healthy containers:\nCommand: cd {deployPath} && (docker compose ps --filter status=running || docker-compose ps --filter status=running || true)\n{healthCheck.Output.Trim()}", cancellationToken);
                    break;
                }
            }
        }

        await healthTask.UpdateAsync("Gathering deployment information...", cancellationToken);

        // Get final service status
        var statusResult = await ExecuteSSHCommandWithOutput(
            $"cd {deployPath} && (docker compose ps || docker-compose ps)", cancellationToken);

        // Try to extract port information
        var portsInfo = await ExtractPortInformation(deployPath, cancellationToken);

        await healthTask.SucceedAsync($"Services are healthy and ready\nCommand: cd {deployPath} && (docker compose ps || docker-compose ps)\nFinal status:\n{statusResult.Output.Trim()}", cancellationToken: cancellationToken);

        return $"Services running: {healthyServices} containers healthy. {portsInfo}";
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

    private async Task<string> ExtractPortInformation(string deployPath, CancellationToken cancellationToken)
    {
        // Try to get port mappings from running containers
        var portResult = await ExecuteSSHCommandWithOutput(
            $"cd {deployPath} && docker ps --format 'table {{{{.Names}}}}\\t{{{{.Ports}}}}' --filter label=com.docker.compose.project", cancellationToken);

        if (!string.IsNullOrEmpty(portResult.Output))
        {
            var portMappings = portResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1) // Skip header
                .Where(line => line.Contains("->"))
                .SelectMany(line =>
                {
                    var parts = line.Trim().Split('\t');
                    if (parts.Length >= 2)
                    {
                        var portsPart = parts[1];
                        return portsPart.Split(',')
                            .Where(p => p.Contains("->"))
                            .Select(p => p.Trim().Split("->")[0].Split(':').LastOrDefault())
                            .Where(p => !string.IsNullOrEmpty(p));
                    }
                    return [];
                })
                .Distinct()
                .Where(p => int.TryParse(p, out _));

            if (portMappings.Any())
            {
                // For port URLs, we need the host information from the SSH connection
                if (_sshClient?.ConnectionInfo?.Host != null)
                {
                    return $"Accessible URLs: {string.Join(", ", portMappings.Select(p => $"http://{_sshClient.ConnectionInfo.Host}:{p}"))}";
                }
                else
                {
                    return $"Accessible ports: {string.Join(", ", portMappings)}";
                }
            }
        }

        // Fallback: try to extract from docker-compose.yml or docker-compose.yaml
        var composePortResult = await ExecuteSSHCommandWithOutput(
            $"cd {deployPath} && (grep -E '^\\s*-\\s*[\"']*[0-9]+:[0-9]+[\"']*' docker-compose.yml || grep -E '^\\s*-\\s*[\"']*[0-9]+:[0-9]+[\"']*' docker-compose.yaml) 2>/dev/null | sed 's/.*\"\\([0-9]*\\):.*/\\1/' | sort -u", cancellationToken);

        if (!string.IsNullOrEmpty(composePortResult.Output))
        {
            var ports = composePortResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => int.TryParse(p.Trim(), out _))
                .Distinct();

            if (ports.Any())
            {
                if (_sshClient?.ConnectionInfo?.Host != null)
                {
                    return $"Configured ports: {string.Join(", ", ports.Select(p => $"http://{_sshClient.ConnectionInfo.Host}:{p}"))}";
                }
                else
                {
                    return $"Configured ports: {string.Join(", ", ports)}";
                }
            }
        }

        return "Port information not available";
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

    private async Task<List<EnvironmentVariable>> ScanEnvironmentFile(DeployingContext context)
    {
        var missingVars = new List<EnvironmentVariable>();

        if (string.IsNullOrEmpty(context.OutputPath))
        {
            return missingVars;
        }

        var envPath = Path.Combine(context.OutputPath, ".env");
        if (!File.Exists(envPath))
        {
            return missingVars;
        }

        var envLines = await File.ReadAllLinesAsync(envPath);

        foreach (var line in envLines)
        {
            var trimmedLine = line.Trim();

            // Skip comments and empty lines
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
            {
                continue;
            }

            // Parse KEY=VALUE format
            var equalIndex = trimmedLine.IndexOf('=');
            if (equalIndex > 0)
            {
                var key = trimmedLine[..equalIndex].Trim();
                var value = trimmedLine[(equalIndex + 1)..].Trim();

                // Check if value is empty or placeholder
                if (string.IsNullOrEmpty(value) ||
                    value == "\"\"" ||
                    value == "''" ||
                    value.Contains("CHANGE_ME") ||
                    value.Contains("TODO") ||
                    value.Contains("PLACEHOLDER") ||
                    value.Contains("REPLACE_ME") ||
                    value.Contains("YOUR_") ||
                    value.Contains("ENTER_") ||
                    value.Contains("INSERT_") ||
                    value == "null" ||
                    value == "undefined" ||
                    value.StartsWith("<") && value.EndsWith(">")) // e.g., <your_api_key>
                {
                    var envVar = new EnvironmentVariable
                    {
                        Key = key,
                        CurrentValue = value,
                        IsSensitive = IsSensitiveEnvironmentVariable(key)
                    };

                    missingVars.Add(envVar);
                }
            }
        }

        return missingVars;
    }

    private static async Task UpdateEnvironmentFile(DeployingContext context, List<EnvironmentVariable> envVars, string[] newValues)
    {
        if (string.IsNullOrEmpty(context.OutputPath))
        {
            throw new InvalidOperationException("Output path not available");
        }

        var envPath = Path.Combine(context.OutputPath, ".env");
        if (!File.Exists(envPath))
        {
            throw new InvalidOperationException(".env file not found");
        }

        var envLines = await File.ReadAllLinesAsync(envPath);
        var updatedLines = new List<string>();

        // Create a lookup of key to new value
        var updates = envVars.Zip(newValues, (envVar, newValue) => new { envVar.Key, Value = newValue })
                            .ToDictionary(x => x.Key, x => x.Value);

        foreach (var line in envLines)
        {
            var trimmedLine = line.Trim();

            // Skip comments and empty lines - keep as is
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
            {
                updatedLines.Add(line);
                continue;
            }

            // Parse KEY=VALUE format
            var equalIndex = trimmedLine.IndexOf('=');
            if (equalIndex > 0)
            {
                var key = trimmedLine[..equalIndex].Trim();

                if (updates.ContainsKey(key))
                {
                    // Update with new value, preserving any quotes if they were there
                    var newValue = updates[key];
                    var originalValue = trimmedLine[(equalIndex + 1)..].Trim();

                    // Preserve quote style from original
                    if ((originalValue.StartsWith("\"") && originalValue.EndsWith("\"")) ||
                        (originalValue.StartsWith("'") && originalValue.EndsWith("'")))
                    {
                        var quote = originalValue[0];
                        newValue = $"{quote}{newValue}{quote}";
                    }

                    updatedLines.Add($"{key}={newValue}");
                }
                else
                {
                    updatedLines.Add(line);
                }
            }
            else
            {
                updatedLines.Add(line);
            }
        }

        await File.WriteAllLinesAsync(envPath, updatedLines);
    }

    private static bool IsSensitiveEnvironmentVariable(string key)
    {
        var sensitiveKeywords = new[]
        {
            "PASSWORD", "SECRET", "KEY", "TOKEN", "API_KEY", "PRIVATE",
            "CERT", "CREDENTIAL", "AUTH", "PASS", "PWD"
        };

        return sensitiveKeywords.Any(keyword => key.Contains(keyword, StringComparison.InvariantCultureIgnoreCase));
    }

    private static bool IsLikelySSHKey(string filePath)
    {
        try
        {
            // Check file size (SSH keys are typically between 100 bytes and 10KB)
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < 100 || fileInfo.Length > 10240)
                return false;

            // Read first few lines to check for SSH key headers
            var firstLines = File.ReadLines(filePath).Take(3).ToArray();
            if (firstLines.Length == 0)
                return false;

            var firstLine = firstLines[0].Trim();

            // Check for common SSH key headers
            var sshKeyHeaders = new[]
            {
                "-----BEGIN OPENSSH PRIVATE KEY-----",
                "-----BEGIN RSA PRIVATE KEY-----",
                "-----BEGIN DSA PRIVATE KEY-----",
                "-----BEGIN EC PRIVATE KEY-----",
                "-----BEGIN PRIVATE KEY-----"
            };

            return sshKeyHeaders.Any(header => firstLine.StartsWith(header, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private async Task EstablishAndTestSSHConnection(string host, string username, string? password, string? keyPath, string port, PublishingStep step, CancellationToken cancellationToken)
    {
        Console.WriteLine("[DEBUG] Establishing SSH connection using SSH.NET");

        // Task 1: Establish SSH connection
        await using var connectTask = await step.CreateTaskAsync("Establishing SSH connection", cancellationToken);

        try
        {
            await connectTask.UpdateAsync("Creating SSH and SCP connections...", cancellationToken);

            _sshClient = await CreateSSHClient(host, username, password, keyPath, port, cancellationToken);
            _scpClient = await CreateSCPClient(host, username, password, keyPath, port, cancellationToken);

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

        await testTask.UpdateAsync("Testing basic SSH connectivity...", cancellationToken);

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

        await verifyTask.UpdateAsync("Testing remote system access...", cancellationToken);

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

    private static async Task<SshClient> CreateSSHClient(string host, string username, string? password, string? keyPath, string port, CancellationToken cancellationToken)
    {
        var connectionInfo = CreateConnectionInfo(host, username, password, keyPath, port);
        var client = new SshClient(connectionInfo);

        await client.ConnectAsync(cancellationToken);

        if (!client.IsConnected)
        {
            client.Dispose();
            throw new InvalidOperationException("Failed to establish SSH connection");
        }

        return client;
    }

    private static async Task<ScpClient> CreateSCPClient(string host, string username, string? password, string? keyPath, string port, CancellationToken cancellationToken)
    {
        var connectionInfo = CreateConnectionInfo(host, username, password, keyPath, port);
        var client = new ScpClient(connectionInfo);

        await client.ConnectAsync(cancellationToken);

        if (!client.IsConnected)
        {
            client.Dispose();
            throw new InvalidOperationException("Failed to establish SCP connection");
        }

        return client;
    }

    private static ConnectionInfo CreateConnectionInfo(string host, string username, string? password, string? keyPath, string port)
    {
        var portInt = int.Parse(port);

        if (!string.IsNullOrEmpty(keyPath))
        {
            // Use key-based authentication
            var keyFile = new PrivateKeyFile(keyPath, password ?? "");
            return new ConnectionInfo(host, portInt, username, new PrivateKeyAuthenticationMethod(username, keyFile));
        }
        else if (!string.IsNullOrEmpty(password))
        {
            // Use password authentication
            return new ConnectionInfo(host, portInt, username, new PasswordAuthenticationMethod(username, password));
        }
        else
        {
            throw new InvalidOperationException("Either SSH password or SSH private key path must be provided");
        }
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
                Placeholder = "Enter registry URL (e.g., docker.io, ghcr.io, your-registry.com:5000)",
                Value = !string.IsNullOrEmpty(configDefaults.RegistryUrl) ? configDefaults.RegistryUrl : "docker.io"
            },
            new() {
                InputType = InputType.Text,
                Label = "Image Repository Prefix",
                Placeholder = "Optional: Enter repository prefix (e.g., mycompany/myproject)",
                Value = configDefaults.RepositoryPrefix
            },
            new() {
                InputType = InputType.Text,
                Label = "Registry Username",
                Placeholder = "Enter your registry username",
                Value = configDefaults.RegistryUsername
            },
            new() {
                InputType = InputType.SecretText,
                Label = "Registry Password/Token",
                Placeholder = "Enter your registry password or access token"
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

                await loginTask.UpdateAsync($"Logging into registry {registryUrl}...", cancellationToken);

                try
                {
                    var loginResult = await ExecuteDockerLogin(registryUrl, registryUsername, registryPassword, cancellationToken);

                    if (loginResult.ExitCode != 0)
                    {
                        await loginTask.FailAsync($"Docker login failed: {loginResult.Error}\nOutput: {loginResult.Output}", cancellationToken);
                        throw new InvalidOperationException($"Docker login failed: {loginResult.Error}");
                    }

                    await loginTask.SucceedAsync($"Successfully authenticated with {registryUrl}", cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (!(ex is InvalidOperationException))
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

                await tagTask.UpdateAsync($"Tagging image for service: {serviceName}...", cancellationToken);

                // Construct the target image name
                var targetImageName = !string.IsNullOrEmpty(repositoryPrefix)
                    ? $"{registryUrl}/{repositoryPrefix}/{serviceName}:{imageTag}"
                    : $"{registryUrl}/{serviceName}:{imageTag}";

                // Tag the image
                var tagResult = await ExecuteDockerCommand($"tag {imageName} {targetImageName}", cancellationToken);

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

                await pushTask.UpdateAsync($"Pushing {serviceName} image to registry...", cancellationToken);

                var pushResult = await ExecuteDockerCommand($"push {targetImageName}", cancellationToken);

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
        var localEnvVars = await ReadEnvironmentFile(localEnvPath);
        
        // Step 2: Get remote environment file (if it exists)
        var remoteEnvVars = new Dictionary<string, string>();
        var remoteEnvPath = $"{remoteDeployPath}/.env";
        
        try
        {
            var remoteEnvResult = await ExecuteSSHCommandWithOutput($"cat '{remoteEnvPath}' 2>/dev/null || echo 'FILE_NOT_FOUND'", cancellationToken);
            if (remoteEnvResult.ExitCode == 0 && !remoteEnvResult.Output.Contains("FILE_NOT_FOUND"))
            {
                remoteEnvVars = ParseEnvironmentContent(remoteEnvResult.Output);
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
            var isSensitive = IsSensitiveEnvironmentVariable(key);
            
            envInputs.Add(new InteractionInput
            {
                InputType = isSensitive ? InputType.SecretText : InputType.Text,
                Label = $"Environment Variable: {key}",
                Placeholder = isImageVar ? "Container image (auto-populated from registry)" : $"Enter value for {key}",
                Value = value ?? "",
                Required = false // Allow empty values for optional variables
            });
        }

        if (envInputs.Count == 0)
        {
            return; // Nothing to configure
        }

        // Step 5: Prompt user for environment values
        var envResult = await interactionService.PromptInputsAsync(
            "Environment Configuration",
            "Please review and update the environment variables for deployment. Image variables have been automatically populated from the registry.",
            [.. envInputs],
            cancellationToken: cancellationToken
        );

        if (envResult.Canceled)
        {
            throw new InvalidOperationException("Environment configuration was canceled");
        }

        // Step 6: Prepare final environment content and write to remote
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

        // Create environment file content
        var envContent = string.Join("\n", finalEnvVars.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        
        // Write to remote environment file
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, envContent, cancellationToken);
            await TransferFile(tempFile, remoteEnvPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private async Task<Dictionary<string, string>> ReadEnvironmentFile(string filePath)
    {
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        if (!File.Exists(filePath))
        {
            return envVars;
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            // Skip comments and empty lines
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
            {
                continue;
            }

            // Parse KEY=VALUE format
            var equalIndex = trimmedLine.IndexOf('=');
            if (equalIndex > 0)
            {
                var key = trimmedLine[..equalIndex].Trim();
                var value = trimmedLine[(equalIndex + 1)..].Trim();
                
                // Remove quotes if present
                if ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                    (value.StartsWith("'") && value.EndsWith("'")))
                {
                    value = value[1..^1];
                }
                
                envVars[key] = value;
            }
        }

        return envVars;
    }

    private Dictionary<string, string> ParseEnvironmentContent(string content)
    {
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        if (string.IsNullOrEmpty(content))
        {
            return envVars;
        }

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            // Skip comments and empty lines
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
            {
                continue;
            }

            // Parse KEY=VALUE format
            var equalIndex = trimmedLine.IndexOf('=');
            if (equalIndex > 0)
            {
                var key = trimmedLine[..equalIndex].Trim();
                var value = trimmedLine[(equalIndex + 1)..].Trim();
                
                // Remove quotes if present
                if ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                    (value.StartsWith("'") && value.EndsWith("'")))
                {
                    value = value[1..^1];
                }
                
                envVars[key] = value;
            }
        }

        return envVars;
    }

    /// <summary>
    /// Generic helper method to execute any process with standard configuration
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> ExecuteProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Executing process: {fileName} {arguments}");
        var startTime = DateTime.UtcNow;

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var endTime = DateTime.UtcNow;
            var exitCode = process.ExitCode;
            Console.WriteLine($"[DEBUG] Process completed in {(endTime - startTime).TotalSeconds:F1}s, exit code: {exitCode}");

            if (exitCode != 0)
            {
                Console.WriteLine($"[DEBUG] Process error output: {error}");
            }

            return (exitCode, output, error);
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG] Process execution failed in {(endTime - startTime).TotalSeconds:F1}s: {ex.Message}");
            return (-1, "", ex.Message);
        }
    }

    /// <summary>
    /// Execute Docker commands with standardized error handling
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> ExecuteDockerCommand(string arguments, CancellationToken cancellationToken)
    {
        return await ExecuteProcessAsync("docker", arguments, cancellationToken);
    }

    /// <summary>
    /// Execute Docker Compose commands with standardized error handling
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> ExecuteDockerComposeCommand(string arguments, CancellationToken cancellationToken)
    {
        return await ExecuteProcessAsync("docker-compose", arguments, cancellationToken);
    }

    /// <summary>
    /// Execute Docker login with password via stdin
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> ExecuteDockerLogin(string registryUrl, string username, string password, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Executing Docker login to {registryUrl} with username {username}");
        var startTime = DateTime.UtcNow;

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"login {registryUrl} --username {username} --password-stdin",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.StandardInput.WriteLineAsync(password);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var endTime = DateTime.UtcNow;
            var exitCode = process.ExitCode;
            Console.WriteLine($"[DEBUG] Docker login completed in {(endTime - startTime).TotalSeconds:F1}s, exit code: {exitCode}");

            if (exitCode != 0)
            {
                Console.WriteLine($"[DEBUG] Docker login error output: {error}");
            }

            return (exitCode, output, error);
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG] Docker login failed in {(endTime - startTime).TotalSeconds:F1}s: {ex.Message}");
            return (-1, "", ex.Message);
        }
    }

    /// <summary>
    /// Reads configuration defaults from the DockerSSH section
    /// </summary>
    private DockerSSHConfiguration GetConfigurationDefaults(DeployingContext context)
    {
        var configuration = context.Services.GetService<IConfiguration>();
        var dockerSSHSection = configuration?.GetSection("DockerSSH");
        
        // Get SSH key path from configuration and resolve if it's just a key name
        var configuredKeyPath = dockerSSHSection?.GetValue<string>("SshKeyPath") ?? "";
        var resolvedKeyPath = ResolveSSHKeyPath(configuredKeyPath);
        
        return new DockerSSHConfiguration
        {
            // SSH Configuration defaults
            SshHost = dockerSSHSection?.GetValue<string>("SshHost") ?? "",
            SshUsername = dockerSSHSection?.GetValue<string>("SshUsername") ?? "",
            SshPort = dockerSSHSection?.GetValue<string>("SshPort") ?? "22",
            SshKeyPath = resolvedKeyPath,
            RemoteDeployPath = dockerSSHSection?.GetValue<string>("RemoteDeployPath") ?? "",
            
            // Docker Registry Configuration defaults
            RegistryUrl = dockerSSHSection?.GetValue<string>("RegistryUrl") ?? "docker.io",
            RepositoryPrefix = dockerSSHSection?.GetValue<string>("RepositoryPrefix") ?? "",
            RegistryUsername = dockerSSHSection?.GetValue<string>("RegistryUsername") ?? ""
        };
    }

    /// <summary>
    /// Resolves SSH key path from configuration. If the path is just a key name (no path separators),
    /// it assumes the key is in the ~/.ssh directory. Otherwise, returns the path as-is.
    /// </summary>
    private static string ResolveSSHKeyPath(string configuredKeyPath)
    {
        if (string.IsNullOrEmpty(configuredKeyPath))
        {
            return "";
        }

        // If the configured path contains path separators, treat it as a full path
        if (configuredKeyPath.Contains('/') || configuredKeyPath.Contains('\\') || configuredKeyPath.Contains(':'))
        {
            return configuredKeyPath;
        }

        // Otherwise, treat it as a key name and resolve to ~/.ssh/{keyname}
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDir = Path.Combine(homeDir, ".ssh");
        var resolvedPath = Path.Combine(sshDir, configuredKeyPath);

        // Only return the resolved path if the file actually exists
        return File.Exists(resolvedPath) ? resolvedPath : configuredKeyPath;
    }

    // ...existing code...
}

// Helper class for SSH configuration
class SSHConfiguration
{
    public string DefaultUsername { get; set; } = string.Empty;
    public string? DefaultKeyPath { get; set; }
    public string DefaultDeployPath { get; set; } = string.Empty;
    public List<string> AvailableKeyPaths { get; set; } = [];
    public List<string> KnownHosts { get; set; } = [];
}

// Helper class for environment variable information
class EnvironmentVariable
{
    public string Key { get; set; } = string.Empty;
    public string CurrentValue { get; set; } = string.Empty;
    public bool IsSensitive { get; set; }
    public string? Description { get; set; }
}

// Helper class for DockerSSH configuration defaults
class DockerSSHConfiguration
{
    // SSH Configuration
    public string SshHost { get; set; } = string.Empty;
    public string SshUsername { get; set; } = string.Empty;
    public string SshPort { get; set; } = "22";
    public string SshKeyPath { get; set; } = string.Empty;
    public string RemoteDeployPath { get; set; } = string.Empty;
    
    // Docker Registry Configuration
    public string RegistryUrl { get; set; } = "docker.io";
    public string RepositoryPrefix { get; set; } = string.Empty;
    public string RegistryUsername { get; set; } = string.Empty;
}
