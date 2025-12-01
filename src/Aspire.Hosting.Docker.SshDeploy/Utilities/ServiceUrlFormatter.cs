using Aspire.Hosting.Docker.SshDeploy.Models;
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

    /// <summary>
    /// Formats service information including URLs and status/uptime into an ASCII table.
    /// </summary>
    public static string FormatServiceStatusAsTable(ComposeStatus status, Dictionary<string, List<string>> serviceUrls)
    {
        if (status.Services.Count == 0)
        {
            return "No services detected";
        }

        // Build service display infos by matching services to their URLs
        var serviceInfos = status.Services
            .Select(s => new ServiceInfo(
                s.Service,
                serviceUrls.TryGetValue(s.Service, out var urls) ? urls : [],
                s.Status,
                s.IsHealthy))
            .ToList();

        // Apply common prefix removal
        serviceInfos = ApplyPrefixRemoval(serviceInfos);

        // Calculate column widths
        var serviceColWidth = Math.Min(Math.Max(15, serviceInfos.Max(s => s.DisplayName.Length)), 25);
        var statusColWidth = Math.Min(Math.Max(12, serviceInfos.Max(s => s.Status.Length)), 20);
        var urlColWidth = CalculateUrlColumnWidth(serviceInfos);

        var lines = new List<string>();

        // Table header
        lines.Add("\n📋 Service Status:");
        lines.Add(BuildTableBorder(serviceColWidth, statusColWidth, urlColWidth, BorderType.Top));
        lines.Add(BuildHeaderRow(serviceColWidth, statusColWidth, urlColWidth));
        lines.Add(BuildTableBorder(serviceColWidth, statusColWidth, urlColWidth, BorderType.Middle));

        // Table rows
        var hasAnyUrls = false;
        foreach (var info in serviceInfos.OrderBy(s => s.DisplayName))
        {
            var rows = BuildServiceRows(info, serviceColWidth, statusColWidth, urlColWidth);
            lines.AddRange(rows);
            if (info.Urls.Count > 0) hasAnyUrls = true;
        }

        // Table footer
        lines.Add(BuildTableBorder(serviceColWidth, statusColWidth, urlColWidth, BorderType.Bottom));

        if (hasAnyUrls)
        {
            lines.Add("💡 Click or copy URLs above to access your deployed services!");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Masks the host in URLs, replacing with a custom domain or "***" if not provided.
    /// </summary>
    public static Dictionary<string, List<string>> MaskUrlHosts(
        Dictionary<string, List<string>> serviceUrls,
        string? customDomain)
    {
        var replacement = string.IsNullOrEmpty(customDomain) ? "***" : customDomain;
        return serviceUrls.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Select(url => MaskUrlHost(url, replacement)).ToList());
    }

    #region Private Types

    private record ServiceInfo(string OriginalName, List<string> Urls, string Status, bool IsHealthy)
    {
        public string DisplayName { get; init; } = OriginalName;
    }

    private enum BorderType { Top, Middle, Bottom }

    #endregion

    #region Table Building Helpers

    private static string BuildTableBorder(int serviceWidth, int statusWidth, int urlWidth, BorderType type)
    {
        var (left, mid, right) = type switch
        {
            BorderType.Top => ('┌', '┬', '┐'),
            BorderType.Middle => ('├', '┼', '┤'),
            BorderType.Bottom => ('└', '┴', '┘'),
            _ => ('┌', '┬', '┐')
        };
        return $"{left}{new string('─', serviceWidth + 2)}{mid}{new string('─', statusWidth + 2)}{mid}{new string('─', urlWidth + 2)}{right}";
    }

    private static string BuildHeaderRow(int serviceWidth, int statusWidth, int urlWidth)
    {
        return $"│ {"Service".PadRight(serviceWidth)} │ {"Status".PadRight(statusWidth)} │ {"URL".PadRight(urlWidth)} │";
    }

    private static List<string> BuildServiceRows(ServiceInfo info, int serviceWidth, int statusWidth, int urlWidth)
    {
        var rows = new List<string>();
        var serviceCol = TruncateAndPad(info.DisplayName, serviceWidth);
        var statusCol = TruncateAndPad(info.Status, statusWidth);

        if (info.Urls.Count == 0)
        {
            rows.Add($"│ {serviceCol} │ {statusCol} │ {"-".PadRight(urlWidth)} │");
        }
        else
        {
            for (int i = 0; i < info.Urls.Count; i++)
            {
                var svcCol = i == 0 ? serviceCol : "".PadRight(serviceWidth);
                var stsCol = i == 0 ? statusCol : "".PadRight(statusWidth);
                var urlCol = TruncateAndPad(info.Urls[i], urlWidth);
                rows.Add($"│ {svcCol} │ {stsCol} │ {urlCol} │");
            }
        }

        return rows;
    }

    private static string TruncateAndPad(string text, int width)
    {
        if (text.Length > width)
        {
            return text[..(width - 3)] + "...";
        }
        return text.PadRight(width);
    }

    private static int CalculateUrlColumnWidth(List<ServiceInfo> services)
    {
        var maxUrlWidth = 25;
        foreach (var info in services)
        {
            if (info.Urls.Count > 0)
            {
                maxUrlWidth = Math.Max(maxUrlWidth, info.Urls.Max(u => u.Length));
            }
        }
        return Math.Min(maxUrlWidth, 40);
    }

    #endregion

    #region Prefix Removal

    private static List<ServiceInfo> ApplyPrefixRemoval(List<ServiceInfo> services)
    {
        var names = services.Select(s => s.OriginalName).ToList();
        var prefix = FindCommonPrefix(names);

        if (prefix.Length < 3 || names.Count <= 1)
        {
            return services;
        }

        return services.Select(s =>
        {
            var displayName = s.OriginalName.StartsWith(prefix)
                ? s.OriginalName[prefix.Length..].TrimStart('-', '_', '.')
                : s.OriginalName;

            return string.IsNullOrEmpty(displayName)
                ? s
                : s with { DisplayName = displayName };
        }).ToList();
    }

    private static string FindCommonPrefix(List<string> strings)
    {
        if (strings.Count <= 1)
            return "";

        var prefix = strings[0];
        foreach (var str in strings.Skip(1))
        {
            while (prefix.Length > 0 && !str.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                prefix = prefix[..^1];
            }
            if (prefix.Length == 0) break;
        }

        return prefix;
    }

    #endregion

    #region URL Masking

    private static string MaskUrlHost(string url, string replacement)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }
        return $"{uri.Scheme}://{replacement}:{uri.Port}{uri.PathAndQuery}".TrimEnd('/');
    }

    #endregion
}
