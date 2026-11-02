using System.Text.RegularExpressions;
using WEBit.Core.Models;
using WEBit.Core.Net;

namespace WEBit.Core.Sites;

/// <summary>
/// Format and conflict checks. Environment checks (folder, ports) run in the service.
/// </summary>
public static partial class SiteValidator
{
    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostRegex();

    public static bool IsValidHost(string? host) => !string.IsNullOrWhiteSpace(host) && HostRegex().IsMatch(host.Trim());

    /// <summary>Errors that block saving.</summary>
    public static List<ValidationIssue> ValidateFormat(SiteDefinition site)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(site.Name))
        {
            issues.Add(ValidationIssue.Error(MessageCodes.NameRequired, "name"));
        }

        if (site.HasRoot && string.IsNullOrWhiteSpace(site.Root))
        {
            issues.Add(ValidationIssue.Error(MessageCodes.RootRequired, "root"));
        }

        if (site.Type == SiteType.Redirect &&
            !(Uri.TryCreate(site.RedirectTarget, UriKind.Absolute, out var target) && (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps)))
        {
            issues.Add(ValidationIssue.Error(MessageCodes.RedirectTargetInvalid, "redirect"));
        }

        if (site.Type == SiteType.Proxy && !IsValidProxyTarget(site.ProxyTarget))
        {
            issues.Add(ValidationIssue.Error(MessageCodes.ProxyTargetInvalid, "proxy", site.ProxyTarget ?? ""));
        }

        if (!string.IsNullOrWhiteSpace(site.PublicUrl) &&
            !(Uri.TryCreate(site.PublicUrl, UriKind.Absolute, out var pub) && (pub.Scheme == Uri.UriSchemeHttp || pub.Scheme == Uri.UriSchemeHttps)))
        {
            issues.Add(ValidationIssue.Error(MessageCodes.InvalidPublicUrl, "publicUrl", site.PublicUrl));
        }

        if (site.Bindings.Count == 0)
        {
            issues.Add(ValidationIssue.Error(MessageCodes.NoBinding, "bindings"));
        }

        for (var i = 0; i < site.Bindings.Count; i++)
        {
            var b = site.Bindings[i];
            var field = $"binding:{i}";
            if (!Endpoint.TryParse(b.Address, out _))
            {
                issues.Add(ValidationIssue.Error(MessageCodes.InvalidAddress, field, b.Address));
            }

            if (!PortPolicy.IsValidPort(b.Port))
            {
                issues.Add(ValidationIssue.Error(MessageCodes.InvalidPort, field, b.Port));
            }

            if (b.HasHost && !IsValidHost(b.Host))
            {
                issues.Add(ValidationIssue.Error(MessageCodes.InvalidHost, field, b.Host));
            }
        }

        foreach (var net in site.Access.AllowNetworks)
        {
            if (!Endpoint.IsValidNetwork(net))
            {
                issues.Add(ValidationIssue.Error(MessageCodes.InvalidNetwork, "access", net));
            }
        }

        return issues;
    }

    /// <summary>Full check incl. warnings and conflicts with other sites.</summary>
    public static List<ValidationIssue> Validate(SiteDefinition site, IEnumerable<SiteDefinition> otherSites, WebitSettings settings)
    {
        var issues = ValidateFormat(site);
        var others = otherSites.Where(o => !string.Equals(o.Id, site.Id, StringComparison.OrdinalIgnoreCase)).ToList();

        for (var i = 0; i < site.Bindings.Count; i++)
        {
            var b = site.Bindings[i];
            var field = $"binding:{i}";
            if (!PortPolicy.IsValidPort(b.Port) || !Endpoint.TryParse(b.Address, out _))
            {
                continue;
            }

            var endpoint = Endpoint.Display(b.Address, b.Port);

            // Ports of WEBit itself (modules, Adminer, admin API) share the same Caddy or loopback.
            if (b.Port == settings.EngineAdminPort || b.Port == settings.MailpitSmtpPort || b.Port == settings.MailpitUiPort
                || b.Port == settings.PhpFastCgiPort || b.Port == settings.MariaDbPort || b.Port == settings.AdminerPort
                || settings.PhpPoolPorts.Contains(b.Port))
            {
                issues.Add(ValidationIssue.Error(MessageCodes.ModulePort, field, b.Port));
            }

            if (b.Tls && !settings.HttpsEnabled)
            {
                issues.Add(ValidationIssue.Error(MessageCodes.HttpsDisabled, field, endpoint));
            }

            if (Endpoint.IsWildcard(b.Address))
            {
                issues.Add(ValidationIssue.Warning(MessageCodes.WildcardBinding, field, endpoint));
            }
            else if (Endpoint.IsLoopback(b.Address))
            {
                issues.Add(ValidationIssue.Warning(MessageCodes.LoopbackBinding, field, endpoint));
            }

            if (PortPolicy.AvoidReason(b.Port) is { } avoided)
            {
                issues.Add(ValidationIssue.Warning(MessageCodes.PortAvoided, field, b.Port, avoided.Reason));
            }
            else if (!PortPolicy.AllowedWellKnown.Contains(b.Port) && (b.Port < settings.SitePortStart || b.Port > settings.SitePortEnd))
            {
                issues.Add(ValidationIssue.Info(MessageCodes.PortOutsideRange, field, b.Port, $"{settings.SitePortStart}–{settings.SitePortEnd}"));
            }

            // Duplicate binding within the same site.
            for (var j = 0; j < i; j++)
            {
                var prev = site.Bindings[j];
                if (prev.Port == b.Port && Endpoint.SameAddress(prev.Address, b.Address) && prev.Tls != b.Tls)
                {
                    issues.Add(ValidationIssue.Error(MessageCodes.TlsMismatch, field, endpoint, site.Name));
                }

                if (prev.Port == b.Port && Endpoint.SameAddress(prev.Address, b.Address) &&
                    string.Equals(prev.Host, b.Host, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(ValidationIssue.Warning(MessageCodes.DuplicateBinding, field, endpoint));
                }
            }

            foreach (var other in others)
            {
                foreach (var ob in other.Bindings)
                {
                    if (ob.Port != b.Port || !Endpoint.Overlaps(ob.Address, b.Address))
                    {
                        continue;
                    }

                    // One listener is either HTTPS or HTTP.
                    if (ob.Tls != b.Tls && Endpoint.SameAddress(ob.Address, b.Address))
                    {
                        issues.Add(ValidationIssue.Error(MessageCodes.TlsMismatch, field, endpoint, other.Name));
                        continue;
                    }

                    if (!Endpoint.SameAddress(ob.Address, b.Address))
                    {
                        // 0.0.0.0:P and 10.0.0.5:P cannot be bound at the same time.
                        issues.Add(ValidationIssue.Error(MessageCodes.BindingOverlap, field, endpoint, Endpoint.Display(ob.Address, ob.Port), other.Name));
                        continue;
                    }

                    var bothWithoutHost = !b.HasHost && !ob.HasHost;
                    var sameHost = b.HasHost && ob.HasHost && string.Equals(b.Host, ob.Host, StringComparison.OrdinalIgnoreCase);
                    if (bothWithoutHost || sameHost)
                    {
                        issues.Add(ValidationIssue.Error(MessageCodes.BindingConflict, field, b.HasHost ? $"{b.Host} ({endpoint})" : endpoint, other.Name));
                    }
                }
            }
        }

        return issues;
    }

    /// <summary>http(s)://host[:port][/] – Caddy proxies to a host, not to a sub path.</summary>
    public static bool IsValidProxyTarget(string? target) =>
        Uri.TryCreate(target?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query);

    public static bool HasErrors(IEnumerable<ValidationIssue> issues) => issues.Any(i => i.Level == IssueLevel.Error);
}
