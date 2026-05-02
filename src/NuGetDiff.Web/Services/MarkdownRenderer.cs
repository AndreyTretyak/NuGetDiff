using System.Text.RegularExpressions;
using Markdig;

namespace NuGetDiff.Web.Services;

/// <summary>
/// Centralised Markdig pipeline configured for untrusted input. Raw HTML is disabled
/// so a hostile package README cannot inject script into our origin, and link/image
/// schemes are filtered down to a safe allow-list (<c>http</c>, <c>https</c>,
/// <c>mailto:</c> and relative URLs; <c>data:image/*</c> additionally for images).
/// In particular this neutralises <c>javascript:</c> link payloads, which Markdig's
/// <c>DisableHtml()</c> alone does not strip from Markdown link syntax.
/// </summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly Regex LinkAttributePattern = new(
        @"(?<attr>href|src)\s*=\s*""(?<url>[^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string ToHtml(string markdown)
    {
        var raw = Markdown.ToHtml(markdown ?? string.Empty, _pipeline);
        return LinkAttributePattern.Replace(raw, m =>
        {
            var attr = m.Groups["attr"].Value;
            var url = m.Groups["url"].Value;
            if (IsSafeUrl(url, attr))
            {
                return m.Value;
            }
            return $"{attr}=\"#\"";
        });
    }

    private static bool IsSafeUrl(string url, string attr)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        if (url.StartsWith('#') || url.StartsWith('/')
            || url.StartsWith("./", StringComparison.Ordinal)
            || url.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }
        if (url.IndexOf(':') < 0)
        {
            // No scheme at all — treat as a relative URL.
            return true;
        }

        var lower = url.ToLowerInvariant();
        var imgAttr = string.Equals(attr, "src", StringComparison.OrdinalIgnoreCase);
        if (lower.StartsWith("http://", StringComparison.Ordinal) || lower.StartsWith("https://", StringComparison.Ordinal))
        {
            return true;
        }
        if (imgAttr && lower.StartsWith("data:image/", StringComparison.Ordinal))
        {
            return true;
        }
        if (!imgAttr && lower.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }
}
