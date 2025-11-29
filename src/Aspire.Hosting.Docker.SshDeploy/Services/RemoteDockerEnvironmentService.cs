using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// Provides high-level operations for validating and preparing remote Docker environments.
/// </summary>
internal class RemoteDockerEnvironmentService : IRemoteDockerEnvironmentService
{
    private readonly ISSHConnectionManager _sshConnectionManager;
    private readonly IProcessExecutor _processExecutor;
    private readonly ILogger<RemoteDockerEnvironmentService> _logger;

    public RemoteDockerEnvironmentService(
        ISSHConnectionManager sshConnectionManager,
        IProcessExecutor processExecutor,
        ILogger<RemoteDockerEnvironmentService> logger)
    {
        _sshConnectionManager = sshConnectionManager;
        _processExecutor = processExecutor;
        _logger = logger;
    }

    public async Task<DockerEnvironmentInfo> ValidateDockerEnvironmentAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Validating Docker environment on remote server");

        // Get OS version for diagnostics
        var osVersionCheck = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            "cat /etc/os-release 2>/dev/null | grep -E '^(NAME|VERSION)=' | head -2 || uname -a",
            cancellationToken);
        var osVersion = osVersionCheck.ExitCode == 0 ? osVersionCheck.Output.Trim() : "Unknown";
        _logger.LogInformation("Remote OS: {OsVersion}", osVersion.Replace("\n", " "));

        // Get SSH server version for diagnostics
        var sshVersionCheck = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            "ssh -V 2>&1 || echo 'Unknown'",
            cancellationToken);
        var sshVersion = sshVersionCheck.Output.Trim();
        _logger.LogInformation("Remote SSH: {SshVersion}", sshVersion);

        // Check Docker installation
        var dockerVersionCheck = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            "docker --version",
            cancellationToken);

        if (dockerVersionCheck.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker is not installed on the target server. Error: {dockerVersionCheck.Error}");
        }

        var dockerVersion = dockerVersionCheck.Output.Trim();
        _logger.LogInformation("Remote Docker: {DockerVersion}", dockerVersion);

        // Check Docker daemon is running
        var dockerInfoCheck = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            "docker info --format '{{.ServerVersion}}' 2>/dev/null",
            cancellationToken);

        if (dockerInfoCheck.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker daemon is not running on the target server. Error: {dockerInfoCheck.Error}");
        }

        var serverVersion = dockerInfoCheck.Output.Trim();
        _logger.LogDebug("Docker server version: {ServerVersion}", serverVersion);

        // Check Docker Compose availability
        var composeCheck = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            "docker compose version",
            cancellationToken);

        if (composeCheck.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker Compose is not available on the target server. " +
                $"Command 'docker compose version' failed with exit code {composeCheck.ExitCode}. " +
                $"Error: {composeCheck.Error}");
        }

        var composeVersion = composeCheck.Output.Trim();
        _logger.LogDebug("Docker Compose version: {ComposeVersion}", composeVersion);

        // Check Docker permissions
        var dockerPermCheck = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            "docker ps > /dev/null 2>&1 && echo 'OK' || echo 'SUDO_REQUIRED'",
            cancellationToken);

        bool hasPermissions = dockerPermCheck.Output.Trim() == "OK";

        if (!hasPermissions)
        {
            throw new InvalidOperationException(
                "User does not have permission to run Docker commands. " +
                "Add user to 'docker' group and restart the session.");
        }

        _logger.LogDebug("Docker environment validated successfully");

        return new DockerEnvironmentInfo(
            DockerVersion: dockerVersion,
            ServerVersion: serverVersion,
            ComposeVersion: composeVersion,
            HasPermissions: hasPermissions);
    }

    public async Task<string> PrepareDeploymentDirectoryAsync(string deployPath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Preparing deployment directory: {DeployPath}", deployPath);

        // Use double quotes to allow shell variable expansion (e.g., $HOME)
        await _sshConnectionManager.ExecuteCommandAsync(
            $"mkdir -p \"{deployPath}\"",
            cancellationToken);

        _logger.LogDebug("Deployment directory prepared: {DeployPath}", deployPath);

        return deployPath;
    }

    public async Task<DeploymentState> GetDeploymentStateAsync(string deployPath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Checking deployment state in: {DeployPath}", deployPath);

        // Check if there are any existing containers (use double quotes for variable expansion)
        var existingContainersCheck = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd \"{deployPath}\" 2>/dev/null && docker compose ps -q 2>/dev/null | wc -l || echo '0'",
            cancellationToken);

        var containerCountStr = existingContainersCheck.Output?.Trim() ?? "0";
        int existingContainerCount = int.TryParse(containerCountStr, out var count) ? count : 0;

        bool hasPreviousDeployment = existingContainerCount > 0;

        _logger.LogDebug(
            "Deployment state: {ContainerCount} existing containers, HasPreviousDeployment: {HasPrevious}",
            existingContainerCount,
            hasPreviousDeployment);

        return new DeploymentState(
            ExistingContainerCount: existingContainerCount,
            HasPreviousDeployment: hasPreviousDeployment);
    }
}
