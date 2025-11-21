#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Aspire.Hosting.Docker.Pipelines.Models;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.Pipelines.Services;

/// <summary>
/// Factory for creating and establishing SSH connections for remote operations.
/// Handles configuration discovery, user prompting, connection establishment, and state persistence.
/// </summary>
internal class SSHConnectionFactory
{
    private readonly SSHConfigurationDiscovery _sshConfigurationDiscovery;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<SSHConnectionFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<ILogger<SSHConnectionManager>, ISSHConnectionManager> _connectionManagerFactory;
    private const string SshContextKey = "DockerSSH";

    public SSHConnectionFactory(
        SSHConfigurationDiscovery sshConfigurationDiscovery,
        IFileSystem fileSystem,
        ILoggerFactory loggerFactory,
        Func<ILogger<SSHConnectionManager>, ISSHConnectionManager>? connectionManagerFactory = null)
    {
        _sshConfigurationDiscovery = sshConfigurationDiscovery;
        _fileSystem = fileSystem;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SSHConnectionFactory>();
        _connectionManagerFactory = connectionManagerFactory ?? (logger => new SSHConnectionManager(logger));
    }

    /// <summary>
    /// Creates and establishes an SSH connection, handling configuration discovery and user prompting.
    /// </summary>
    public async Task<ISSHConnectionManager> CreateConnectedManagerAsync(
        PipelineStepContext context,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();

        return await CreateConnectedManagerAsync(
            interactionService,
            configuration,
            deploymentStateManager,
            context,
            step,
            cancellationToken);
    }

    /// <summary>
    /// Creates and establishes an SSH connection (testable overload).
    /// </summary>
    internal async Task<ISSHConnectionManager> CreateConnectedManagerAsync(
        IInteractionService interactionService,
        IConfiguration configuration,
        IDeploymentStateManager deploymentStateManager,
        PipelineStepContext context,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Creating SSH connection");

        // Get SSH connection context (prompts user if needed)
        var sshContext = await GetOrPromptForSSHContextAsync(
            interactionService,
            configuration,
            context,
            cancellationToken);

        // Create and establish connection
        var manager = _connectionManagerFactory(_loggerFactory.CreateLogger<SSHConnectionManager>());
        await manager.EstablishConnectionAsync(sshContext, step, cancellationToken);

        // Persist SSH context for future runs
        await PersistSSHContextAsync(deploymentStateManager, sshContext, cancellationToken);

        return manager;
    }

    private async Task<SSHConnectionContext> GetOrPromptForSSHContextAsync(
        IInteractionService interactionService,
        IConfiguration configuration,
        PipelineStepContext context,
        CancellationToken cancellationToken)
    {
        // Try to load from configuration first
        var section = configuration.GetSection(SshContextKey);
        var targetHost = section[nameof(SSHConnectionContext.TargetHost)];
        var sshUsername = section[nameof(SSHConnectionContext.SshUsername)];
        var sshPort = section[nameof(SSHConnectionContext.SshPort)];
        var sshPassword = section[nameof(SSHConnectionContext.SshPassword)];
        var sshKeyPath = section[nameof(SSHConnectionContext.SshKeyPath)];

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
                SshPort = string.IsNullOrEmpty(sshPort) ? "22" : sshPort
            };
        }

        // Check if prompting is available
        if (!interactionService.IsAvailable)
        {
            var missing = new List<ConfigurationRequirement>();
            if (string.IsNullOrEmpty(targetHost)) missing.Add(new(SshContextKey, nameof(SSHConnectionContext.TargetHost)));
            if (string.IsNullOrEmpty(sshPassword) && string.IsNullOrEmpty(sshKeyPath))
            {
                missing.Add(new(SshContextKey, $"{nameof(SSHConnectionContext.SshKeyPath)} or {nameof(SSHConnectionContext.SshPassword)}"));
            }
            if (missing.Count > 0)
            {
                throw new ConfigurationRequiredException(SshContextKey, missing);
            }

            // Use defaults for optional values
            return new SSHConnectionContext
            {
                TargetHost = targetHost!,
                SshUsername = sshUsername ?? "root",
                SshPassword = string.IsNullOrEmpty(sshPassword) ? null : sshPassword,
                SshKeyPath = string.IsNullOrEmpty(sshKeyPath) ? null : sshKeyPath,
                SshPort = sshPort ?? "22"
            };
        }

        // Discover SSH configuration
        var sshConfig = _sshConfigurationDiscovery.DiscoverSSHConfiguration(context);

        // Prompt for configuration
        targetHost ??= await PromptForTargetHostAsync(interactionService);
        var sshKeyOptions = BuildSshKeyOptions(sshConfig);
        sshKeyPath ??= await PromptForSshKeyPathAsync(interactionService, sshKeyOptions, configuration, sshConfig, _fileSystem);
        var sshDetails = await PromptForSshDetailsAsync(interactionService, targetHost, sshKeyPath, configuration, sshConfig);

        // Validate SSH authentication method
        if (string.IsNullOrEmpty(sshDetails.SshPassword) && string.IsNullOrEmpty(sshKeyPath))
        {
            throw new InvalidOperationException("Either SSH password or SSH private key path must be provided");
        }

        return new SSHConnectionContext
        {
            TargetHost = targetHost,
            SshUsername = sshDetails.SshUsername,
            SshPassword = sshDetails.SshPassword,
            SshKeyPath = sshKeyPath,
            SshPort = sshDetails.SshPort
        };
    }

    private async Task PersistSSHContextAsync(
        IDeploymentStateManager deploymentStateManager,
        SSHConnectionContext sshContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var sshSection = await deploymentStateManager.AcquireSectionAsync(SshContextKey, cancellationToken);

            sshSection.Data[nameof(SSHConnectionContext.TargetHost)] = sshContext.TargetHost;
            sshSection.Data[nameof(SSHConnectionContext.SshUsername)] = sshContext.SshUsername;
            sshSection.Data[nameof(SSHConnectionContext.SshPassword)] = sshContext.SshPassword ?? "";
            sshSection.Data[nameof(SSHConnectionContext.SshKeyPath)] = sshContext.SshKeyPath ?? "";
            sshSection.Data[nameof(SSHConnectionContext.SshPort)] = sshContext.SshPort;

            await deploymentStateManager.SaveSectionAsync(sshSection, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist SSH context");
        }
    }

    private static List<KeyValuePair<string, string>> BuildSshKeyOptions(SSHConfiguration sshConfig)
    {
        var sshKeyOptions = new List<KeyValuePair<string, string>>();

        foreach (var keyPath in sshConfig.AvailableKeyPaths)
        {
            var keyName = Path.GetFileName(keyPath);
            sshKeyOptions.Add(new KeyValuePair<string, string>(keyPath, keyName));
        }

        sshKeyOptions.Add(new KeyValuePair<string, string>("", "Password Authentication (no key)"));
        return sshKeyOptions;
    }

    private static async Task<string> PromptForTargetHostAsync(IInteractionService interactionService)
    {
        var inputs = new InteractionInput[]
        {
            new() {
                Name = "targetHost",
                Required = true,
                InputType = InputType.Text,
                Label = "Target Server Host"
            }
        };

        var result = await interactionService.PromptInputsAsync(
            "Target Host Configuration",
            "Please enter the target server host for deployment.",
            inputs);

        if (result.Canceled)
        {
            throw new InvalidOperationException("Target host configuration was canceled");
        }

        return result.Data["targetHost"].Value ?? throw new InvalidOperationException("Target host is required");
    }

    private static async Task<string?> PromptForSshKeyPathAsync(
        IInteractionService interactionService,
        List<KeyValuePair<string, string>> sshKeyOptions,
        IConfiguration configuration,
        SSHConfiguration sshConfig,
        IFileSystem fileSystem)
    {
        // Read default SSH key path from configuration or use discovered default
        var configuredKeyPath = configuration["DockerSSH:SshKeyPath"];
        var defaultKeyPath = !string.IsNullOrEmpty(configuredKeyPath)
            ? ResolveSSHKeyPath(configuredKeyPath, fileSystem)
            : (sshConfig.DefaultKeyPath ?? "");

        var inputs = new InteractionInput[]
        {
            new() {
                Name = "sshKeyPath",
                InputType = InputType.Choice,
                AllowCustomChoice = true,
                Label = "SSH Authentication Method",
                Options = sshKeyOptions,
                Value = defaultKeyPath
            }
        };

        var result = await interactionService.PromptInputsAsync(
            "SSH Authentication Method",
            "Please select how you want to authenticate with the SSH server.",
            inputs);

        if (result.Canceled)
        {
            throw new InvalidOperationException("SSH authentication method selection was canceled");
        }

        var selectedKeyPath = result.Data[0].Value;
        return string.IsNullOrEmpty(selectedKeyPath) ? null : selectedKeyPath;
    }

    private static async Task<(string SshUsername, string? SshPassword, string SshPort)> PromptForSshDetailsAsync(
        IInteractionService interactionService,
        string targetHost,
        string? sshKeyPath,
        IConfiguration configuration,
        SSHConfiguration sshConfig)
    {
        // Read defaults from configuration
        var defaultUsername = configuration["DockerSSH:SshUsername"] ?? "root";
        var defaultPort = configuration["DockerSSH:SshPort"] ?? "22";

        var inputs = new InteractionInput[]
        {
            new() {
                Name = "sshUsername",
                Required = true,
                InputType = InputType.Text,
                Label = "SSH Username",
                Value = defaultUsername
            },
            new() {
                Name = "sshPassword",
                InputType = InputType.SecretText,
                Label = "SSH Password"
            },
            new() {
                Name = "sshPort",
                InputType = InputType.Text,
                Label = "SSH Port",
                Value = defaultPort
            }
        };

        var result = await interactionService.PromptInputsAsync(
            "SSH Configuration",
            "Please provide the SSH configuration details for the target server.\n",
            inputs);

        if (result.Canceled)
        {
            throw new InvalidOperationException("SSH configuration was canceled");
        }

        var sshUsername = result.Data["sshUsername"].Value ?? throw new InvalidOperationException("SSH username is required");
        var sshPassword = string.IsNullOrEmpty(result.Data["sshPassword"].Value) ? null : result.Data["sshPassword"].Value;
        var sshPort = result.Data["sshPort"].Value ?? "22";

        return (sshUsername, sshPassword, sshPort);
    }

    /// <summary>
    /// Resolves SSH key path from configuration. If the path is just a key name (no path separators),
    /// it assumes the key is in the ~/.ssh directory. Otherwise, returns the path as-is.
    /// </summary>
    internal static string? ResolveSSHKeyPath(string? configuredKeyPath, IFileSystem fileSystem)
    {
        if (string.IsNullOrEmpty(configuredKeyPath))
        {
            return null;
        }

        // If the path contains directory separators, assume it's a full path
        if (configuredKeyPath.Contains(Path.DirectorySeparatorChar) ||
            configuredKeyPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return configuredKeyPath;
        }

        // Otherwise, assume it's a key name in the ~/.ssh directory
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDir = Path.Combine(homeDir, ".ssh");
        var resolvedPath = Path.Combine(sshDir, configuredKeyPath);

        // Only return the resolved path if the file actually exists
        return fileSystem.FileExists(resolvedPath) ? resolvedPath : configuredKeyPath;
    }

}
