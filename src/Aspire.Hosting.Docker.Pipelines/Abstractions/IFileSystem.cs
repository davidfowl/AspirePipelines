namespace Aspire.Hosting.Docker.Pipelines.Abstractions;

/// <summary>
/// Abstraction for file system operations.
/// </summary>
internal interface IFileSystem
{
    /// <summary>
    /// Checks if a file exists at the specified path.
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// Checks if a directory exists at the specified path.
    /// </summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// Reads all text from a file asynchronously.
    /// </summary>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads all lines from a file.
    /// </summary>
    string[] ReadAllLines(string path);

    /// <summary>
    /// Writes all text to a file asynchronously.
    /// </summary>
    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all files in a directory matching the specified search pattern.
    /// </summary>
    string[] GetFiles(string path, string searchPattern, SearchOption searchOption);

    /// <summary>
    /// Gets the user's home directory path.
    /// </summary>
    string GetUserProfilePath();

    /// <summary>
    /// Combines path segments into a single path.
    /// </summary>
    string CombinePaths(params string[] paths);

    /// <summary>
    /// Gets the file name from a path.
    /// </summary>
    string GetFileName(string path);
}
