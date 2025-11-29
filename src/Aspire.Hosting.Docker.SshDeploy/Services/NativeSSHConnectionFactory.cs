#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// Factory for creating native SSH connections that leverage ssh-agent for authentication.
/// This is the default factory - it uses the system's native ssh command with ControlMaster
/// for connection reuse, and relies on ssh-agent for key management.
/// </summary>
internal class NativeSSHConnectionFactory : ISSHConnectionFactory
{
    private readonly IProcessExecutor _processExecutor;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<NativeSSHConnectionFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private const string SshContextKey = "DockerSSH";

    public NativeSSHConnectionFactory(
        IProcessExecutor processExecutor,
        IFileSystem fileSystem,
        ILoggerFactory loggerFactory)
    {
        _processExecutor = processExecutor;
        _fileSystem = fileSystem;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NativeSSHConnectionFactory>();
    }

    /// <summary>
    /// Creates and establishes an SSH connection using native ssh with ssh-agent.
    /// </summary>
    public async Task<ISSHConnectionManager> CreateConnectedManagerAsync(
        PipelineStepContext context,
        IReportingStep step,
        CancellationToken cancellationToken)
    {
        var interactionService = context.Services.GetRequiredService<IInteractionService>();
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();

        _logger.LogDebug("Creating native SSH connection (using ssh-agent for authentication)");

        // Get SSH connection context (simplified - no key/password prompts)
        var sshContext = await GetOrPromptForSSHContextAsync(
            interactionService,
            configuration,
            cancellationToken);

        // Create and establish connection
        var manager = new NativeSSHConnectionManager(
            _processExecutor,
            _fileSystem,
            _loggerFactory.CreateLogger<NativeSSHConnectionManager>());

        await manager.EstablishConnectionAsync(sshContext, step, cancellationToken);

        // Persist SSH context for future runs
        await PersistSSHContextAsync(deploymentStateManager, sshContext, cancellationToken);

        return manager;
    }

    private async Task<SSHConnectionContext> GetOrPromptForSSHContextAsync(
        IInteractionService interactionService,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Try to load from configuration first
        var section = configuration.GetSection(SshContextKey);
        var targetHost = section[nameof(SSHConnectionContext.TargetHost)];
        var sshUsername = section[nameof(SSHConnectionContext.SshUsername)];
        var sshPort = section[nameof(SSHConnectionContext.SshPort)];
        var connectTimeoutStr = section[nameof(SSHConnectionContext.ConnectTimeout)];
        var connectTimeout = int.TryParse(connectTimeoutStr, out var timeout) ? timeout : 120;

        // If we have host and username, we're good - ssh-agent handles auth
        if (!string.IsNullOrEmpty(targetHost) && !string.IsNullOrEmpty(sshUsername))
        {
            _logger.LogDebug("Using SSH configuration from settings: {User}@{Host}:{Port} (timeout: {Timeout}s)",
                sshUsername, targetHost, sshPort ?? "22", connectTimeout);

            return new SSHConnectionContext
            {
                TargetHost = targetHost,
                SshUsername = sshUsername,
                SshPassword = null,  // Not needed - using ssh-agent
                SshKeyPath = null,   // Not needed - using ssh-agent
                SshPort = string.IsNullOrEmpty(sshPort) ? "22" : sshPort,
                ConnectTimeout = connectTimeout
            };
        }

        // Check if prompting is available
        if (!interactionService.IsAvailable)
        {
            var missing = new List<ConfigurationRequirement>();
            if (string.IsNullOrEmpty(targetHost))
            {
                missing.Add(new(SshContextKey, nameof(SSHConnectionContext.TargetHost)));
            }
            if (string.IsNullOrEmpty(sshUsername))
            {
                missing.Add(new(SshContextKey, nameof(SSHConnectionContext.SshUsername)));
            }
            if (missing.Count > 0)
            {
                throw new ConfigurationRequiredException(SshContextKey, missing);
            }

            return new SSHConnectionContext
            {
                TargetHost = targetHost!,
                SshUsername = sshUsername!,
                SshPassword = null,
                SshKeyPath = null,
                SshPort = sshPort ?? "22",
                ConnectTimeout = connectTimeout
            };
        }

        // Prompt for target host if not provided
        targetHost ??= await PromptForTargetHostAsync(interactionService, configuration, cancellationToken);

        // Prompt for username and port only (no password/key - ssh-agent handles that)
        var (username, port) = await PromptForSshDetailsAsync(
            interactionService,
            sshUsername,
            sshPort,
            cancellationToken);

        return new SSHConnectionContext
        {
            TargetHost = targetHost,
            SshUsername = username,
            SshPassword = null,
            SshKeyPath = null,
            SshPort = port,
            ConnectTimeout = connectTimeout
        };
    }

    private async Task<string> PromptForTargetHostAsync(
        IInteractionService interactionService,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Target host is always treated as a secret
        // Set UNSAFE_SHOW_TARGET_HOST=true/1 to show it as plain text
        var unsafeShowHost = configuration["UNSAFE_SHOW_TARGET_HOST"];
        var forceShow = string.Equals(unsafeShowHost, "true", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(unsafeShowHost, "1", StringComparison.Ordinal);

        var inputs = new InteractionInput[]
        {
            new()
            {
                Name = "targetHost",
                Required = true,
                InputType = forceShow ? InputType.Text : InputType.SecretText,
                Label = "Target Server Host/IP"
            }
        };

        var result = await interactionService.PromptInputsAsync(
            "Target Host Configuration",
            "Please enter the target server host for deployment.",
            inputs,
            cancellationToken: cancellationToken);

        if (result.Canceled)
        {
            throw new OperationCanceledException("Target host configuration was canceled");
        }

        return result.Data["targetHost"].Value ?? throw new InvalidOperationException("Target host is required");
    }

    private async Task<(string SshUsername, string SshPort)> PromptForSshDetailsAsync(
        IInteractionService interactionService,
        string? defaultUsername,
        string? defaultPort,
        CancellationToken cancellationToken)
    {
        var inputs = new InteractionInput[]
        {
            new()
            {
                Name = "sshUsername",
                Required = true,
                InputType = InputType.Text,
                Label = "SSH Username",
                Value = defaultUsername ?? "root"
            },
            new()
            {
                Name = "sshPort",
                InputType = InputType.Text,
                Label = "SSH Port",
                Value = defaultPort ?? "22"
            }
        };

        var result = await interactionService.PromptInputsAsync(
            "SSH Configuration",
            "Provide SSH connection details. Authentication will use your SSH agent.\n\nEnsure ssh-agent is running and your key is loaded (ssh-add).",
            inputs,
            cancellationToken: cancellationToken);

        if (result.Canceled)
        {
            throw new OperationCanceledException("SSH configuration was canceled");
        }

        var sshUsername = result.Data["sshUsername"].Value ?? throw new InvalidOperationException("SSH username is required");
        var sshPort = result.Data["sshPort"].Value ?? "22";

        return (sshUsername, sshPort);
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
            sshSection.Data[nameof(SSHConnectionContext.SshPort)] = sshContext.SshPort;
            // Note: Not persisting SshPassword or SshKeyPath as native ssh uses ssh-agent

            await deploymentStateManager.SaveSectionAsync(sshSection, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist SSH context");
        }
    }
}
