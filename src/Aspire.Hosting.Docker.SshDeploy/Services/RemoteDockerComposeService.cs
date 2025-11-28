using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Aspire.Hosting.Docker.SshDeploy.Models;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// Provides high-level Docker Compose operations on remote servers.
/// </summary>
internal class RemoteDockerComposeService : IRemoteDockerComposeService
{
    private readonly ISSHConnectionManager _sshConnectionManager;
    private readonly ILogger<RemoteDockerComposeService> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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

        // Use double quotes to allow shell variable expansion (e.g., $HOME)
        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd \"{deployPath}\" && (docker compose down || docker-compose down || true)",
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

        // Use double quotes to allow shell variable expansion (e.g., $HOME)
        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd \"{deployPath}\" && (docker compose pull || docker-compose pull || true)",
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

        // Use double quotes to allow shell variable expansion (e.g., $HOME)
        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd \"{deployPath}\" && (docker compose up -d || docker-compose up -d)",
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

        // Use double quotes to allow shell variable expansion (e.g., $HOME)
        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd \"{deployPath}\" && (docker compose logs --tail={tailLines} || docker-compose logs --tail={tailLines} || true)",
            cancellationToken);

        _logger.LogDebug("Retrieved {Length} characters of logs", result.Output.Length);

        return result.Output;
    }

    public async Task<ComposeStatus> GetStatusAsync(string deployPath, string host, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Getting service status from {DeployPath}", deployPath);

        // Use double quotes to allow shell variable expansion (e.g., $HOME)
        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"cd \"{deployPath}\" && docker compose ps --format json",
            cancellationToken);

        var services = new List<ComposeServiceInfo>();

        if (result.ExitCode == 0 && !string.IsNullOrEmpty(result.Output))
        {
            // docker compose ps --format json outputs NDJSON (one JSON object per line)
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var service = JsonSerializer.Deserialize<ComposeServiceInfo>(line, JsonOptions);
                    if (service != null)
                    {
                        services.Add(service);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Failed to parse JSON line: {Line}", line);
                }
            }
        }
        else
        {
            _logger.LogWarning("Failed to get service status (exit code {ExitCode}): {Error}", result.ExitCode, result.Error);
        }

        // Build service URLs from published ports (supporting multiple ports per service)
        var serviceUrls = new Dictionary<string, List<string>>();
        foreach (var service in services)
        {
            var urls = service.Publishers
                .Where(p => p.PublishedPort > 0)
                .Select(p => BuildUrl(host, p.PublishedPort))
                .ToList();

            if (urls.Count > 0)
            {
                serviceUrls[service.Service] = urls;
            }
        }

        _logger.LogDebug("Found {ServiceCount} services, {UrlCount} with URLs", services.Count, serviceUrls.Count);

        return new ComposeStatus(
            Services: services,
            TotalServices: services.Count,
            HealthyServices: services.Count(s => s.IsHealthy),
            UnhealthyServices: services.Count(s => !s.IsHealthy),
            ServiceUrls: serviceUrls);
    }

    private static string BuildUrl(string host, int port)
    {
        // Use HTTPS for well-known secure ports
        var scheme = port is 443 or 8443 ? "https" : "http";
        return $"{scheme}://{host}:{port}";
    }

    public async IAsyncEnumerable<ComposeStatus> StreamStatusAsync(
        string deployPath,
        string host,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);

        // Yield initial status immediately
        yield return await GetStatusAsync(deployPath, host, cancellationToken);

        // Continue yielding at each interval until cancelled
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            yield return await GetStatusAsync(deployPath, host, cancellationToken);
        }
    }
}
