namespace Aspire.Hosting.Docker.SshDeploy.Abstractions;

/// <summary>
/// Result of a Docker Compose operation.
/// </summary>
internal record ComposeOperationResult(
    int ExitCode,
    string Output,
    string Error,
    bool Success);
