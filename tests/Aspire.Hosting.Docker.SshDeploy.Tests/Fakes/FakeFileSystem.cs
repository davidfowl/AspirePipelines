using Aspire.Hosting.Docker.SshDeploy.Abstractions;

namespace Aspire.Hosting.Docker.SshDeploy.Tests.Fakes;

/// <summary>
/// Hand-rolled fake implementation of IFileSystem for testing.
/// Provides an in-memory file system and records all operations.
/// </summary>
internal class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new();
    private readonly HashSet<string> _directories = new();
    private readonly List<FileSystemCall> _calls = new();
    private string _userProfilePath = "/home/testuser";

    /// <summary>
    /// Gets all the file system calls that were made.
    /// </summary>
    public IReadOnlyList<FileSystemCall> Calls => _calls.AsReadOnly();

    /// <summary>
    /// Sets the user profile path that will be returned by GetUserProfilePath.
    /// </summary>
    public void SetUserProfilePath(string path)
    {
        _userProfilePath = path;
    }

    /// <summary>
    /// Adds a file to the in-memory file system.
    /// </summary>
    public void AddFile(string path, string content)
    {
        _files[NormalizePath(path)] = content;
    }

    /// <summary>
    /// Adds a directory to the in-memory file system.
    /// </summary>
    public void AddDirectory(string path)
    {
        _directories.Add(NormalizePath(path));
    }

    public bool FileExists(string path)
    {
        _calls.Add(new FileSystemCall("FileExists", path));
        return _files.ContainsKey(NormalizePath(path));
    }

    public bool DirectoryExists(string path)
    {
        _calls.Add(new FileSystemCall("DirectoryExists", path));
        return _directories.Contains(NormalizePath(path));
    }

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        _calls.Add(new FileSystemCall("ReadAllTextAsync", path));
        var normalizedPath = NormalizePath(path);

        if (!_files.TryGetValue(normalizedPath, out var content))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        return Task.FromResult(content);
    }

    public string[] ReadAllLines(string path)
    {
        _calls.Add(new FileSystemCall("ReadAllLines", path));
        var normalizedPath = NormalizePath(path);

        if (!_files.TryGetValue(normalizedPath, out var content))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        return content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        _calls.Add(new FileSystemCall("WriteAllTextAsync", path, contents));
        _files[NormalizePath(path)] = contents;
        return Task.CompletedTask;
    }

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        _calls.Add(new FileSystemCall("GetFiles", path, searchPattern));
        var normalizedPath = NormalizePath(path);

        // Simple pattern matching - just check if path starts with directory path
        var matchingFiles = _files.Keys
            .Where(f => f.StartsWith(normalizedPath + "/") || f.StartsWith(normalizedPath + "\\"))
            .ToArray();

        return matchingFiles;
    }

    public string GetUserProfilePath()
    {
        _calls.Add(new FileSystemCall("GetUserProfilePath", ""));
        return _userProfilePath;
    }

    public string CombinePaths(params string[] paths)
    {
        _calls.Add(new FileSystemCall("CombinePaths", string.Join(", ", paths)));
        return string.Join("/", paths);
    }

    public string GetFileName(string path)
    {
        _calls.Add(new FileSystemCall("GetFileName", path));
        return Path.GetFileName(path);
    }

    public void CreateDirectory(string path)
    {
        _calls.Add(new FileSystemCall("CreateDirectory", path));
        _directories.Add(NormalizePath(path));
    }

    public void DeleteFile(string path)
    {
        _calls.Add(new FileSystemCall("DeleteFile", path));
        _files.Remove(NormalizePath(path));
    }

    /// <summary>
    /// Clears all recorded calls.
    /// </summary>
    public void ClearCalls()
    {
        _calls.Clear();
    }

    /// <summary>
    /// Checks if a specific file system operation was called.
    /// </summary>
    public bool WasCalled(string operation, string? path = null)
    {
        return _calls.Any(c =>
            c.Operation == operation &&
            (path == null || c.Path == path));
    }

    /// <summary>
    /// Gets the number of times a specific operation was called.
    /// </summary>
    public int GetCallCount(string operation, string? path = null)
    {
        return _calls.Count(c =>
            c.Operation == operation &&
            (path == null || c.Path == path));
    }

    private static string NormalizePath(string path)
    {
        return path.Replace("\\", "/");
    }
}

/// <summary>
/// Represents a recorded file system operation call.
/// </summary>
public record FileSystemCall(string Operation, string Path, string? Data = null);
