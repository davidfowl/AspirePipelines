using Aspire.Hosting.Docker.Pipelines.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.Pipelines.Services;

/// <summary>
/// Provides high-level file transfer and verification operations for remote servers.
/// </summary>
internal class RemoteFileService : IRemoteFileService
{
    private readonly ISSHConnectionManager _sshConnectionManager;
    private readonly ILogger<RemoteFileService> _logger;

    public RemoteFileService(
        ISSHConnectionManager sshConnectionManager,
        ILogger<RemoteFileService> logger)
    {
        _sshConnectionManager = sshConnectionManager;
        _logger = logger;
    }

    public async Task<FileTransferResult> TransferWithVerificationAsync(
        string localPath,
        string remotePath,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Transferring file {LocalPath} to {RemotePath}", localPath, remotePath);

        // Get local file size
        var localFileInfo = new FileInfo(localPath);
        if (!localFileInfo.Exists)
        {
            throw new FileNotFoundException($"Local file not found: {localPath}", localPath);
        }

        // Transfer the file
        await _sshConnectionManager.TransferFileAsync(localPath, remotePath, cancellationToken);
        _logger.LogDebug("File transfer completed, verifying...");

        // Verify the transfer
        var remoteFileInfo = await GetFileInfoAsync(remotePath, cancellationToken);

        if (remoteFileInfo == null || !remoteFileInfo.Exists)
        {
            throw new InvalidOperationException($"File transfer verification failed: {Path.GetFileName(localPath)} not found on remote server");
        }

        // Verify size matches
        bool verified = remoteFileInfo.Size == localFileInfo.Length;
        if (!verified)
        {
            throw new InvalidOperationException(
                $"File transfer verification failed: Size mismatch for {Path.GetFileName(localPath)}. " +
                $"Local: {localFileInfo.Length} bytes, Remote: {remoteFileInfo.Size} bytes");
        }

        _logger.LogDebug("File transfer verified successfully: {Bytes} bytes", remoteFileInfo.Size);

        return new FileTransferResult(
            Success: true,
            BytesTransferred: localFileInfo.Length,
            Verified: verified,
            RemoteFile: remoteFileInfo);
    }

    public async Task<RemoteFileInfo?> GetFileInfoAsync(string remotePath, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Getting file info for {RemotePath}", remotePath);

        // Try to get detailed file information using ls -la
        var lsResult = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"ls -la '{remotePath}' 2>/dev/null || echo 'FILE_NOT_FOUND'",
            cancellationToken);

        if (lsResult.ExitCode != 0 || lsResult.Output.Contains("FILE_NOT_FOUND"))
        {
            _logger.LogDebug("File not found: {RemotePath}", remotePath);
            return new RemoteFileInfo(remotePath, 0, false);
        }

        // Parse ls output to get file size
        // Format: -rw-r--r-- 1 user group SIZE date time filename
        var lsOutput = lsResult.Output.Trim();
        var parts = lsOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 5 && long.TryParse(parts[4], out var remoteSize))
        {
            _logger.LogDebug("File found: {RemotePath}, Size: {Size} bytes", remotePath, remoteSize);
            return new RemoteFileInfo(remotePath, remoteSize, true);
        }

        // Fallback: just check if file exists
        var existsResult = await _sshConnectionManager.ExecuteCommandWithOutputAsync(
            $"test -f '{remotePath}' && echo 'EXISTS' || echo 'NOT_FOUND'",
            cancellationToken);

        if (existsResult.Output.Trim() == "EXISTS")
        {
            _logger.LogDebug("File exists but size could not be determined: {RemotePath}", remotePath);
            return new RemoteFileInfo(remotePath, -1, true);
        }

        _logger.LogDebug("File not found: {RemotePath}", remotePath);
        return new RemoteFileInfo(remotePath, 0, false);
    }
}
