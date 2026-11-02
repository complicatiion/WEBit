using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WEBit.Core.Serialization;

namespace WEBit.Core.Urls;

/// <summary>Link for messages and exports.</summary>
public sealed record ExportLink(string Title, string Url, string? Folder = null);

/// <summary>Messages and bookmark exports for users.</summary>
public static class LinkExport
{
    /// <summary>Fills {SiteName}, {Url}, {Description}, {Links} and collapses blank lines.</summary>
    public static string RenderMessage(string template, string siteName, string url, string? description, IEnumerable<ExportLink>? links = null)
    {
        var linkText = links is null
            ? ""
            : string.Join(Environment.NewLine, links.Select(l => $"• {l.Title}: {l.Url}"));

        var text = template
            .Replace("{SiteName}", siteName, StringComparison.Ordinal)
            .Replace("{Url}", url, StringComparison.Ordinal)
            .Replace("{Description}", description ?? "", StringComparison.Ordinal)
            .Replace("{Links}", linkText, StringComparison.Ordinal);

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (++blank > 1)
                {
                    continue;
                }
            }
            else
            {
                blank = 0;
            }

            sb.Append(line.TrimEnd()).Append(Environment.NewLine);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Netscape bookmark file (Edge, Chrome, Firefox).</summary>
    public static string BookmarksHtml(IEnumerable<ExportLink> links, string rootFolder = "WEBit")
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<!-- This is an automatically generated file. It will be read and overwritten. DO NOT EDIT! -->");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE>");
        sb.AppendLine("<H1>Bookmarks</H1>");
        sb.AppendLine("<DL><p>");
        sb.AppendLine($"    <DT><H3>{WebUtility.HtmlEncode(rootFolder)}</H3>");
        sb.AppendLine("    <DL><p>");

        foreach (var folder in links.GroupBy(l => l.Folder))
        {
            var indent = "        ";
            if (!string.IsNullOrWhiteSpace(folder.Key))
            {
                sb.AppendLine($"        <DT><H3>{WebUtility.HtmlEncode(folder.Key)}</H3>");
                sb.AppendLine("        <DL><p>");
                indent = "            ";
            }

            foreach (var link in folder)
            {
                sb.AppendLine($"{indent}<DT><A HREF=\"{WebUtility.HtmlEncode(link.Url)}\">{WebUtility.HtmlEncode(link.Title)}</A>");
            }

            if (!string.IsNullOrWhiteSpace(folder.Key))
            {
                sb.AppendLine("        </DL><p>");
            }
        }

        sb.AppendLine("    </DL><p>");
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    /// <summary>Group policy JSON for ManagedFavorites (Edge) / ManagedBookmarks (Chrome).</summary>
    public static string ManagedBookmarksJson(IEnumerable<ExportLink> links, string topLevelName = "WEBit")
    {
        var root = new JsonArray(new JsonObject { ["toplevel_name"] = topLevelName });
        foreach (var folder in links.GroupBy(l => l.Folder))
        {
            if (string.IsNullOrWhiteSpace(folder.Key))
            {
                foreach (var link in folder)
                {
                    root.Add(new JsonObject { ["name"] = link.Title, ["url"] = link.Url });
                }
            }
            else
            {
                var children = new JsonArray();
                foreach (var link in folder)
                {
                    children.Add(new JsonObject { ["name"] = link.Title, ["url"] = link.Url });
                }

                root.Add(new JsonObject { ["name"] = folder.Key, ["children"] = children });
            }
        }

        return root.ToJsonString(WebitJson.File);
    }

    public static string Csv(IEnumerable<ExportLink> links)
    {
        static string Quote(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("Folder;Title;Url");
        foreach (var l in links)
        {
            sb.AppendLine($"{Quote(l.Folder)};{Quote(l.Title)};{Quote(l.Url)}");
        }

        return sb.ToString();
    }

    public static string HtmlLink(ExportLink link) =>
        $"<a href=\"{WebUtility.HtmlEncode(link.Url)}\">{WebUtility.HtmlEncode(link.Title)}</a>";

    public static string MarkdownLink(ExportLink link) =>
        $"[{link.Title.Replace("[", "\\[").Replace("]", "\\]")}]({link.Url})";

    /// <summary>Windows internet shortcut (.url).</summary>
    public static string InternetShortcut(string url) => $"[InternetShortcut]{Environment.NewLine}URL={url}{Environment.NewLine}";
}
