using System.Text;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

/// <summary>
/// Parses SSH command output that has been wrapped with delimiters.
/// Used by PersistentSSHConnectionManager to extract command results from the output stream.
/// </summary>
internal static class SSHOutputParser
{
    // Unique delimiters for parsing command output
    public const string OutputStartMarker = "___ASPIRE_CMD_START___";
    public const string OutputEndMarker = "___ASPIRE_CMD_END___";
    public const string ExitCodeMarker = "___ASPIRE_EXIT_CODE___";

    /// <summary>
    /// Wraps a command with markers for later parsing.
    /// Stderr is redirected to stdout (2&gt;&amp;1) so all output comes through one stream.
    /// </summary>
    public static string WrapCommand(string command)
    {
        // Wrap command in a group with stderr redirected to stdout.
        // Capture exit code immediately after the command (before any other operations).
        // The group command `{ cmd; }` preserves the exit code of the last command inside it.
        return $"echo {OutputStartMarker}; {{ {command}; __ec=$?; }} 2>&1; echo {ExitCodeMarker}$__ec; echo {OutputEndMarker}";
    }

    /// <summary>
    /// Parses lines of output to extract the command result.
    /// </summary>
    /// <param name="lines">The lines of output from the SSH session</param>
    /// <returns>A tuple containing the exit code and the command output</returns>
    public static (int ExitCode, string Output) ParseOutput(IEnumerable<string> lines)
    {
        var outputBuilder = new StringBuilder();
        int exitCode = 0;
        bool foundStart = false;
        bool foundEnd = false;

        foreach (var line in lines)
        {
            if (line == OutputStartMarker)
            {
                foundStart = true;
                continue;
            }

            if (line == OutputEndMarker)
            {
                foundEnd = true;
                continue;
            }

            if (line.StartsWith(ExitCodeMarker))
            {
                if (int.TryParse(line[ExitCodeMarker.Length..], out var ec))
                {
                    exitCode = ec;
                }
                continue;
            }

            if (foundStart && !foundEnd)
            {
                outputBuilder.AppendLine(line);
            }
        }

        var output = outputBuilder.ToString().TrimEnd('\r', '\n');
        return (exitCode, output);
    }

    /// <summary>
    /// Processes a single line of output, updating the parse state.
    /// Returns true if the end marker was found.
    /// </summary>
    public static bool ProcessLine(
        string? line,
        ref bool foundStart,
        ref int exitCode,
        StringBuilder outputBuilder)
    {
        if (line == null)
        {
            return false;
        }

        if (line == OutputStartMarker)
        {
            foundStart = true;
            return false;
        }

        if (line == OutputEndMarker)
        {
            return true; // End found
        }

        if (line.StartsWith(ExitCodeMarker))
        {
            if (int.TryParse(line[ExitCodeMarker.Length..], out var ec))
            {
                exitCode = ec;
            }
            return false;
        }

        if (foundStart)
        {
            outputBuilder.AppendLine(line);
        }

        return false;
    }
}
