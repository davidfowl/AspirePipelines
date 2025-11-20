namespace Aspire.Hosting.Docker.Pipelines.Abstractions;

/// <summary>
/// Abstraction for executing external processes.
/// </summary>
public interface IProcessExecutor
{
    /// <summary>
    /// Executes a process with the specified file name and arguments.
    /// </summary>
    /// <param name="fileName">The executable file name or path.</param>
    /// <param name="arguments">The command-line arguments.</param>
    /// <param name="workingDirectory">The working directory for the process. If null, uses current directory.</param>
    /// <param name="environmentVariables">Additional environment variables to set.</param>
    /// <param name="stdinInput">Optional input to write to the process standard input.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the process execution.</returns>
    Task<ProcessResult> ExecuteAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        Dictionary<string, string>? environmentVariables = null,
        string? stdinInput = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the result of a process execution.
/// </summary>
/// <param name="ExitCode">The exit code returned by the process.</param>
/// <param name="Output">The standard output from the process.</param>
/// <param name="Error">The standard error from the process.</param>
public record ProcessResult(int ExitCode, string Output, string Error);
