using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Models;

namespace WEBit.Service.Modules;

/// <summary>Xdebug: Windows DLLs (non-thread-safe, x64) of the latest stable release from xdebug.org, one per PHP branch.</summary>
public sealed partial class ModuleDownloader
{
    private sealed record XdebugBuild(string Branch, string Url, string? Sha256);

    private sealed record XdebugRelease(string Version, List<XdebugBuild> Builds);

    // php_xdebug-3.4.5-8.4-nts-vs17-x86_64.dll (also the older order …-8.3-vs16-nts-x86_64.dll); stable versions only.
    [GeneratedRegex(@"href=""(?<url>[^""]*?php_xdebug-(?<ver>\d+\.\d+\.\d+)-(?<php>\d+\.\d+)-(?:nts-vs\d+|vs\d+-nts)-x86_64\.dll)""", RegexOptions.IgnoreCase)]
    private static partial Regex XdebugLinkRegex();

    [GeneratedRegex(@"\b[0-9a-f]{64}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Sha256Regex();

    private async Task<XdebugRelease> ResolveXdebugAsync(CancellationToken ct)
    {
        var html = await _http.GetStringAsync(XdebugPage, ct);
        var links = XdebugLinkRegex().Matches(html).ToList();
        var newest = links
            .Select(m => m.Groups["ver"].Value)
            .Distinct()
            .OrderByDescending(v => System.Version.Parse(v))
            .FirstOrDefault()
            ?? throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no Xdebug Windows build on xdebug.org"));

        var builds = new List<XdebugBuild>();
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (link.Groups["ver"].Value != newest || builds.Any(b => b.Branch == link.Groups["php"].Value))
            {
                continue;
            }

            // Checksum shown right after the link (up to the next link of any kind).
            var start = link.Index + link.Length;
            var nextHref = html.IndexOf("href=", start, StringComparison.OrdinalIgnoreCase);
            var next = nextHref >= 0 ? nextHref : html.Length;
            var window = html[start..Math.Max(start, Math.Min(next, start + 600))];
            var hash = Sha256Regex().Match(window) is { Success: true } h ? h.Value.ToLowerInvariant() : null;

            var url = link.Groups["url"].Value;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = XdebugBase + "/" + url.TrimStart('/');
            }

            builds.Add(new XdebugBuild(link.Groups["php"].Value, url, hash));
        }

        return new XdebugRelease(newest, builds);
    }

    /// <summary>Downloads the DLL of every PHP branch into <paramref name="staging"/> as php_xdebug-&lt;branch&gt;.dll.</summary>
    private async Task<string> DownloadXdebugAsync(string work, string staging, CancellationToken ct)
    {
        var release = await ResolveXdebugAsync(ct);
        Update(ModuleIds.Xdebug, s => s.Version = release.Version);
        Directory.CreateDirectory(staging);
        foreach (var build in release.Builds)
        {
            if (!build.Url.StartsWith(XdebugBase + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _log.LogInformation("Downloading Xdebug {Version} for PHP {Branch} from {Url}.", release.Version, build.Branch, build.Url);
            var file = Path.Combine(work, $"xdebug-{build.Branch}.dll");
            await DownloadAsync(ModuleIds.Xdebug, build.Url, file, ct);

            if (build.Sha256 is { } expected)
            {
                if (!string.Equals(await HashAsync(file, sha512: false, ct), expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadChecksumMismatch, Path.GetFileName(build.Url)));
                }
            }
            else
            {
                // xdebug.org lists no checksum for this file: HTTPS from xdebug.org and a Windows DLL header.
                _log.LogWarning("No published checksum for {Url}; accepted over HTTPS from xdebug.org.", build.Url);
            }

            if (!IsWindowsDll(file))
            {
                throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadChecksumMismatch, Path.GetFileName(build.Url)));
            }

            File.Copy(file, Path.Combine(staging, $"php_xdebug-{build.Branch}.dll"), overwrite: true);
        }

        if (!Directory.EnumerateFiles(staging, "*.dll").Any())
        {
            throw new DownloadException(LocalizedMessage.Of(MessageCodes.DownloadFailed, "no Xdebug Windows build on xdebug.org"));
        }

        Update(ModuleIds.Xdebug, s => { s.Percent = 100; s.Message = LocalizedMessage.Of(MessageCodes.DownloadInstalling); });
        return release.Version;
    }

    private static bool IsWindowsDll(string file)
    {
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[2];
        return stream.Read(header) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }
}
