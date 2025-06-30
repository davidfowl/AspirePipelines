namespace Aspire.Hosting.Docker.Pipelines.Utilities;

public static class EnvironmentFileUtility
{
    public static async Task<Dictionary<string, string>> ReadEnvironmentFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            return ParseEnvironmentContent(content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not read environment file {filePath}: {ex.Message}");
            return [];
        }
    }

    public static Dictionary<string, string> ParseEnvironmentContent(string content)
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

    public static bool IsSensitiveEnvironmentVariable(string key)
    {
        var sensitiveKeywords = new[]
        {
            "PASSWORD", "SECRET", "KEY", "TOKEN", "API_KEY", "PRIVATE",
            "CERT", "CREDENTIAL", "AUTH", "PASS", "PWD"
        };

        return sensitiveKeywords.Any(keyword => key.Contains(keyword, StringComparison.InvariantCultureIgnoreCase));
    }
}
