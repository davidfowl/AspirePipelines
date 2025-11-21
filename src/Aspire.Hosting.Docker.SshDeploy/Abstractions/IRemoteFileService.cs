namespace Aspire.Hosting.Docker.SshDeploy.Abstractions;

/// <summary>
/// Provides high-level file transfer and verification operations for remote servers.
/// </summary>
internal interface IRemoteFileService
{
    /// <summary>
    /// Transfers a file to the remote server and verifies it arrived correctly.
    /// </summary>
    /// <param name="localPath">Path to the local file to transfer</param>
    /// <param name="remotePath">Destination path on the remote server</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing transfer status and file information</returns>
    Task<FileTransferResult> TransferWithVerificationAsync(
        string localPath,
        string remotePath,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets information about a file on the remote server.
    /// </summary>
    /// <param name="remotePath">Path to the remote file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File information, or null if the file doesn't exist</returns>
    Task<RemoteFileInfo?> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken);
}

/// <summary>
/// Result of a file transfer operation.
/// </summary>
internal record FileTransferResult(
    bool Success,
    long BytesTransferred,
    bool Verified,
    RemoteFileInfo RemoteFile);

/// <summary>
/// Information about a file on the remote server.
/// </summary>
internal record RemoteFileInfo(
    string Path,
    long Size,
    bool Exists);
