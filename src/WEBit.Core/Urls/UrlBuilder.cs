using WEBit.Core.Models;
using WEBit.Core.Net;

namespace WEBit.Core.Urls;

public enum SiteUrlKind
{
    /// <summary>Address shared with users.</summary>
    Primary,

    /// <summary>Address of another binding.</summary>
    Binding,

    /// <summary>Alternative form (FQDN, host name, IP).</summary>
    Alternative,

    /// <summary>Link to a sub page.</summary>
    DeepLink
}

/// <summary>Shareable address of a site.</summary>
public sealed class SiteUrl
{
    public string Url { get; set; } = "";

    public SiteUrlKind Kind { get; set; }

    public string? Label { get; set; }
}

/// <summary>URL context (host name, DNS suffix).</summary>
public sealed record UrlContext(string? MachineFqdn, string? DnsSuffix);

/// <summary>Bookmark-ready URLs from bindings; never uses "localhost".</summary>
public static class UrlBuilder
{
    public static string ForBinding(SiteBinding binding, UrlContext context)
    {
        string host;
        if (binding.HasHost)
        {
            host = binding.Host!.Trim().ToLowerInvariant();
        }
        else if (Endpoint.IsWildcard(binding.Address))
        {
            host = string.IsNullOrWhiteSpace(context.MachineFqdn) ? Environment.MachineName.ToLowerInvariant() : context.MachineFqdn!;
        }
        else
        {
            var address = binding.Address.Trim();
            host = address.Contains(':') ? $"[{address}]" : address;
        }

        return Compose(host, binding.Port, "/", binding.Tls);
    }

    public static string Compose(string host, int port, string path, bool https = false)
    {
        var portPart = port == (https ? 443 : 80) ? "" : $":{port}";
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }
        else if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return $"{(https ? "https" : "http")}://{host}{portPart}{path}";
    }

    public static string NormalizePublicUrl(string url)
    {
        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.AbsoluteUri;
        }

        return trimmed;
    }

    /// <summary>Public URL, else first binding with host name, else first binding.</summary>
    /// <summary>Live reload wrapper of a site (same origin, so the frame can be reloaded).</summary>
    public static string LiveUrl(string primaryUrl)
    {
        if (!Uri.TryCreate(primaryUrl, UriKind.Absolute, out var uri))
        {
            return primaryUrl;
        }

        // The frame starts at the path of the primary URL (#/handbook/).
        var live = uri.GetLeftPart(UriPartial.Authority) + Engine.CaddyConfigBuilder.LivePrefix + "/live.html";
        return uri.PathAndQuery is "/" or "" ? live : live + "#" + uri.PathAndQuery;
    }

    public static string Primary(SiteDefinition site, UrlContext context)
    {
        if (!string.IsNullOrWhiteSpace(site.PublicUrl))
        {
            return NormalizePublicUrl(site.PublicUrl);
        }

        var binding = site.Bindings.FirstOrDefault(b => b.HasHost) ?? site.Bindings.FirstOrDefault();
        return binding is null ? "" : ForBinding(binding, context);
    }

    public static List<SiteUrl> Build(SiteDefinition site, UrlContext context)
    {
        var result = new List<SiteUrl>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string url, SiteUrlKind kind, string? label = null)
        {
            if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
            {
                result.Add(new SiteUrl { Url = url, Kind = kind, Label = label });
            }
        }

        var primary = Primary(site, context);
        Add(primary, SiteUrlKind.Primary);

        foreach (var binding in site.Bindings)
        {
            Add(ForBinding(binding, context), SiteUrlKind.Binding);

            if (binding.HasHost && !binding.Host!.Contains('.') && !string.IsNullOrWhiteSpace(context.DnsSuffix))
            {
                Add(Compose($"{binding.Host}.{context.DnsSuffix}", binding.Port, "/", binding.Tls), SiteUrlKind.Alternative);
            }

            if (!binding.HasHost && Endpoint.IsWildcard(binding.Address))
            {
                foreach (var address in HostNetwork.GetIPv4Addresses())
                {
                    Add(Compose(address.Address, binding.Port, "/", binding.Tls), SiteUrlKind.Alternative);
                }
            }
        }

        if (!string.IsNullOrEmpty(primary) && Uri.TryCreate(primary, UriKind.Absolute, out var baseUri))
        {
            foreach (var link in site.Links.Where(l => !string.IsNullOrWhiteSpace(l.Path)))
            {
                var path = link.Path.Trim();
                var relative = path.StartsWith('/') ? path : "/" + path;
                Add(new Uri(baseUri, relative).AbsoluteUri, SiteUrlKind.DeepLink, string.IsNullOrWhiteSpace(link.Label) ? null : link.Label.Trim());
            }
        }

        return result;
    }
}
