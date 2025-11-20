#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

public class DockerCommandExecutor
{
    private readonly IProcessExecutor _processExecutor;

    public DockerCommandExecutor(IProcessExecutor processExecutor)
    {
        _processExecutor = processExecutor;
    }
    /// <summary>
    /// Generic helper method to execute any process with standard configuration
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecuteProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var result = await _processExecutor.ExecuteAsync(fileName, arguments, cancellationToken: cancellationToken);
        return (result.ExitCode, result.Output, result.Error);
    }

    /// <summary>
    /// Execute Docker commands with standardized error handling
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecuteDockerCommand(string arguments, CancellationToken cancellationToken)
    {
        return await ExecuteProcessAsync("docker", arguments, cancellationToken);
    }

    /// <summary>
    /// Execute Docker Compose commands with standardized error handling
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecuteDockerComposeCommand(string arguments, CancellationToken cancellationToken)
    {
        return await ExecuteProcessAsync("docker-compose", arguments, cancellationToken);
    }

    /// <summary>
    /// Execute Docker login with password via stdin
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> ExecuteDockerLogin(string registryUrl, string username, string password, CancellationToken cancellationToken)
    {
        var arguments = $"login {registryUrl} --username {username} --password-stdin";
        var result = await _processExecutor.ExecuteAsync("docker", arguments, stdinInput: password, cancellationToken: cancellationToken);
        return (result.ExitCode, result.Output, result.Error);
    }

    public async Task CheckDockerAvailability(IReportingStep step, CancellationToken cancellationToken)
    {
        await using var task = await step.CreateTaskAsync("Checking Docker availability", cancellationToken);

        try
        {
            await task.UpdateAsync("Checking Docker client version...", cancellationToken);

            // Check Docker version
            var versionResult = await ExecuteDockerCommand("--version", cancellationToken);

            if (versionResult.ExitCode != 0)
            {
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
                throw new InvalidOperationException("Docker daemon must be running for this deployment");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Docker is required for this deployment", ex);
        }
    }

    public async Task CheckDockerCompose(IReportingStep step, CancellationToken cancellationToken)
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
                    throw new InvalidOperationException("Docker Compose is required for this deployment");
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Docker Compose is required for this deployment", ex);
        }
    }
}
