#pragma warning disable ASPIREPUBLISHERS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Publishing;
using System.Diagnostics;

namespace Aspire.Hosting.Docker.Pipelines.Utilities;

public static class DockerCommandUtility
{
    /// <summary>
    /// Generic helper method to execute any process with standard configuration
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> ExecuteProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Executing process: {fileName} {arguments}");

        var startTime = DateTime.UtcNow;

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.CreateNoWindow = true;

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            var endTime = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG] Process completed in {(endTime - startTime).TotalSeconds:F1}s, exit code: {process.ExitCode}");

            return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG] Process failed in {(endTime - startTime).TotalSeconds:F1}s: {ex.Message}");
            return (-1, "", ex.Message);
        }
    }

    /// <summary>
    /// Execute Docker commands with standardized error handling
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> ExecuteDockerCommand(string arguments, CancellationToken cancellationToken)
    {
        return await ExecuteProcessAsync("docker", arguments, cancellationToken);
    }

    /// <summary>
    /// Execute Docker Compose commands with standardized error handling
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> ExecuteDockerComposeCommand(string arguments, CancellationToken cancellationToken)
    {
        return await ExecuteProcessAsync("docker-compose", arguments, cancellationToken);
    }

    /// <summary>
    /// Execute Docker login with password via stdin
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> ExecuteDockerLogin(string registryUrl, string username, string password, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[DEBUG] Executing Docker login for registry: {registryUrl}");

        var startTime = DateTime.UtcNow;

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "docker";
            process.StartInfo.Arguments = $"login {registryUrl} --username {username} --password-stdin";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.CreateNoWindow = true;

            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Send password to stdin
            await process.StandardInput.WriteLineAsync(password);
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken);

            var endTime = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG] Docker login completed in {(endTime - startTime).TotalSeconds:F1}s, exit code: {process.ExitCode}");

            return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            Console.WriteLine($"[DEBUG] Docker login failed in {(endTime - startTime).TotalSeconds:F1}s: {ex.Message}");
            return (-1, "", ex.Message);
        }
    }

    public static async Task CheckDockerAvailability(PublishingStep step, CancellationToken cancellationToken)
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

    public static async Task CheckDockerCompose(PublishingStep step, CancellationToken cancellationToken)
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
}
