namespace Aspire.Hosting.Docker.SshDeploy.Utilities;

/// <summary>
/// Provides utilities for expanding path variables for local and remote shell environments.
/// </summary>
internal static class PathExpansionUtility
{
    /// <summary>
    /// Expands tilde (~) and $HOME in a local path to the actual user profile directory.
    /// Use this for resolving paths to local files (e.g., SSH keys in configuration).
    /// </summary>
    /// <param name="path">The path that may contain ~ or $HOME prefix</param>
    /// <returns>Path with ~ or $HOME expanded to the actual home directory, or original path if no expansion needed</returns>
    /// <remarks>
    /// Examples:
    /// - "~/.ssh/id_rsa" -> "/Users/john/.ssh/id_rsa"
    /// - "$HOME/.ssh/id_rsa" -> "/Users/john/.ssh/id_rsa"
    /// - "/full/path/key" -> "/full/path/key" (unchanged)
    /// </remarks>
    // TODO: Support expanding arbitrary environment variables (e.g., $VAR or ${VAR} syntax).
    // Note: Environment.ExpandEnvironmentVariables uses %VAR% syntax on all platforms,
    // so we'd need custom parsing for Unix-style $VAR syntax.
    public static string? ExpandToLocalPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Expand ~ to home directory
        if (path.StartsWith("~/"))
        {
            var relativePath = path[2..].Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(homeDir, relativePath);
        }
        if (path == "~")
        {
            return homeDir;
        }

        // Expand $HOME to home directory
        if (path.StartsWith("$HOME/"))
        {
            var relativePath = path[6..].Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(homeDir, relativePath);
        }
        if (path == "$HOME")
        {
            return homeDir;
        }

        return path;
    }

    /// <summary>
    /// Expands tilde (~) to $HOME environment variable for better compatibility with SSH/SCP operations.
    /// Use this for paths that will be executed on a remote shell.
    /// </summary>
    /// <param name="path">The path that may contain tilde (~) prefix</param>
    /// <returns>Path with tilde expanded to $HOME, or original path if no tilde prefix found</returns>
    /// <remarks>
    /// Examples:
    /// - "~/aspire/apps" -> "$HOME/aspire/apps"
    /// - "~/" -> "$HOME/"
    /// - "/opt/app" -> "/opt/app" (unchanged)
    /// - "$HOME/app" -> "$HOME/app" (unchanged)
    /// </remarks>
    public static string? ExpandTildeToHome(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // If path starts with ~/, replace with $HOME/
        if (path.StartsWith("~/"))
        {
            return $"$HOME{path.Substring(1)}";
        }

        // If path is exactly ~, replace with $HOME
        if (path == "~")
        {
            return "$HOME";
        }

        // Return unchanged if no tilde expansion needed
        return path;
    }
}
