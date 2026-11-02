using System.Text.Json.Nodes;
using WEBit.Core.Apps;
using WEBit.Core.Engine;
using WEBit.Core.Models;
using WEBit.Core.Net;
using WEBit.Core.Sites;
using WEBit.Core.Storage;
using WEBit.Core.Urls;

namespace WEBit.Core.Tests;

public class SiteIdTests
{
    [Theory]
    [InlineData("Handbuch Produkt X", "handbuch-produkt-x")]
    [InlineData("Übersicht & Größen", "uebersicht-groessen")]
    [InlineData("   ", "site")]
    [InlineData("../../etc", "etc")]
    public void Slugify_creates_safe_ids(string name, string expected) =>
        Assert.Equal(expected, SiteIds.Slugify(name));

    [Fact]
    public void CreateUnique_appends_counter()
    {
        var id = SiteIds.CreateUnique("Doku", ["doku", "doku-2"]);
        Assert.Equal("doku-3", id);
    }

    [Fact]
    public void EnsureSafe_rejects_path_traversal() =>
        Assert.Throws<ArgumentException>(() => SiteIds.EnsureSafe("..\\x"));
}

public class PortPolicyTests
{
    [Theory]
    [InlineData(8080)]
    [InlineData(8081)]
    [InlineData(5000)]
    [InlineData(7001)]
    [InlineData(50000)]
    public void Avoids_standard_and_dynamic_ports(int port) =>
        Assert.NotNull(PortPolicy.AvoidReason(port));

    [Theory]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(47100)]
    public void Allows_dedicated_and_webit_ports(int port) =>
        Assert.Null(PortPolicy.AvoidReason(port));

    [Fact]
    public void Suggest_skips_used_ports()
    {
        var used = new HashSet<int> { 47100, 47101 };
        Assert.Equal(47102, PortPolicy.Suggest(47100, 47199, p => !used.Contains(p)));
    }
}

public class ValidatorTests
{
    private static SiteDefinition Site(string id, string address, int port, string? host = null) => new()
    {
        Id = id,
        Name = id,
        Root = "C:\\www\\" + id,
        Bindings = [new SiteBinding { Address = address, Port = port, Host = host }]
    };

    [Fact]
    public void Same_endpoint_without_host_conflicts()
    {
        var issues = SiteValidator.Validate(Site("a", "10.0.0.5", 47100), [Site("b", "10.0.0.5", 47100)], new WebitSettings());
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.Message.Code == MessageCodes.BindingConflict);
    }

    [Fact]
    public void Same_endpoint_with_different_hosts_is_allowed()
    {
        var issues = SiteValidator.Validate(Site("a", "10.0.0.5", 80, "doku.intern"), [Site("b", "10.0.0.5", 80, "wiki.intern")], new WebitSettings());
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Wildcard_and_specific_address_overlap()
    {
        var issues = SiteValidator.Validate(Site("a", "0.0.0.0", 47100), [Site("b", "10.0.0.5", 47100, "x.intern")], new WebitSettings());
        Assert.Contains(issues, i => i.Message.Code == MessageCodes.BindingOverlap);
    }

    [Fact]
    public void Invalid_host_is_rejected()
    {
        var issues = SiteValidator.ValidateFormat(Site("a", "10.0.0.5", 47100, "bad host!"));
        Assert.Contains(issues, i => i.Message.Code == MessageCodes.InvalidHost);
    }
}

public class CaddyConfigTests
{
    [Fact]
    public void Sites_sharing_a_listener_use_one_server_with_host_routes()
    {
        var paths = WebitPaths.ForDataRoot(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "webit-test"));
        var a = new SiteDefinition { Id = "doku", Name = "Doku", Root = "C:\\www\\doku", Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 80, Host = "doku.intern" }] };
        var b = new SiteDefinition { Id = "wiki", Name = "Wiki", Root = "C:\\www\\wiki", Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 80, Host = "wiki.intern" }] };

        var config = CaddyConfigBuilder.Build(new WebitSettings(), paths, [a, b]);
        var servers = config["apps"]!["http"]!["servers"]!.AsObject();

        Assert.Single(servers);
        var server = servers.First().Value!.AsObject();
        Assert.Equal("10.0.0.5:80", server["listen"]![0]!.GetValue<string>());
        Assert.Equal(3, server["routes"]!.AsArray().Count); // 2 host routes + 404 fallback
        Assert.Equal("127.0.0.1:47299", config["admin"]!["listen"]!.GetValue<string>());
    }

    [Fact]
    public void Legacy_charset_sets_html_content_type()
    {
        Assert.Equal("text/html; charset=windows-1252", CaddyConfigBuilder.ContentTypeFor(CharsetMode.Windows1252));
        Assert.Equal("text/html", CaddyConfigBuilder.ContentTypeFor(CharsetMode.Auto));
        Assert.Null(CaddyConfigBuilder.ContentTypeFor(CharsetMode.Utf8));
    }

    [Fact]
    public void Empty_site_list_produces_valid_config()
    {
        var paths = WebitPaths.ForDataRoot(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "webit-test"));
        var config = CaddyConfigBuilder.Build(new WebitSettings(), paths, []);
        Assert.IsType<JsonObject>(config["apps"]);
        Assert.Null(config["apps"]!["http"]);
    }
}

public class UrlTests
{
    [Fact]
    public void Port_80_is_omitted_and_host_is_used()
    {
        var url = UrlBuilder.ForBinding(new SiteBinding { Address = "10.0.0.5", Port = 80, Host = "Doku.Intern" }, new UrlContext("srv01.firma.intern", null));
        Assert.Equal("http://doku.intern/", url);
    }

    [Fact]
    public void Wildcard_binding_never_uses_localhost()
    {
        var url = UrlBuilder.ForBinding(new SiteBinding { Address = "0.0.0.0", Port = 47100 }, new UrlContext("srv01.firma.intern", null));
        Assert.Equal("http://srv01.firma.intern:47100/", url);
    }

    [Fact]
    public void Public_url_wins_as_primary()
    {
        var site = new SiteDefinition
        {
            PublicUrl = "https://doku.firma.de/",
            Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100 }]
        };
        Assert.Equal("https://doku.firma.de/", UrlBuilder.Primary(site, new UrlContext(null, null)));
    }

    [Fact]
    public void Message_template_replaces_placeholders()
    {
        var text = LinkExport.RenderMessage("Hallo {SiteName}: {Url}\n\n\n{Description}", "Doku", "http://doku/", null);
        Assert.Equal("Hallo Doku: http://doku/", text);
    }

    [Fact]
    public void Bookmarks_are_html_encoded()
    {
        var html = LinkExport.BookmarksHtml([new ExportLink("A & B", "http://x/?a=1&b=2")]);
        Assert.Contains("A &amp; B", html);
        Assert.Contains("a=1&amp;b=2", html);
    }
}

public class SettingsTests
{
    [Fact]
    public void Defaults_enable_web_server_and_php()
    {
        var settings = new WebitSettings();
        Assert.True(settings.IsModuleEnabled(ModuleIds.WebServer));
        Assert.True(settings.IsModuleEnabled(ModuleIds.Php));
        Assert.True(settings.AutoSites);
        Assert.False(settings.IsModuleEnabled(ModuleIds.Mailpit));
        Assert.False(settings.IsModuleEnabled(ModuleIds.MariaDb));
        Assert.False(settings.IsModuleEnabled(ModuleIds.Node));
        Assert.False(settings.MariaDbNetworkAccess);
        Assert.Equal(47204, settings.MariaDbPort);
        Assert.False(settings.IsModuleEnabled("unknown"));
    }

    [Fact]
    public void Normalize_merges_defaults_case_insensitive()
    {
        var settings = new WebitSettings { Modules = new() { ["MAILPIT"] = true } };
        settings.Normalize();
        Assert.True(settings.IsModuleEnabled(ModuleIds.Mailpit));
        Assert.True(settings.IsModuleEnabled(ModuleIds.WebServer));
    }

    [Fact]
    public void Normalize_repairs_mailpit_port_clash()
    {
        var settings = new WebitSettings { MailpitSmtpPort = 47210, MailpitUiPort = 47210 };
        settings.Normalize();
        Assert.Equal(47202, settings.MailpitUiPort);
    }
}

public class SiteIdSafetyTests
{
    [Theory]
    [InlineData("Doku")]
    [InlineData("doku-")]
    [InlineData("-doku")]
    [InlineData("a--b")]
    [InlineData("a/b")]
    [InlineData("")]
    public void EnsureSafe_rejects_invalid(string id) =>
        Assert.Throws<ArgumentException>(() => SiteIds.EnsureSafe(id));

    [Fact]
    public void EnsureSafe_rejects_too_long() =>
        Assert.Throws<ArgumentException>(() => SiteIds.EnsureSafe(new string('a', 65)));

    [Fact]
    public void CreateUnique_stays_within_limit()
    {
        var id = SiteIds.CreateUnique(new string('x', 120), []);
        Assert.True(id.Length <= 64);
        Assert.Equal(id, SiteIds.EnsureSafe(id));
    }
}

public class QuickTestConfigTests
{
    private static readonly WebitPaths Paths = WebitPaths.ForDataRoot(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "webit-test"));

    private static SiteDefinition Site(SiteType type, bool live) => new()
    {
        Id = "shop",
        Name = "Shop",
        Type = type,
        Root = "C:\\www\\shop",
        LiveReload = live,
        Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100 }]
    };

    private static string Json(JsonObject config) => config.ToJsonString();

    [Fact]
    public void Php_site_routes_php_files_to_fastcgi()
    {
        var config = CaddyConfigBuilder.Build(new WebitSettings(), Paths, [Site(SiteType.Php, false)],
            new CaddyBuildOptions { PhpFastCgi = "127.0.0.1:47203", LiveDir = Paths.LiveDir });
        var json = Json(config);
        Assert.Contains("\"protocol\":\"fastcgi\"", json);
        Assert.Contains("127.0.0.1:47203", json);
        Assert.Contains("C:/www/shop", json);
    }

    [Fact]
    public void Php_site_without_module_never_serves_source()
    {
        var json = Json(CaddyConfigBuilder.Build(new WebitSettings(), Paths, [Site(SiteType.Php, false)], new CaddyBuildOptions()));
        Assert.DoesNotContain("fastcgi", json);
        Assert.Contains("503", json);
    }

    [Fact]
    public void Live_reload_adds_state_route_and_no_store()
    {
        var json = Json(CaddyConfigBuilder.Build(new WebitSettings(), Paths, [Site(SiteType.Static, true)],
            new CaddyBuildOptions { LiveDir = Paths.LiveDir }));
        Assert.Contains("/__webit/state.json", json);
        Assert.Contains("/shop/state.json", json);
        Assert.Contains("no-store", json);
    }

    [Fact]
    public void Static_site_without_live_reload_has_no_live_routes()
    {
        var json = Json(CaddyConfigBuilder.Build(new WebitSettings(), Paths, [Site(SiteType.Static, false)]));
        Assert.DoesNotContain("__webit", json);
    }

    [Theory]
    [InlineData("http://10.0.0.5:47100/", "http://10.0.0.5:47100/__webit/live.html")]
    [InlineData("http://docs.company.local/handbook/", "http://docs.company.local/__webit/live.html#/handbook/")]
    public void Live_url_uses_site_origin(string primary, string expected) =>
        Assert.Equal(expected, UrlBuilder.LiveUrl(primary));
}

public class AppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "webit-app-" + Guid.NewGuid().ToString("N")[..8]);

    public AppTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SiteDefinition App(SiteType type) => new()
    {
        Id = "app",
        Name = "App",
        Type = type,
        Root = _root,
        App = new SiteAppOptions { Port = 47210 },
        Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100 }]
    };

    [Fact]
    public void Vite_dev_script_gets_port_and_ipv4_loopback()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"), """{ "scripts": { "dev": "vite", "build": "vite build" } }""");
        var launch = AppCommandResolver.Resolve(App(SiteType.Node), 47210)!;
        Assert.Equal(AppProgram.Npm, launch.Program);
        Assert.Equal(new[] { "run", "dev", "--", "--port", "47210", "--strictPort", "--host", "127.0.0.1" }, launch.Arguments);
    }

    [Fact]
    public void Plain_server_file_runs_with_node()
    {
        File.WriteAllText(Path.Combine(_root, "server.js"), "require('http').createServer().listen(process.env.PORT)");
        var launch = AppCommandResolver.Resolve(App(SiteType.Node), 47210)!;
        Assert.Equal(AppProgram.Node, launch.Program);
        Assert.Equal(new[] { "server.js" }, launch.Arguments);
        Assert.Equal(SiteType.Node, AppCommandResolver.DetectType(_root));
    }

    [Fact]
    public void Own_node_command_runs_through_the_shell()
    {
        var site = App(SiteType.Node);
        site.App.Command = "npm run serve -- --port %PORT%";
        var launch = AppCommandResolver.Resolve(site, 47210)!;
        Assert.Equal(AppProgram.Shell, launch.Program);
        Assert.Equal("npm run serve -- --port %PORT%", launch.CommandLine);
    }

    [Fact]
    public void Fastapi_app_starts_with_uvicorn()
    {
        File.WriteAllText(Path.Combine(_root, "main.py"), "from fastapi import FastAPI\napi = FastAPI()\n");
        var launch = AppCommandResolver.Resolve(App(SiteType.Python), 47211)!;
        Assert.Equal(AppProgram.Uv, launch.Program);
        Assert.Contains("uvicorn", launch.Arguments);
        Assert.Contains("main:api", launch.Arguments);
        Assert.Contains("--with", launch.Arguments);
    }

    [Fact]
    public void Flask_project_with_requirements_uses_them()
    {
        File.WriteAllText(Path.Combine(_root, "app.py"), "from flask import Flask\napp = Flask(__name__)\n");
        File.WriteAllText(Path.Combine(_root, "requirements.txt"), "flask\n");
        var site = App(SiteType.Python);
        site.App.PythonVersion = "3.13";
        var args = AppCommandResolver.Resolve(site, 47212)!.Arguments;
        Assert.Equal(new[] { "run", "--python", "3.13", "--with-requirements", "requirements.txt", "flask", "--app", "app", "run",
            "--host", "127.0.0.1", "--port", "47212", "--debug", "--no-debugger" }, args);
    }

    [Fact]
    public void Django_uses_runserver_on_loopback()
    {
        File.WriteAllText(Path.Combine(_root, "manage.py"), "");
        var args = AppCommandResolver.Resolve(App(SiteType.Python), 47213)!.Arguments;
        Assert.Contains("runserver", args);
        Assert.Contains("127.0.0.1:47213", args);
    }

    [Fact]
    public void Empty_folder_has_nothing_to_start() =>
        Assert.Null(AppCommandResolver.Resolve(App(SiteType.Node), 47210));

    [Theory]
    [InlineData("uvicorn \"my app:app\" --reload", new[] { "uvicorn", "my app:app", "--reload" })]
    [InlineData("  python   run.py ", new[] { "python", "run.py" })]
    public void Tokenize_respects_quotes(string command, string[] expected) =>
        Assert.Equal(expected, AppCommandResolver.Tokenize(command));

    [Fact]
    public void App_site_is_proxied_to_its_port()
    {
        var paths = WebitPaths.ForDataRoot(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "webit-test"));
        var json = CaddyConfigBuilder.Build(new WebitSettings(), paths, [App(SiteType.Node)]).ToJsonString();
        Assert.Contains("\"reverse_proxy\"", json);
        Assert.Contains("127.0.0.1:47210", json);
        Assert.DoesNotContain("file_server", json);
    }

    [Fact]
    public void Proxy_to_foreign_host_rewrites_host_header()
    {
        var site = App(SiteType.Proxy);
        site.ProxyTarget = "https://intranet.company.local/";
        var paths = WebitPaths.ForDataRoot(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "webit-test"));
        var json = CaddyConfigBuilder.Build(new WebitSettings(), paths, [site]).ToJsonString();
        Assert.Contains("intranet.company.local:443", json);
        Assert.Contains("upstream.hostport", json);
        Assert.Contains("\"tls\"", json);
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000/", true)]
    [InlineData("http://127.0.0.1:3000", true)]
    [InlineData("http://127.0.0.1:3000/app", false)]
    [InlineData("ftp://x/", false)]
    [InlineData("", false)]
    public void Proxy_target_must_be_a_host(string target, bool valid) =>
        Assert.Equal(valid, SiteValidator.IsValidProxyTarget(target));
}

public class V008Tests
{
    [Theory]
    [InlineData("Test", "Test")]
    [InlineData("My Shop: Test", "My Shop- Test")]
    [InlineData("  ", "site")]
    [InlineData("CON", "site")]
    [InlineData("Docs.", "Docs")]
    public void Folder_follows_site_name(string name, string expected) =>
        Assert.Equal(expected, SiteFolders.FromName(name));

    [Fact]
    public void Random_color_prefers_unused_palette_colors()
    {
        var used = SiteColors.Palette.Skip(1).ToList();
        Assert.Equal(SiteColors.Palette[0], SiteColors.Pick(used));
        Assert.True(SiteColors.IsRandom(new SiteDefinition().Color));
    }

    [Fact]
    public void Https_binding_uses_https_url_without_default_port()
    {
        var url = UrlBuilder.ForBinding(new SiteBinding { Address = "10.0.0.5", Port = 443, Tls = true }, new UrlContext(null, null));
        Assert.Equal("https://10.0.0.5/", url);
    }

    [Fact]
    public void Https_site_gets_tls_policy_and_local_ca()
    {
        var paths = WebitPaths.ForDataRoot(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "webit-test"));
        var site = new SiteDefinition
        {
            Id = "secure", Name = "Secure", Type = SiteType.Static, Root = "C:\\www\\secure",
            Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100, Tls = true }]
        };
        var json = CaddyConfigBuilder.Build(new WebitSettings { HttpsEnabled = true }, paths, [site]).ToJsonString();
        Assert.Contains("tls_connection_policies", json);
        Assert.Contains("\"default_sni\":\"10.0.0.5\"", json);
        Assert.Contains("\"module\":\"internal\"", json);
        Assert.Contains("install_trust", json);
    }

    [Fact]
    public void Https_binding_needs_https_setting_and_one_scheme_per_listener()
    {
        var a = new SiteDefinition { Id = "a", Name = "A", Root = "C:\\a", Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100, Tls = true }] };
        var b = new SiteDefinition { Id = "b", Name = "B", Root = "C:\\b", Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100, Host = "b.local" }] };
        Assert.Contains(SiteValidator.Validate(a, [], new WebitSettings()), i => i.Message.Code == MessageCodes.HttpsDisabled);
        Assert.Contains(SiteValidator.Validate(b, [a], new WebitSettings { HttpsEnabled = true }), i => i.Message.Code == MessageCodes.TlsMismatch);
    }

    [Fact]
    public void Php_settings_reject_invalid_sizes()
    {
        var php = new PhpSettings { MemoryLimit = "lots", UploadMaxFilesize = "256m", MaxExecutionTime = 99999, Extensions = ["Intl", "bad;ext", "intl"] };
        php.Normalize();
        Assert.Equal("512M", php.MemoryLimit);
        Assert.Equal("256M", php.UploadMaxFilesize);
        Assert.Equal(3600, php.MaxExecutionTime);
        Assert.Equal(new[] { "intl" }, php.Extensions);
    }

    [Fact]
    public void Interval_reload_enables_live_view_for_apps()
    {
        var site = new SiteDefinition { Type = SiteType.Node, ReloadInterval = 1 };
        site.Normalize();
        Assert.Equal(2, site.ReloadInterval);
        Assert.True(site.HasLiveView);
    }
}
