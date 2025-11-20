using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.Docker.Pipelines.Services;

/// <summary>
/// Provides high-level operations for inspecting running services on remote servers.
/// </summary>
internal class RemoteServiceInspectionService : IRemoteServiceInspectionService
{
    private readonly ISSHConnectionManager _sshConnectionManager;
    private readonly ILogger<RemoteServiceInspectionService> _logger;

    public RemoteServiceInspectionService(
        ISSHConnectionManager sshConnectionManager,
        ILogger<RemoteServiceInspectionService> logger)
    {
        _sshConnectionManager = sshConnectionManager;
        _logger = logger;
    }

    public async Task<string> GetServiceLogsAsync(
        string serviceName,
        int tailLines,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Getting logs for service {ServiceName}, tail={TailLines}", serviceName, tailLines);

        var logCommand = $"docker logs --tail {tailLines} {serviceName}";
        var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(logCommand, cancellationToken);

        _logger.LogDebug(
            "Retrieved {Length} characters of logs from {ServiceName}",
            result.Output.Length,
            serviceName);

        return result.Output;
    }

    public async Task<string?> ExtractDashboardTokenAsync(
        string serviceName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Extracting dashboard token from {ServiceName}, timeout={Timeout}",
            serviceName,
            timeout);

        var deadline = DateTime.UtcNow.Add(timeout);
        string? token = null;

        // Simplified regex: capture after ?t=
        var urlPattern = @"\?t=(?<tok>[A-Za-z0-9\-_.:]+)";

        int attempt = 0;
        while (DateTime.UtcNow < deadline && token is null && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            _logger.LogDebug("Dashboard token extraction attempt {Attempt}", attempt);

            // Get recent logs
            var logCommand = $"docker logs --tail 50 {serviceName}";
            var result = await _sshConnectionManager.ExecuteCommandWithOutputAsync(logCommand, cancellationToken);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
            {
                _logger.LogDebug("No logs yet or command failed; will retry");
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
                        _logger.LogDebug("Found dashboard login line: {Line}", line);

                        // Attempt regex on this specific line
                        var lineMatch = Regex.Match(line, urlPattern, RegexOptions.IgnoreCase);
                        if (lineMatch.Success && lineMatch.Groups["tok"].Success)
                        {
                            token = lineMatch.Groups["tok"].Value.Trim();
                            _logger.LogDebug("Extracted token via regex: {Token}", token);
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
                                _logger.LogDebug("Extracted token via fallback: {Token}", token);
                                break;
                            }
                        }
                    }
                }

                if (token is not null)
                {
                    break;
                }

                _logger.LogDebug("Token not found in current log snapshot");
            }

            if (token is null)
            {
                // Delay before next attempt
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        if (token is null)
        {
            _logger.LogWarning(
                "Dashboard login token not detected within {Timeout} polling window",
                timeout);
        }
        else
        {
            _logger.LogInformation("Successfully extracted dashboard login token");
        }

        return token;
    }
}
