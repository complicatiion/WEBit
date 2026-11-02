using System.Text;
using System.Text.RegularExpressions;
using WEBit.Core.Models;

namespace WEBit.Core.Analysis;

/// <summary>Site folder analysis result.</summary>
public sealed class FolderAnalysis
{
    public string Path { get; set; } = "";

    public bool Exists { get; set; }

    public bool AccessDenied { get; set; }

    public int FileCount { get; set; }

    public int HtmlCount { get; set; }

    public int PhpCount { get; set; }

    /// <summary>Node.js or Python project (package.json, manage.py, app.py …).</summary>
    public SiteType? AppType { get; set; }

    /// <summary>Php when the folder contains .php files, then Node.js/Python projects, otherwise static.</summary>
    public SiteType SuggestedType => PhpCount > 0 ? SiteType.Php : AppType ?? SiteType.Static;

    public long TotalBytes { get; set; }

    public bool Truncated { get; set; }

    public string? StartDocument { get; set; }

    public string? Generator { get; set; }

    public bool IsFrontPage { get; set; }

    public string? MetaCharset { get; set; }

    public CharsetMode SuggestedCharset { get; set; } = CharsetMode.Auto;

    public List<string> SystemFolders { get; set; } = [];
}

/// <summary>Detects start page, generator (e.g. FrontPage), charset and size.</summary>
public static partial class FolderAnalyzer
{
    private const int MaxFiles = 250_000;

    [GeneratedRegex("<meta[^>]+charset\\s*=\\s*[\"']?([A-Za-z0-9_\\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharsetRegex();

    [GeneratedRegex("<meta[^>]+name\\s*=\\s*[\"']?generator[\"']?[^>]*content\\s*=\\s*[\"']([^\"']+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratorRegex();

    [GeneratedRegex("<meta[^>]+content\\s*=\\s*[\"']([^\"']+)[\"'][^>]*name\\s*=\\s*[\"']?generator", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratorReverseRegex();

    private static bool IsPackageFolder(string name) =>
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".venv", StringComparison.OrdinalIgnoreCase)
        || name.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)
        || name.Equals(".git", StringComparison.OrdinalIgnoreCase);

    public static FolderAnalysis Analyze(string path, IReadOnlyList<string>? defaultDocuments = null)
    {
        var result = new FolderAnalysis { Path = path };
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return result;
        }

        result.Exists = true;
        result.AppType = Apps.AppCommandResolver.DetectType(path);
        var docs = defaultDocuments is { Count: > 0 } ? defaultDocuments : SiteContentOptions.DefaultDocumentList();

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            // Installed packages (node_modules, .venv) are not site content and can be huge.
            var files = new System.IO.Enumeration.FileSystemEnumerable<FileInfo>(
                path,
                (ref System.IO.Enumeration.FileSystemEntry entry) => (FileInfo)entry.ToFileSystemInfo(),
                options)
            {
                ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) => !entry.IsDirectory,
                ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
                    !IsPackageFolder(entry.FileName.ToString())
            };

            foreach (var file in files)
            {
                result.FileCount++;
                result.TotalBytes += file.Length;
                var ext = file.Extension.ToLowerInvariant();
                if (ext is ".htm" or ".html" or ".shtml" or ".asp" or ".php")
                {
                    result.HtmlCount++;
                }

                if (ext == ".php")
                {
                    result.PhpCount++;
                }

                if (result.FileCount >= MaxFiles)
                {
                    result.Truncated = true;
                    break;
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(path, "_*", SearchOption.TopDirectoryOnly))
            {
                result.SystemFolders.Add(System.IO.Path.GetFileName(dir));
            }

            if (result.SystemFolders.Any(d => d.StartsWith("_vti_", StringComparison.OrdinalIgnoreCase)))
            {
                result.IsFrontPage = true;
            }

            foreach (var doc in docs)
            {
                var candidate = Directory.EnumerateFiles(path, doc, SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (candidate is not null)
                {
                    result.StartDocument = System.IO.Path.GetFileName(candidate);
                    InspectStartDocument(candidate, result);
                    break;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            result.AccessDenied = true;
        }
        catch (IOException)
        {
            result.AccessDenied = true;
        }

        result.SuggestedCharset = result.MetaCharset is not null
            ? CharsetMode.Auto
            : result.IsFrontPage ? CharsetMode.Windows1252 : CharsetMode.Auto;

        return result;
    }

    private static void InspectStartDocument(string file, FolderAnalysis result)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            int read;
            using (var stream = File.OpenRead(file))
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }

            var head = Encoding.Latin1.GetString(buffer, 0, read);
            var charset = CharsetRegex().Match(head);
            if (charset.Success)
            {
                result.MetaCharset = charset.Groups[1].Value.ToLowerInvariant();
            }

            var generator = GeneratorRegex().Match(head);
            if (!generator.Success)
            {
                generator = GeneratorReverseRegex().Match(head);
            }

            if (generator.Success)
            {
                result.Generator = generator.Groups[1].Value.Trim();
                if (result.Generator.Contains("FrontPage", StringComparison.OrdinalIgnoreCase))
                {
                    result.IsFrontPage = true;
                }
            }
        }
        catch (IOException)
        {
            // Start page unreadable: partial analysis.
        }
        catch (UnauthorizedAccessException)
        {
            result.AccessDenied = true;
        }
    }
}
