using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace Aspire.Hosting.Docker.Pipelines.Infrastructure;

/// <summary>
/// Concrete implementation of IProcessExecutor that wraps System.Diagnostics.Process.
/// </summary>
public class ProcessExecutor : IProcessExecutor
{
    private readonly ILogger<ProcessExecutor> _logger;

    public ProcessExecutor(ILogger<ProcessExecutor> logger)
    {
        _logger = logger;
    }
    public async Task<ProcessResult> ExecuteAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        Dictionary<string, string>? environmentVariables = null,
        string? stdinInput = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Executing process: {FileName} {Arguments}", fileName, arguments);

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

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                process.StartInfo.WorkingDirectory = workingDirectory;
            }

            if (environmentVariables != null)
            {
                foreach (var kvp in environmentVariables)
                {
                    process.StartInfo.Environment[kvp.Key] = kvp.Value;
                }
            }

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

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

            // Send stdin input if provided
            if (!string.IsNullOrEmpty(stdinInput))
            {
                await process.StandardInput.WriteLineAsync(stdinInput);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(cancellationToken);

            var endTime = DateTime.UtcNow;
            _logger.LogDebug("Process completed in {Duration:F1}s, exit code: {ExitCode}", (endTime - startTime).TotalSeconds, process.ExitCode);

            return new ProcessResult(process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            _logger.LogError(ex, "Process failed in {Duration:F1}s", (endTime - startTime).TotalSeconds);
            return new ProcessResult(-1, "", ex.Message);
        }
    }
}
