using Aspire.Hosting.Docker.Pipelines.Abstractions;

namespace Aspire.Hosting.Docker.Pipelines.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IRemoteFileService for testing.
/// Records all calls and allows pre-configuring responses.
/// </summary>
public class FakeRemoteFileService : IRemoteFileService
{
    private readonly List<FileOperation> _operations = new();
    private readonly Dictionary<string, RemoteFileInfo> _remoteFiles = new();
    private bool _shouldTransferFail;
    private bool _shouldVerifyFail;

    /// <summary>
    /// Gets all the file operations that were performed.
    /// </summary>
    public IReadOnlyList<FileOperation> Operations => _operations.AsReadOnly();

    /// <summary>
    /// Configures the service to simulate a failed transfer.
    /// </summary>
    public void ConfigureTransferFailure(bool shouldFail = true)
    {
        _shouldTransferFail = shouldFail;
    }

    /// <summary>
    /// Configures the service to simulate a failed verification.
    /// </summary>
    public void ConfigureVerificationFailure(bool shouldFail = true)
    {
        _shouldVerifyFail = shouldFail;
    }

    /// <summary>
    /// Pre-configures information about a remote file.
    /// </summary>
    public void ConfigureRemoteFile(string remotePath, long size, bool exists)
    {
        _remoteFiles[remotePath] = new RemoteFileInfo(remotePath, size, exists);
    }

    public Task<FileTransferResult> TransferWithVerificationAsync(
        string localPath,
        string remotePath,
        CancellationToken cancellationToken)
    {
        _operations.Add(new FileOperation("Transfer", localPath, remotePath));

        if (_shouldTransferFail)
        {
            throw new InvalidOperationException("File transfer failed (configured to fail)");
        }

        // Simulate getting local file size
        var localFileInfo = new FileInfo(localPath);
        var fileSize = localFileInfo.Exists ? localFileInfo.Length : 1024;

        if (_shouldVerifyFail)
        {
            // Return mismatched size to trigger verification failure
            var badRemoteInfo = new RemoteFileInfo(remotePath, fileSize + 100, true);
            throw new InvalidOperationException(
                $"File transfer verification failed: Size mismatch. Local: {fileSize} bytes, Remote: {badRemoteInfo.Size} bytes");
        }

        // Successful transfer
        var remoteInfo = _remoteFiles.GetValueOrDefault(remotePath)
            ?? new RemoteFileInfo(remotePath, fileSize, true);

        // Update the remote files dictionary
        _remoteFiles[remotePath] = remoteInfo;

        return Task.FromResult(new FileTransferResult(
            Success: true,
            BytesTransferred: fileSize,
            Verified: true,
            RemoteFile: remoteInfo));
    }

    public Task<RemoteFileInfo?> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken)
    {
        _operations.Add(new FileOperation("GetInfo", null, remotePath));

        if (_remoteFiles.TryGetValue(remotePath, out var fileInfo))
        {
            return Task.FromResult<RemoteFileInfo?>(fileInfo);
        }

        // File not found
        return Task.FromResult<RemoteFileInfo?>(new RemoteFileInfo(remotePath, 0, false));
    }

    /// <summary>
    /// Checks if a specific operation was performed.
    /// </summary>
    public bool WasOperationPerformed(string operation, string? remotePath = null)
    {
        return _operations.Any(op =>
            op.Operation == operation &&
            (remotePath == null || op.RemotePath == remotePath));
    }

    /// <summary>
    /// Gets the number of times an operation was performed.
    /// </summary>
    public int GetOperationCount(string operation)
    {
        return _operations.Count(op => op.Operation == operation);
    }

    /// <summary>
    /// Clears all recorded operations.
    /// </summary>
    public void ClearOperations()
    {
        _operations.Clear();
    }
}

/// <summary>
/// Represents a recorded file operation.
/// </summary>
public record FileOperation(string Operation, string? LocalPath, string? RemotePath);
