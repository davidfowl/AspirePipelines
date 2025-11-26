using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// Provides high-level operations for managing environment configuration on remote servers.
/// </summary>
internal class RemoteEnvironmentService : IRemoteEnvironmentService
{
    private readonly ISSHConnectionManager _sshConnectionManager;
    private readonly EnvironmentFileReader _environmentFileReader;
    private readonly ILogger<RemoteEnvironmentService> _logger;

    public RemoteEnvironmentService(
        ISSHConnectionManager sshConnectionManager,
        EnvironmentFileReader environmentFileReader,
        ILogger<RemoteEnvironmentService> logger)
    {
        _sshConnectionManager = sshConnectionManager;
        _environmentFileReader = environmentFileReader;
        _logger = logger;
    }

    public async Task<EnvironmentDeploymentResult> DeployEnvironmentAsync(
        string localEnvPath,
        string remoteDeployPath,
        Dictionary<string, string> imageTags,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Deploying environment from {LocalPath} to {RemotePath}",
            localEnvPath,
            remoteDeployPath);

        // Read the local environment file
        if (!File.Exists(localEnvPath))
        {
            throw new FileNotFoundException(
                $"Environment file not found: {localEnvPath}",
                localEnvPath);
        }

        var envVars = await _environmentFileReader.ReadEnvironmentFile(localEnvPath);

        _logger.LogDebug("Read {Count} environment variables from local file", envVars.Count);

        // Update *_IMAGE variables with registry-tagged images
        foreach (var (serviceName, registryImageTag) in imageTags)
        {
            var imageEnvKey = $"{serviceName.ToUpperInvariant()}_IMAGE";
            envVars[imageEnvKey] = registryImageTag;
            _logger.LogDebug("Updated {Key} = {Value}", imageEnvKey, registryImageTag);
        }

        var finalEnvVars = envVars.OrderBy(kvp => kvp.Key).ToList();

        // Create environment file content
        var envContent = string.Join("\n", finalEnvVars.Select(kvp => $"{kvp.Key}={kvp.Value}"));

        // Write to temporary local file
        var tempFile = Path.Combine(Path.GetTempPath(), $"remote.env.{Guid.NewGuid()}");
        var remoteEnvPath = $"{remoteDeployPath}/.env";

        _logger.LogDebug("Writing merged environment to temporary file: {TempFile}", tempFile);
        await File.WriteAllTextAsync(tempFile, envContent, cancellationToken);

        try
        {
            // Ensure the remote directory exists (use double quotes to allow variable expansion)
            _logger.LogDebug("Ensuring remote directory exists: {RemoteDeployPath}", remoteDeployPath);
            await _sshConnectionManager.ExecuteCommandAsync(
                $"mkdir -p \"{remoteDeployPath}\"",
                cancellationToken);

            // Transfer environment file
            _logger.LogDebug("Transferring environment file to: {RemoteEnvPath}", remoteEnvPath);
            await _sshConnectionManager.TransferFileAsync(tempFile, remoteEnvPath, cancellationToken);

            _logger.LogDebug("Environment deployment completed successfully");

            return new EnvironmentDeploymentResult(
                VariableCount: finalEnvVars.Count,
                RemoteEnvPath: remoteEnvPath,
                MergedVariables: envVars);
        }
        finally
        {
            // Clean up temporary file
            if (File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                    _logger.LogDebug("Cleaned up temporary file: {TempFile}", tempFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary file: {TempFile}", tempFile);
                }
            }
        }
    }
}
