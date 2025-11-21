namespace Aspire.Hosting.Docker.SshDeploy;

/// <summary>
/// Exception thrown when required configuration is missing and interactive prompting is not available.
/// </summary>
internal class ConfigurationRequiredException : InvalidOperationException
{
    public IReadOnlyList<ConfigurationRequirement> MissingConfiguration { get; }

    public ConfigurationRequiredException(string section, IEnumerable<ConfigurationRequirement> missingConfiguration)
        : base(FormatMessage(section, missingConfiguration))
    {
        MissingConfiguration = missingConfiguration.ToList();
    }

    private static string FormatMessage(string section, IEnumerable<ConfigurationRequirement> requirements)
    {
        var lines = new List<string>
        {
            $"Required {section} configuration is missing and interactive prompting is not available.",
            "",
            "Configure the following settings in appsettings.json or environment variables:",
            ""
        };

        foreach (var req in requirements)
        {
            lines.Add($"  {req.Section}:{req.Key} ({req.Section.ToUpperInvariant()}__{req.Key.ToUpperInvariant()})");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Represents a required configuration setting.
/// </summary>
internal record ConfigurationRequirement(string Section, string Key);
