using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WEBit.Core.Models;

namespace WEBit.Core.Apps;

/// <summary>Program that runs an app command.</summary>
public enum AppProgram
{
    /// <summary>node.exe of the Node.js module.</summary>
    Node,

    /// <summary>npm (node.exe + npm-cli.js of the Node.js module).</summary>
    Npm,

    /// <summary>uv.exe of the Python module.</summary>
    Uv,

    /// <summary>Own command line through cmd.exe (Node.js sites with a custom command).</summary>
    Shell
}

/// <summary>How an app is started: program, arguments and a readable form for the UI.</summary>
public sealed record AppLaunch(AppProgram Program, IReadOnlyList<string> Arguments, string Display)
{
    /// <summary>Shell only: the complete command line.</summary>
    public string? CommandLine { get; init; }
}

/// <summary>
/// Detects the start command of Node.js and Python projects (dev servers first).
/// The port is passed as PORT and, for tools that ignore PORT, as command line option.
/// </summary>
public static partial class AppCommandResolver
{
    public const string Loopback = "127.0.0.1";

    public static readonly string[] NodeEntries =
        ["server.js", "app.js", "index.js", "main.js", "server.mjs", "app.mjs", "index.mjs", "main.mjs"];

    public static readonly string[] NodeScripts = ["dev", "start", "serve"];

    public static readonly string[] PythonEntries = ["app.py", "main.py", "server.py", "run.py", "wsgi.py", "asgi.py"];

    [GeneratedRegex(@"^\s*(\w+)\s*(?::[^=]+)?=\s*(?:fastapi\.)?(FastAPI|Starlette)\s*\(", RegexOptions.Multiline)]
    private static partial Regex AsgiAppRegex();

    [GeneratedRegex(@"\bFlask\s*\(")]
    private static partial Regex FlaskRegex();

    /// <summary>Start command of the site; null when the folder has nothing to start.</summary>
    public static AppLaunch? Resolve(SiteDefinition site, int port) => site.Type switch
    {
        SiteType.Node => ResolveNode(site, port),
        SiteType.Python => ResolvePython(site, port),
        _ => null
    };

    /// <summary>Installs the dependencies (npm install, uv sync); null when there is nothing to install.</summary>
    public static AppLaunch? ResolveInstall(SiteDefinition site)
    {
        var root = site.Root;
        if (site.Type == SiteType.Node && File.Exists(Path.Combine(root, "package.json")))
        {
            return new AppLaunch(AppProgram.Npm, ["install"], "npm install");
        }

        if (site.Type == SiteType.Python)
        {
            var args = new List<string>();
            if (File.Exists(Path.Combine(root, "pyproject.toml")))
            {
                args.Add("sync");
            }
            else
            {
                // No project file: fetch the Python version up front (packages come with the first start).
                args.AddRange(["python", "install"]);
                if (site.App.PythonVersion is { } v)
                {
                    args.Add(v);
                }

                return new AppLaunch(AppProgram.Uv, args, "uv " + string.Join(' ', args));
            }

            if (site.App.PythonVersion is { } version)
            {
                args.AddRange(["--python", version]);
            }

            return new AppLaunch(AppProgram.Uv, args, "uv " + string.Join(' ', args));
        }

        return null;
    }

    /// <summary>Node.js project whose packages were never installed (quick test: install before the first start).</summary>
    public static bool NeedsNodeInstall(SiteDefinition site) =>
        site.Type == SiteType.Node && site.App.Command is null
        && File.Exists(Path.Combine(site.Root, "package.json"))
        && !Directory.Exists(Path.Combine(site.Root, "node_modules"))
        && PackageNeedsModules(Path.Combine(site.Root, "package.json"));

    /// <summary>The folder looks like a Node.js (package.json, server.js …) or Python project.</summary>
    public static SiteType? DetectType(string root)
    {
        try
        {
            if (File.Exists(Path.Combine(root, "package.json")))
            {
                return SiteType.Node;
            }

            if (File.Exists(Path.Combine(root, "manage.py")) || File.Exists(Path.Combine(root, "pyproject.toml"))
                || File.Exists(Path.Combine(root, "requirements.txt"))
                || PythonEntries.Any(e => File.Exists(Path.Combine(root, e))))
            {
                return SiteType.Python;
            }

            if (NodeEntries.Take(2).Any(e => File.Exists(Path.Combine(root, e))))
            {
                return SiteType.Node;
            }
        }
        catch (Exception)
        {
            // Unreadable folder: no suggestion.
        }

        return null;
    }

    // ------------------------------------------------------------------ Node.js

    private static AppLaunch? ResolveNode(SiteDefinition site, int port)
    {
        if (site.App.Command is { } command)
        {
            return new AppLaunch(AppProgram.Shell, [], command) { CommandLine = command };
        }

        var root = site.Root;
        var package = Path.Combine(root, "package.json");
        if (File.Exists(package) && ReadScripts(package) is { Count: > 0 } scripts)
        {
            foreach (var name in NodeScripts)
            {
                if (scripts.TryGetValue(name, out var script))
                {
                    var args = new List<string> { "run", name };
                    var extra = PortArguments(script, port);
                    if (extra.Count > 0)
                    {
                        args.Add("--");
                        args.AddRange(extra);
                    }

                    return new AppLaunch(AppProgram.Npm, args, "npm " + string.Join(' ', args));
                }
            }
        }

        var entry = File.Exists(package) ? ReadMain(package) : null;
        entry ??= NodeEntries.FirstOrDefault(e => File.Exists(Path.Combine(root, e)));
        return entry is null ? null : new AppLaunch(AppProgram.Node, [entry], "node " + entry);
    }

    /// <summary>Dev servers that ignore PORT get the port (and the IPv4 loopback) on the command line.</summary>
    public static List<string> PortArguments(string script, int port)
    {
        var s = script.ToLowerInvariant();
        var p = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.Contains("--port") || Regex.IsMatch(s, @"(^|\s)-p(\s|=)"))
        {
            return [];
        }

        if (Regex.IsMatch(s, @"\bnext\s+(dev|start)\b"))
        {
            return ["--port", p, "--hostname", Loopback];
        }

        if (Regex.IsMatch(s, @"(^|[\s&;/])vite(\s|$)") || s.Contains("vite dev") || s.Contains("vite serve") || s.Contains("vite preview"))
        {
            return ["--port", p, "--strictPort", "--host", Loopback];
        }

        if (s.Contains("astro") || s.Contains("nuxt") || s.Contains("ng serve") || s.Contains("webpack serve")
            || s.Contains("webpack-dev-server") || s.Contains("remix vite"))
        {
            return ["--port", p, "--host", Loopback];
        }

        return [];
    }

    private static Dictionary<string, string>? ReadScripts(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return scripts.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadMain(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("main", out var main) && main.GetString() is { Length: > 0 } file
                && !file.Contains("..", StringComparison.Ordinal) && !Path.IsPathRooted(file)
                && File.Exists(Path.Combine(Path.GetDirectoryName(packageJson)!, file)))
            {
                return file;
            }
        }
        catch (Exception)
        {
            // Broken package.json: fall back to the usual file names.
        }

        return null;
    }

    private static bool PackageNeedsModules(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return new[] { "dependencies", "devDependencies" }.Any(name =>
                doc.RootElement.TryGetProperty(name, out var deps) && deps.ValueKind == JsonValueKind.Object && deps.EnumerateObject().Any());
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ Python

    private static AppLaunch? ResolvePython(SiteDefinition site, int port)
    {
        var root = site.Root;
        var p = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var hasProject = File.Exists(Path.Combine(root, "pyproject.toml"));
        var hasRequirements = File.Exists(Path.Combine(root, "requirements.txt"));

        var args = new List<string> { "run" };
        if (site.App.PythonVersion is { } version)
        {
            args.AddRange(["--python", version]);
        }

        if (!hasProject && hasRequirements)
        {
            args.AddRange(["--with-requirements", "requirements.txt"]);
        }

        // Server packages only when the project declares no dependencies (a second copy would shadow its own).
        void With(string package)
        {
            if (!hasProject && !hasRequirements)
            {
                args.AddRange(["--with", package]);
            }
        }

        if (site.App.Command is { } command)
        {
            args.AddRange(Tokenize(command));
            return Launch(args);
        }

        if (File.Exists(Path.Combine(root, "manage.py")))
        {
            With("django");
            args.AddRange(["python", "manage.py", "runserver", $"{Loopback}:{p}"]);
            if (site.App.KeepAlive)
            {
                args.Add("--noreload");
            }

            return Launch(args);
        }

        foreach (var entry in PythonEntries)
        {
            var file = Path.Combine(root, entry);
            if (!File.Exists(file))
            {
                continue;
            }

            var module = Path.GetFileNameWithoutExtension(entry);
            var text = ReadHead(file);
            if (AsgiAppRegex().Match(text) is { Success: true } asgi)
            {
                With("uvicorn");
                args.AddRange(["uvicorn", $"{module}:{asgi.Groups[1].Value}", "--host", Loopback, "--port", p]);
                if (!site.App.KeepAlive)
                {
                    args.Add("--reload");
                }

                return Launch(args);
            }

            if (FlaskRegex().IsMatch(text))
            {
                With("flask");
                // Debugger and reloader only for the dev server, not in continuous operation.
                args.AddRange(["flask", "--app", module, "run", "--host", Loopback, "--port", p]);
                // Reloader and error pages, but no interactive debugger console: the site may be reachable from the LAN.
                if (!site.App.KeepAlive)
                {
                    args.AddRange(["--debug", "--no-debugger"]);
                }

                return Launch(args);
            }

            // Plain script: reads PORT itself.
            args.AddRange(["python", entry]);
            return Launch(args);
        }

        return null;

        static AppLaunch Launch(List<string> a) => new(AppProgram.Uv, a, "uv " + string.Join(' ', a.Select(Quote)));
    }

    private static string ReadHead(string file)
    {
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[64 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>Splits a command line at spaces; double quotes group, "" inside quotes is a literal quote.</summary>
    public static List<string> Tokenize(string command)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var hasToken = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '"')
            {
                if (quoted && i + 1 < command.Length && command[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                    hasToken = true;
                }
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (hasToken || current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (hasToken || current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
