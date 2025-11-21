using Aspire.Hosting.Docker.SshDeploy.Abstractions;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.SshDeploy.Services;

internal class EnvironmentFileReader
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<EnvironmentFileReader> _logger;

    public EnvironmentFileReader(IFileSystem fileSystem, ILogger<EnvironmentFileReader> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> ReadEnvironmentFile(string filePath)
    {
        if (!_fileSystem.FileExists(filePath))
        {
            return [];
        }

        try
        {
            var content = await _fileSystem.ReadAllTextAsync(filePath);
            return ParseEnvironmentContent(content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read environment file {FilePath}", filePath);
            return [];
        }
    }

    public Dictionary<string, string> ParseEnvironmentContent(string content)
    {
        var variables = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(content))
        {
            return variables;
        }

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
            {
                continue;
            }

            // Find the first = sign to split key and value
            var equalIndex = trimmedLine.IndexOf('=');
            if (equalIndex > 0)
            {
                var key = trimmedLine[..equalIndex].Trim();
                var value = trimmedLine[(equalIndex + 1)..].Trim();

                // Remove surrounding quotes if present
                if ((value.StartsWith('"') && value.EndsWith('"')) ||
                    (value.StartsWith('\'') && value.EndsWith('\'')))
                {
                    value = value[1..^1];
                }

                variables[key] = value;
            }
        }

        return variables;
    }

    public bool IsSensitiveEnvironmentVariable(string key)
    {
        var sensitiveKeywords = new[]
        {
            "PASSWORD", "SECRET", "KEY", "TOKEN", "API_KEY", "PRIVATE",
            "CERT", "CREDENTIAL", "AUTH", "PASS", "PWD"
        };

        return sensitiveKeywords.Any(keyword => key.Contains(keyword, StringComparison.InvariantCultureIgnoreCase));
    }
}
