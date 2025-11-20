using Aspire.Hosting.Docker.Pipelines.Abstractions;

namespace Aspire.Hosting.Docker.Pipelines.Infrastructure;

/// <summary>
/// Concrete implementation of IFileSystem that wraps System.IO operations.
/// </summary>
public class FileSystemAdapter : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
        => File.ReadAllTextAsync(path, cancellationToken);

    public string[] ReadAllLines(string path) => File.ReadAllLines(path);

    public Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
        => File.WriteAllTextAsync(path, contents, cancellationToken);

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        => Directory.GetFiles(path, searchPattern, searchOption);

    public string GetUserProfilePath()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string CombinePaths(params string[] paths) => Path.Combine(paths);

    public string GetFileName(string path) => Path.GetFileName(path);
}
