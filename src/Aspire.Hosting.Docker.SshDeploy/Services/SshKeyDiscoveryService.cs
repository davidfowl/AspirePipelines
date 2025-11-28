#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Docker.SshDeploy.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// Service for discovering SSH private keys on the local machine.
/// </summary>
internal interface ISshKeyDiscoveryService
{
    /// <summary>
    /// Discovers SSH private keys in the user's ~/.ssh directory.
    /// </summary>
    /// <returns>A list of discovered SSH key paths.</returns>
    List<SshKeyInfo> DiscoverKeys();

    /// <summary>
    /// Reads the contents of an SSH private key file.
    /// </summary>
    /// <param name="path">The path to the key file (supports ~ and $HOME expansion).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The contents of the key file.</returns>
    Task<string> ReadKeyAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Information about a discovered SSH key.
/// </summary>
/// <param name="FullPath">The full path to the private key file.</param>
/// <param name="DisplayPath">A user-friendly display path (e.g., ~/.ssh/id_rsa).</param>
/// <param name="KeyType">The type of key (e.g., RSA, ED25519, ECDSA).</param>
internal record SshKeyInfo(string FullPath, string DisplayPath, string? KeyType);

/// <summary>
/// Default implementation of SSH key discovery.
/// </summary>
internal class SshKeyDiscoveryService : ISshKeyDiscoveryService
{
    private readonly IFileSystem _fileSystem;

    private static readonly Dictionary<string, string> KeyTypeMarkers = new()
    {
        ["-----BEGIN OPENSSH PRIVATE KEY-----"] = "OpenSSH",
        ["-----BEGIN RSA PRIVATE KEY-----"] = "RSA",
        ["-----BEGIN DSA PRIVATE KEY-----"] = "DSA",
        ["-----BEGIN EC PRIVATE KEY-----"] = "ECDSA",
        ["-----BEGIN PRIVATE KEY-----"] = "PKCS#8"
    };

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "known_hosts", "known_hosts.old", "authorized_keys", "config"
    };

    public SshKeyDiscoveryService(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public List<SshKeyInfo> DiscoverKeys()
    {
        var keys = new List<SshKeyInfo>();
        var sshDir = _fileSystem.CombinePaths(_fileSystem.GetUserProfilePath(), ".ssh");

        if (!_fileSystem.DirectoryExists(sshDir))
        {
            return keys;
        }

        // Check all files in ~/.ssh (excluding .pub files and known non-key files)
        foreach (var file in _fileSystem.GetFiles(sshDir, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = _fileSystem.GetFileName(file);

            // Skip .pub files and known non-key files
            if (fileName.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ||
                ExcludedFiles.Contains(fileName))
            {
                continue;
            }

            // Check if file contains a private key marker
            var keyType = DetectKeyType(file);
            if (keyType != null)
            {
                var displayPath = $"~/.ssh/{fileName}";
                keys.Add(new SshKeyInfo(file, displayPath, keyType));
            }
        }

        return keys;
    }

    public async Task<string> ReadKeyAsync(string path, CancellationToken cancellationToken)
    {
        var expandedPath = ExpandPath(path);

        if (!_fileSystem.FileExists(expandedPath))
        {
            throw new FileNotFoundException($"SSH private key file not found: {path}");
        }

        return await _fileSystem.ReadAllTextAsync(expandedPath, cancellationToken);
    }

    private string? DetectKeyType(string filePath)
    {
        try
        {
            // Read just the first line to check for key markers
            var lines = _fileSystem.ReadAllLines(filePath);
            if (lines.Length == 0)
            {
                return null;
            }

            var firstLine = lines[0];

            foreach (var (marker, keyType) in KeyTypeMarkers)
            {
                if (firstLine.Contains(marker, StringComparison.Ordinal))
                {
                    return keyType;
                }
            }

            return null;
        }
        catch
        {
            // If we can't read the file, it's not a valid key
            return null;
        }
    }

    private string ExpandPath(string path)
    {
        var homeDir = _fileSystem.GetUserProfilePath();
        var expanded = path.Replace("~", homeDir);
        expanded = Environment.ExpandEnvironmentVariables(expanded);
        if (expanded.Contains("$HOME"))
        {
            expanded = expanded.Replace("$HOME", homeDir);
        }
        return expanded;
    }
}
