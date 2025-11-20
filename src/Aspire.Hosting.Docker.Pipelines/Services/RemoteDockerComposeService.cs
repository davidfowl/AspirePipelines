using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.Pipelines.Services;

/// <summary>
/// Provides high-level Docker Compose operations on remote servers.
/// </summary>
internal class RemoteDockerComposeService : IRemoteDockerComposeService
{
    private readonly ISSHConnectionManager _sshConnectionManager;
    private readonly ILogger<RemoteDockerComposeService> _logger;

    public RemoteDockerComposeService(
        ISSHConnectionManager sshConnectionManager,
        ILogger<RemoteDockerComposeService> logger)
    {
        _sshConnectionManager = sshConnectionManager;
        _logger = logger;
    }

    public async Task<ComposeOperationResult> StopAsync(string deployPath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Stopping containers in {DeployPath}", deployPath);

        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd {deployPath} && (docker compose down || docker-compose down || true)",
            cancellationToken);

        _logger.LogDebug(
            "Stop completed with exit code {ExitCode}",
            result.ExitCode);

        return new ComposeOperationResult(
            ExitCode: result.ExitCode,
            Output: result.Output,
            Error: result.Error,
            Success: result.ExitCode == 0);
    }

    public async Task<ComposeOperationResult> PullImagesAsync(string deployPath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Pulling images in {DeployPath}", deployPath);

        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd {deployPath} && (docker compose pull || docker-compose pull || true)",
            cancellationToken);

        _logger.LogDebug(
            "Pull completed with exit code {ExitCode}",
            result.ExitCode);

        return new ComposeOperationResult(
            ExitCode: result.ExitCode,
            Output: result.Output,
            Error: result.Error,
            Success: result.ExitCode == 0);
    }

    public async Task<ComposeOperationResult> StartAsync(string deployPath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting containers in {DeployPath}", deployPath);

        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd {deployPath} && (docker compose up -d || docker-compose up -d)",
            cancellationToken);

        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "Start failed with exit code {ExitCode}: {Error}",
                result.ExitCode,
                result.Error);

            throw new InvalidOperationException(
                $"Failed to start containers: {result.Error}");
        }

        _logger.LogDebug("Start completed successfully");

        return new ComposeOperationResult(
            ExitCode: result.ExitCode,
            Output: result.Output,
            Error: result.Error,
            Success: true);
    }

    public async Task<string> GetLogsAsync(string deployPath, int tailLines, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Getting logs from {DeployPath}, tail={TailLines}", deployPath, tailLines);

        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd {deployPath} && (docker compose logs --tail={tailLines} || docker-compose logs --tail={tailLines} || true)",
            cancellationToken);

        _logger.LogDebug("Retrieved {Length} characters of logs", result.Output.Length);

        return result.Output;
    }
}
