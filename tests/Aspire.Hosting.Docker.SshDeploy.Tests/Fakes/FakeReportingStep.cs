#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IReportingStep for testing.
/// </summary>
internal class FakeReportingStep : IReportingStep
{
    private readonly List<FakeReportingTask> _tasks = new();

    public IReadOnlyList<FakeReportingTask> Tasks => _tasks.AsReadOnly();

    public void Log(LogLevel logLevel, string message, bool enableMarkdown) { }

    public Task CompleteAsync(string completionText, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReportingTask> CreateTaskAsync(string description, CancellationToken cancellationToken = default)
    {
        var task = new FakeReportingTask(description);
        _tasks.Add(task);
        return Task.FromResult<IReportingTask>(task);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Hand-rolled fake implementation of IReportingTask for testing.
/// </summary>
internal class FakeReportingTask : IReportingTask
{
    public string Description { get; }
    public string? CompletionText { get; private set; }
    public CompletionState? CompletionState { get; private set; }

    public FakeReportingTask(string description)
    {
        Description = description;
    }

    public Task UpdateAsync(string statusText, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CompleteAsync(string? completionText = null, CompletionState completionState = Pipelines.CompletionState.Completed, CancellationToken cancellationToken = default)
    {
        CompletionText = completionText;
        CompletionState = completionState;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
