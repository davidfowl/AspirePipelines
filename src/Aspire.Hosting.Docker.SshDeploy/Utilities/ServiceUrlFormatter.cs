using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Docker.SshDeploy.Utilities;

internal static class ServiceUrlFormatter
{
    /// <summary>
    /// Determines if the target host can be shown in output.
    /// Target host is always treated as sensitive and masked by default.
    /// Set UNSAFE_SHOW_TARGET_HOST=true/1 to show the host.
    /// </summary>
    public static bool CanShowTargetHost(IConfiguration configuration, string? host)
    {
        // If host is empty/null, can't show it anyway
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Target host is always masked unless UNSAFE_SHOW_TARGET_HOST is set
        var unsafeShowHost = configuration["UNSAFE_SHOW_TARGET_HOST"];
        return string.Equals(unsafeShowHost, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(unsafeShowHost, "1", StringComparison.Ordinal);
    }

    public static string FormatServiceUrlsAsTable(Dictionary<string, List<string>> serviceUrls)
    {
        if (serviceUrls.Count == 0)
        {
            return "No exposed ports detected";
        }

        // Remove duplicates and clean up the data
        var cleanedServiceUrls = new Dictionary<string, List<string>>();
        foreach (var (serviceName, urls) in serviceUrls)
        {
            var uniqueUrls = urls.Distinct().OrderBy(u => u).ToList();
            if (uniqueUrls.Count > 0)
            {
                cleanedServiceUrls[serviceName] = uniqueUrls;
            }
        }

        // Remove common prefix from service names if applicable
        var serviceNames = cleanedServiceUrls.Keys.ToList();
        var commonPrefix = FindCommonPrefix(serviceNames);

        // Only remove prefix if it's meaningful (at least 3 characters and applies to multiple services)
        var displayServiceUrls = cleanedServiceUrls;
        if (commonPrefix.Length >= 3 && serviceNames.Count > 1)
        {
            displayServiceUrls = new Dictionary<string, List<string>>();
            foreach (var (serviceName, urls) in cleanedServiceUrls)
            {
                var displayName = serviceName.StartsWith(commonPrefix)
                    ? serviceName[commonPrefix.Length..].TrimStart('-', '_', '.')
                    : serviceName;

                // Ensure we don't end up with empty names
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = serviceName;
                }

                displayServiceUrls[displayName] = urls;
            }
        }

        var lines = new List<string>();

        // Calculate max widths for better formatting
        var maxServiceNameWidth = Math.Max(15, displayServiceUrls.Keys.Max(k => k.Length));

        // Calculate max URL width accounting for emoji and formatting
        var maxUrlContentWidth = 0;
        foreach (var (serviceName, urls) in displayServiceUrls)
        {
            if (urls.Count == 0)
            {
                // Account for "⚠️ (no exposed ports)" - emoji + text
                maxUrlContentWidth = Math.Max(maxUrlContentWidth, "⚠️ (no exposed ports)".Length + 1); // +1 for emoji width
            }
            else
            {
                foreach (var url in urls)
                {
                    // Account for "✅ " prefix on first URL and "   " prefix on subsequent URLs
                    var formattedLength = url.Length + 3; // 3 spaces for "✅ " or "   " (emoji counts as ~2 chars visually)
                    maxUrlContentWidth = Math.Max(maxUrlContentWidth, formattedLength);
                }
            }
        }

        // Limit service name column width for readability, ensure URL column accounts for emoji formatting
        var serviceColWidth = Math.Min(maxServiceNameWidth, 35);
        var urlColWidth = Math.Max(maxUrlContentWidth, 25);

        // Add table header
        lines.Add("\n📋 Service URLs:");
        lines.Add("┌" + new string('─', serviceColWidth + 2) + "┬" + new string('─', urlColWidth + 2) + "┐");
        lines.Add($"│ {"Service".PadRight(serviceColWidth)} │ {"URL".PadRight(urlColWidth)} │");
        lines.Add("├" + new string('─', serviceColWidth + 2) + "┼" + new string('─', urlColWidth + 2) + "┤");

        var hasAnyUrls = false;

        // Add service URLs
        foreach (var (serviceName, urls) in displayServiceUrls.OrderBy(kvp => kvp.Key))
        {
            if (urls.Count == 0)
            {
                // Service with no exposed URLs
                var serviceCol = serviceName.Length > serviceColWidth
                    ? serviceName[..(serviceColWidth - 3)] + "..."
                    : serviceName.PadRight(serviceColWidth);

                var warningText = "⚠️ (no exposed ports)";
                var urlCol = warningText.PadRight(urlColWidth - 1); // -1 to account for emoji visual width
                lines.Add($"│ {serviceCol} │ {urlCol} │");
            }
            else
            {
                hasAnyUrls = true;
                // Service with URLs - show service name only on first row
                for (int i = 0; i < urls.Count; i++)
                {
                    var serviceCol = i == 0
                        ? (serviceName.Length > serviceColWidth
                            ? serviceName[..(serviceColWidth - 3)] + "..."
                            : serviceName.PadRight(serviceColWidth))
                        : "".PadRight(serviceColWidth);

                    var url = urls[i];

                    // Format URL with appropriate icon/spacing
                    var formattedUrl = i == 0 ? $"✅ {url}" : $"   {url}";

                    // Account for emoji visual width when padding
                    var paddingAdjustment = i == 0 ? -1 : 0; // -1 for emoji visual width
                    formattedUrl = formattedUrl.PadRight(urlColWidth + paddingAdjustment);

                    lines.Add($"│ {serviceCol} │ {formattedUrl} │");
                }
            }
        }

        // Add table footer
        lines.Add("└" + new string('─', serviceColWidth + 2) + "┴" + new string('─', urlColWidth + 2) + "┘");

        // Add helpful note if there are URLs
        if (hasAnyUrls)
        {
            lines.Add("💡 Click or copy URLs above to access your deployed services!");
        }

        return string.Join("\n", lines);
    }

    private static string FindCommonPrefix(List<string> strings)
    {
        if (strings.Count == 0)
            return "";

        if (strings.Count == 1)
            return "";

        var prefix = strings[0];
        for (int i = 1; i < strings.Count; i++)
        {
            while (prefix.Length > 0 && !strings[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                prefix = prefix[..^1];
            }

            if (prefix.Length == 0)
                break;
        }

        return prefix;
    }

    /// <summary>
    /// Masks the host in URLs, replacing with a custom domain or "***" if not provided.
    /// </summary>
    public static Dictionary<string, List<string>> MaskUrlHosts(
        Dictionary<string, List<string>> serviceUrls,
        string? customDomain)
    {
        var masked = new Dictionary<string, List<string>>();
        foreach (var (service, urls) in serviceUrls)
        {
            masked[service] = urls.Select(url => MaskUrlHost(url, customDomain)).ToList();
        }
        return masked;
    }

    private static string MaskUrlHost(string url, string? customDomain)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var replacement = string.IsNullOrEmpty(customDomain) ? "***" : customDomain;
        return $"{uri.Scheme}://{replacement}:{uri.Port}{uri.PathAndQuery}".TrimEnd('/');
    }
}
