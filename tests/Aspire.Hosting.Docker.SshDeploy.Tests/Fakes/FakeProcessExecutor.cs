using Aspire.Hosting.Docker.SshDeploy.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IProcessExecutor for testing.
/// Records all calls and allows pre-configuring responses.
/// </summary>
internal class FakeProcessExecutor : IProcessExecutor
{
    private readonly List<ProcessCall> _calls = new();
    private readonly Dictionary<string, ProcessResult> _configuredResults = new();
    private ProcessResult? _defaultResult;

    /// <summary>
    /// Gets all the process execution calls that were made.
    /// </summary>
    public IReadOnlyList<ProcessCall> Calls => _calls.AsReadOnly();

    /// <summary>
    /// Configure a specific result for a given command pattern.
    /// The key should be in the format "fileName arguments".
    /// </summary>
    public void ConfigureResult(string fileNameAndArgs, ProcessResult result)
    {
        _configuredResults[fileNameAndArgs] = result;
    }

    /// <summary>
    /// Configure the default result for any command that doesn't have a specific configured result.
    /// </summary>
    public void ConfigureDefaultResult(ProcessResult result)
    {
        _defaultResult = result;
    }

    public Task<ProcessResult> ExecuteAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        Dictionary<string, string>? environmentVariables = null,
        string? stdinInput = null,
        CancellationToken cancellationToken = default)
    {
        var call = new ProcessCall(
            fileName,
            arguments,
            workingDirectory,
            environmentVariables,
            stdinInput,
            DateTime.UtcNow);

        _calls.Add(call);

        // Try to find a configured result
        var key = $"{fileName} {arguments}";
        if (_configuredResults.TryGetValue(key, out var result))
        {
            return Task.FromResult(result);
        }

        // Return default result or success
        return Task.FromResult(_defaultResult ?? new ProcessResult(0, "", ""));
    }

    /// <summary>
    /// Asserts that a specific command was called.
    /// </summary>
    public bool WasCalled(string fileName, string? arguments = null)
    {
        return _calls.Any(c =>
            c.FileName == fileName &&
            (arguments == null || c.Arguments == arguments));
    }

    /// <summary>
    /// Gets the number of times a specific command was called.
    /// </summary>
    public int GetCallCount(string fileName, string? arguments = null)
    {
        return _calls.Count(c =>
            c.FileName == fileName &&
            (arguments == null || c.Arguments == arguments));
    }

    /// <summary>
    /// Clears all recorded calls.
    /// </summary>
    public void ClearCalls()
    {
        _calls.Clear();
    }
}

/// <summary>
/// Represents a recorded process execution call.
/// </summary>
public record ProcessCall(
    string FileName,
    string Arguments,
    string? WorkingDirectory,
    Dictionary<string, string>? EnvironmentVariables,
    string? StdinInput,
    DateTime CalledAt);
