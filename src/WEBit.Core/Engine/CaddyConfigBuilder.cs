using System.Text.Json.Nodes;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Storage;

namespace WEBit.Core.Engine;

/// <summary>
/// Builds the Caddy JSON config. Sites on the same listener share one server and are routed by host name.
/// </summary>
/// <summary>Runtime context for the config: module endpoints and live reload folder.</summary>
public sealed record CaddyBuildOptions
{
    /// <summary>FastCGI address of the PHP module (127.0.0.1:port); null = PHP not available.</summary>
    public string? PhpFastCgi { get; init; }

    /// <summary>
    /// FastCGI address per PHP site (own PHP version, Xdebug); null = that PHP is not available.
    /// Not set: every PHP site uses <see cref="PhpFastCgi"/>.
    /// </summary>
    public Func<SiteDefinition, string?>? PhpUpstream { get; init; }

    /// <summary>Folder with _assets and one state.json per site.</summary>
    public string? LiveDir { get; init; }

    /// <summary>DNS suffix: host names without dot also get "host.suffix" in the certificate.</summary>
    public string? DnsSuffix { get; init; }

    /// <summary>Certificate names for HTTPS bindings on 0.0.0.0 (machine name, FQDN, IP addresses).</summary>
    public IReadOnlyList<string> WildcardNames { get; init; } = [];
}

public static class CaddyConfigBuilder
{
    /// <summary>Reserved URL prefix for live reload files.</summary>
    public const string LivePrefix = "/__webit";
    public const string AccessLoggerPrefix = "http.log.access.";

    public static string LoggerName(SiteDefinition site) => "site_" + site.Id.Replace('-', '_');

    public static JsonObject Build(WebitSettings settings, WebitPaths paths, IReadOnlyCollection<SiteDefinition> sites, CaddyBuildOptions? options = null)
    {
        options ??= new CaddyBuildOptions { LiveDir = paths.LiveDir };
        var logs = new JsonObject
        {
            ["default"] = new JsonObject
            {
                ["writer"] = FileWriter(paths.EngineLogFile, settings.Logging),
                ["level"] = "INFO",
                ["exclude"] = new JsonArray("http.log.access")
            }
        };

        var servers = new JsonObject();
        var serverIndex = 0;
        var tlsNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        var groups = sites
            .SelectMany(s => s.Bindings.Select(b => (Site: s, Binding: b)))
            .GroupBy(x => Endpoint.ListenKey(x.Binding.Address, x.Binding.Port), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var routes = new JsonArray();
            var loggerNames = new JsonObject();
            var routedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Host routes first …
            foreach (var item in group.Where(x => x.Binding.HasHost).OrderBy(x => x.Binding.Host, StringComparer.OrdinalIgnoreCase))
            {
                var host = item.Binding.Host!.Trim().ToLowerInvariant();
                if (!routedHosts.Add(host))
                {
                    continue;
                }

                routes.Add(BuildSiteRoute(item.Site, host, options));
                loggerNames[host] = new JsonArray(LoggerName(item.Site));
            }

            // … then at most one site without host name as default for this listener.
            string? defaultLogger = null;
            var catchAll = group.FirstOrDefault(x => !x.Binding.HasHost);
            if (catchAll.Site is not null)
            {
                routes.Add(BuildSiteRoute(catchAll.Site, null, options));
                defaultLogger = LoggerName(catchAll.Site);
            }
            else
            {
                // Unknown host names on this listener: 404.
                routes.Add(new JsonObject
                {
                    ["handle"] = new JsonArray(new JsonObject
                    {
                        ["handler"] = "static_response",
                        ["status_code"] = 404,
                        ["body"] = "404 Not Found"
                    }),
                    ["terminal"] = true
                });
            }

            var serverLogs = new JsonObject();
            if (loggerNames.Count > 0)
            {
                serverLogs["logger_names"] = loggerNames;
            }

            if (defaultLogger is not null)
            {
                serverLogs["default_logger_name"] = defaultLogger;
            }

            var server = new JsonObject
            {
                ["listen"] = new JsonArray(group.Key),
                ["routes"] = routes,
                ["automatic_https"] = new JsonObject { ["disable"] = true }
            };

            // HTTPS listener: certificates of the local CA; default_sni for calls by IP (no SNI).
            if (settings.HttpsEnabled && group.Any(x => x.Binding.Tls))
            {
                var names = new List<string>();
                foreach (var item in group)
                {
                    if (item.Binding.HasHost)
                    {
                        var host = item.Binding.Host!.Trim().ToLowerInvariant();
                        names.Add(host);
                        if (!host.Contains('.') && !string.IsNullOrWhiteSpace(options.DnsSuffix))
                        {
                            names.Add($"{host}.{options.DnsSuffix}");
                        }
                    }
                    else if (Endpoint.IsWildcard(item.Binding.Address))
                    {
                        names.AddRange(options.WildcardNames);
                    }
                    else
                    {
                        names.Add(item.Binding.Address.Trim());
                    }
                }

                names = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var policy = new JsonObject();
                // Only for a specific address: on 0.0.0.0 the certificate is chosen by the local IP of the connection.
                var defaultName = catchAll.Site is not null && !Endpoint.IsWildcard(catchAll.Binding.Address)
                    ? catchAll.Binding.Address.Trim()
                    : null;
                if (defaultName is not null)
                {
                    policy["default_sni"] = defaultName;
                }

                server["tls_connection_policies"] = new JsonArray(policy);
                tlsNames.UnionWith(names);
            }

            // Access logging only when switched on (Settings → Logs).
            if (settings.Logging.AccessLogs)
            {
                server["logs"] = serverLogs;
            }

            servers[$"srv{serverIndex++}"] = server;
        }

        foreach (var site in sites.Where(_ => settings.Logging.AccessLogs))
        {
            var name = LoggerName(site);
            logs[name] = new JsonObject
            {
                ["writer"] = FileWriter(paths.SiteAccessLog(site.Id), settings.Logging),
                ["encoder"] = new JsonObject { ["format"] = "json" },
                ["include"] = new JsonArray(AccessLoggerPrefix + name)
            };
        }

        var apps = new JsonObject();
        if (servers.Count > 0)
        {
            apps["http"] = new JsonObject { ["servers"] = servers };
        }

        if (settings.HttpsEnabled)
        {
            // Local CA (created on first start, kept in the storage folder); trust is installed from the app.
            apps["pki"] = new JsonObject
            {
                ["certificate_authorities"] = new JsonObject
                {
                    ["local"] = new JsonObject
                    {
                        ["name"] = "WEBit Local CA",
                        ["root_common_name"] = "WEBit Local CA",
                        ["intermediate_common_name"] = "WEBit Local CA Intermediate",
                        ["install_trust"] = false
                    }
                }
            };

            if (tlsNames.Count > 0)
            {
                apps["tls"] = new JsonObject
                {
                    ["automation"] = new JsonObject
                    {
                        ["policies"] = new JsonArray(new JsonObject
                        {
                            ["subjects"] = ToArray(tlsNames),
                            ["issuers"] = new JsonArray(new JsonObject { ["module"] = "internal", ["ca"] = "local" })
                        })
                    },
                    ["certificates"] = new JsonObject { ["automate"] = ToArray(tlsNames) }
                };
            }
        }

        return new JsonObject
        {
            ["admin"] = new JsonObject
            {
                ["listen"] = $"127.0.0.1:{settings.EngineAdminPort}",
                ["config"] = new JsonObject { ["persist"] = false }
            },
            ["storage"] = new JsonObject
            {
                ["module"] = "file_system",
                ["root"] = Path.Combine(paths.EngineRuntimeDir, "data")
            },
            ["logging"] = new JsonObject { ["logs"] = logs },
            ["apps"] = apps
        };
    }

    private static JsonObject BuildSiteRoute(SiteDefinition site, string? host, CaddyBuildOptions options)
    {
        var inner = new JsonArray();

        if (site.Access.AllowNetworks.Count > 0)
        {
            inner.Add(new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject
                {
                    ["not"] = new JsonArray(new JsonObject
                    {
                        ["remote_ip"] = new JsonObject { ["ranges"] = ToArray(site.Access.AllowNetworks) }
                    })
                }),
                ["handle"] = new JsonArray(new JsonObject
                {
                    ["handler"] = "static_response",
                    ["status_code"] = 403,
                    ["body"] = "403 Forbidden"
                }),
                ["terminal"] = true
            });
        }

        if (site.HasLiveView && options.LiveDir is not null)
        {
            AddLiveRoutes(inner, site, options.LiveDir);
        }

        inner.Add(new JsonObject
        {
            ["handle"] = site.Type switch
            {
                SiteType.Redirect => RedirectHandlers(site),
                SiteType.Node or SiteType.Python or SiteType.Proxy => ProxyHandlers(site),
                _ => StaticHandlers(site, options)
            }
        });

        var route = new JsonObject
        {
            ["handle"] = new JsonArray(new JsonObject
            {
                ["handler"] = "subroute",
                ["routes"] = inner
            }),
            ["terminal"] = true
        };

        if (host is not null)
        {
            route["match"] = new JsonArray(new JsonObject { ["host"] = new JsonArray(host) });
        }

        return route;
    }

    /// <summary>
    /// /__webit/state.json → the site's change counter, /__webit/* → shared live reload scripts.
    /// </summary>
    private static void AddLiveRoutes(JsonArray inner, SiteDefinition site, string liveDir)
    {
        var noStore = new JsonObject
        {
            ["handler"] = "headers",
            ["response"] = new JsonObject
            {
                ["set"] = new JsonObject { ["Cache-Control"] = new JsonArray("no-store") }
            }
        };

        inner.Add(new JsonObject
        {
            ["match"] = new JsonArray(new JsonObject { ["path"] = new JsonArray(LivePrefix + "/state.json") }),
            ["handle"] = new JsonArray(
                noStore.DeepClone(),
                new JsonObject { ["handler"] = "rewrite", ["uri"] = "/" + site.Id + "/state.json" },
                new JsonObject { ["handler"] = "file_server", ["root"] = ForwardSlashes(liveDir) }),
            ["terminal"] = true
        });

        inner.Add(new JsonObject
        {
            ["match"] = new JsonArray(new JsonObject { ["path"] = new JsonArray(LivePrefix + "/*") }),
            ["handle"] = new JsonArray(
                noStore.DeepClone(),
                new JsonObject { ["handler"] = "rewrite", ["strip_path_prefix"] = LivePrefix },
                new JsonObject { ["handler"] = "file_server", ["root"] = ForwardSlashes(Path.Combine(liveDir, "_assets")) }),
            ["terminal"] = true
        });
    }

    private static JsonArray StaticHandlers(SiteDefinition site, CaddyBuildOptions options)
    {
        var handlers = new JsonArray();

        // Quick testing: edits must show up immediately.
        if (site.HasLiveView)
        {
            handlers.Add(new JsonObject
            {
                ["handler"] = "headers",
                ["response"] = new JsonObject
                {
                    ["set"] = new JsonObject { ["Cache-Control"] = new JsonArray("no-store, must-revalidate") },
                    ["delete"] = new JsonArray("ETag", "Last-Modified"),
                    ["deferred"] = true
                }
            });
        }

        // Override the HTML charset (Caddy defaults to UTF-8).
        // PHP sets its own charset (default_charset UTF-8); "Auto" must not strip it.
        var contentType = site.Type == SiteType.Php && site.Content.Charset == CharsetMode.Auto
            ? null
            : ContentTypeFor(site.Content.Charset);
        if (contentType is not null)
        {
            handlers.Add(new JsonObject
            {
                ["handler"] = "headers",
                ["response"] = new JsonObject
                {
                    ["set"] = new JsonObject { ["Content-Type"] = new JsonArray(contentType) },
                    ["require"] = new JsonObject
                    {
                        ["headers"] = new JsonObject { ["Content-Type"] = new JsonArray("text/html*") }
                    },
                    ["deferred"] = true
                }
            });
        }

        handlers.Add(new JsonObject
        {
            ["handler"] = "headers",
            ["response"] = new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["X-Content-Type-Options"] = new JsonArray("nosniff"),
                    ["Referrer-Policy"] = new JsonArray("same-origin")
                },
                ["delete"] = new JsonArray("Server"),
                ["deferred"] = true
            }
        });

        if (site.Content.Compression)
        {
            handlers.Add(new JsonObject
            {
                ["handler"] = "encode",
                ["encodings"] = new JsonObject
                {
                    ["zstd"] = new JsonObject(),
                    ["gzip"] = new JsonObject()
                },
                ["prefer"] = new JsonArray("zstd", "gzip")
            });
        }

        var indexNames = site.Type == SiteType.Php
            ? new[] { "index.php" }.Concat(site.Content.DefaultDocuments.Where(d => !d.Equals("index.php", StringComparison.OrdinalIgnoreCase)))
            : site.Content.DefaultDocuments;

        if (site.Type == SiteType.Php)
        {
            // Without the PHP module never deliver PHP source code.
            var upstream = options.PhpUpstream is { } resolve ? resolve(site) : options.PhpFastCgi;
            handlers.Add(upstream is not null ? PhpSubroute(site, upstream) : PhpUnavailable(site.PhpVersion));
        }

        var fileServer = new JsonObject
        {
            ["handler"] = "file_server",
            ["root"] = site.Root,
            ["index_names"] = ToArray(indexNames)
        };

        // Static sites never deliver PHP source code (config files with passwords etc.).
        var hidden = site.Content.HiddenPaths.ToList();
        if (site.Type == SiteType.Static)
        {
            hidden.Add("*.php");
        }

        if (hidden.Count > 0)
        {
            fileServer["hide"] = ToArray(hidden);
        }

        if (site.Content.DirectoryBrowsing)
        {
            fileServer["browse"] = new JsonObject();
        }

        handlers.Add(fileServer);
        return handlers;
    }

    /// <summary>Equivalent of Caddy's php_fastcgi directive: trailing-slash redirect, try_files, FastCGI for *.php.</summary>
    private static JsonObject PhpSubroute(SiteDefinition site, string fastCgi)
    {
        var root = ForwardSlashes(site.Root);
        var routes = new JsonArray
        {
            // /dir → /dir/ when /dir/index.php exists.
            new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject
                {
                    ["file"] = new JsonObject
                    {
                        ["root"] = root,
                        ["try_files"] = new JsonArray("{http.request.uri.path}/index.php")
                    },
                    ["not"] = new JsonArray(new JsonObject { ["path"] = new JsonArray("*/") })
                }),
                ["handle"] = new JsonArray(new JsonObject
                {
                    ["handler"] = "static_response",
                    ["status_code"] = 308,
                    ["headers"] = new JsonObject { ["Location"] = new JsonArray("{http.request.orig_uri.path}/") }
                }),
                ["terminal"] = true
            },

            // Existing file, folder index or front controller (index.php).
            new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject
                {
                    ["file"] = new JsonObject
                    {
                        ["root"] = root,
                        ["try_files"] = new JsonArray("{http.request.uri.path}", "{http.request.uri.path}/index.php", "index.php"),
                        ["split_path"] = new JsonArray(".php")
                    }
                }),
                ["handle"] = new JsonArray(new JsonObject
                {
                    ["handler"] = "rewrite",
                    ["uri"] = "{http.matchers.file.relative}"
                })
            },

            new JsonObject
            {
                ["match"] = new JsonArray(new JsonObject { ["path"] = new JsonArray("*.php") }),
                ["handle"] = new JsonArray(new JsonObject
                {
                    ["handler"] = "reverse_proxy",
                    ["transport"] = new JsonObject
                    {
                        ["protocol"] = "fastcgi",
                        ["root"] = root,
                        ["split_path"] = new JsonArray(".php")
                    },
                    ["upstreams"] = new JsonArray(new JsonObject { ["dial"] = fastCgi })
                }),
                ["terminal"] = true
            }
        };

        return new JsonObject { ["handler"] = "subroute", ["routes"] = routes };
    }

    private static JsonObject PhpUnavailable(string? version = null) => new()
    {
        ["handler"] = "subroute",
        ["routes"] = new JsonArray(new JsonObject
        {
            ["match"] = new JsonArray(new JsonObject { ["path"] = new JsonArray("*.php", "*/") }),
            ["handle"] = new JsonArray(new JsonObject
            {
                ["handler"] = "static_response",
                ["status_code"] = 503,
                ["body"] = version is null
                    ? "503 – the WEBit PHP module is not running."
                    : $"503 – PHP {version} is not installed or not running (WEBit → Dependencies)."
            }),
            ["terminal"] = true
        })
    };

    /// <summary>
    /// Node.js/Python apps: reverse proxy to the app port on 127.0.0.1 (WebSockets for HMR included), Host = 127.0.0.1:port.
    /// Proxy sites: to the target; foreign hosts get their own host name in the Host header.
    /// </summary>
    private static JsonArray ProxyHandlers(SiteDefinition site)
    {
        string dial;
        var tls = false;
        var rewriteHost = false;
        if (site.IsApp)
        {
            if (site.App.Port is < 1 or > 65535)
            {
                return new JsonArray(new JsonObject
                {
                    ["handler"] = "static_response",
                    ["status_code"] = 503,
                    ["body"] = "503 – the app has no port."
                });
            }

            dial = $"127.0.0.1:{site.App.Port}";

            // Dev servers only trust local host names (Vite allowedHosts, Django ALLOWED_HOSTS in DEBUG);
            // the original host still arrives as X-Forwarded-Host.
            rewriteHost = true;
        }
        else
        {
            var uri = new Uri(site.ProxyTarget!);
            dial = $"{uri.Host}:{uri.Port}";
            tls = uri.Scheme == Uri.UriSchemeHttps;
            rewriteHost = !uri.IsLoopback;
        }

        var proxy = new JsonObject
        {
            ["handler"] = "reverse_proxy",
            ["upstreams"] = new JsonArray(new JsonObject { ["dial"] = dial }),
            // Stream at once: server-sent events and dev server output.
            ["flush_interval"] = -1
        };

        if (tls)
        {
            proxy["transport"] = new JsonObject { ["protocol"] = "http", ["tls"] = new JsonObject() };
        }

        if (rewriteHost)
        {
            proxy["headers"] = new JsonObject
            {
                ["request"] = new JsonObject
                {
                    ["set"] = new JsonObject { ["Host"] = new JsonArray("{http.reverse_proxy.upstream.hostport}") }
                }
            };
        }

        return new JsonArray(proxy);
    }

    private static string ForwardSlashes(string path) => path.Replace('\\', '/');

    private static JsonArray RedirectHandlers(SiteDefinition site) =>
        new(new JsonObject
        {
            ["handler"] = "static_response",
            ["status_code"] = site.RedirectPermanent ? 301 : 302,
            ["headers"] = new JsonObject { ["Location"] = new JsonArray(site.RedirectTarget ?? "/") }
        });

    /// <summary>HTML Content-Type; null = Caddy default (UTF-8).</summary>
    public static string? ContentTypeFor(CharsetMode mode) => mode switch
    {
        CharsetMode.Auto => "text/html",
        CharsetMode.Windows1252 => "text/html; charset=windows-1252",
        CharsetMode.Iso88591 => "text/html; charset=iso-8859-1",
        _ => null
    };

    private static JsonObject FileWriter(string path, LogSettings logging) => new()
    {
        ["output"] = "file",
        ["filename"] = path,
        ["roll_size_mb"] = logging.MaxSizeMb,
        // Caddy treats 0 as "unset" (defaults to 10): keep at least one archive.
        ["roll_keep"] = Math.Max(1, logging.Keep)
    };

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            array.Add(v.Trim());
        }

        return array;
    }
}
