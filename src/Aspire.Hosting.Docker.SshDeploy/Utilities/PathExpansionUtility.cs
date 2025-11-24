namespace Aspire.Hosting.Docker.SshDeploy.Utilities;

/// <summary>
/// Provides utilities for expanding path variables to be compatible with remote shell environments.
/// </summary>
internal static class PathExpansionUtility
{
    /// <summary>
    /// Expands tilde (~) to $HOME environment variable for better compatibility with SSH/SCP operations.
    /// This ensures paths work correctly in both interactive and non-interactive shells, as well as SCP transfers.
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
