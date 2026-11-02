using WEBit.Core.Engine;
using WEBit.Core.Models;
using WEBit.Core.Storage;

namespace WEBit.Core.Tests;

public class HttpsConfigTests
{
    private static readonly WebitPaths Paths = WebitPaths.ForDataRoot(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "webit-test"));

    [Fact]
    public void Wildcard_https_binding_has_no_default_sni()
    {
        var site = new SiteDefinition
        {
            Id = "all", Name = "All", Type = SiteType.Static, Root = "C:\\www\\all",
            Bindings = [new SiteBinding { Address = "0.0.0.0", Port = 47100, Tls = true }]
        };
        var json = CaddyConfigBuilder.Build(new WebitSettings { HttpsEnabled = true }, Paths, [site],
            new CaddyBuildOptions { WildcardNames = ["pc01", "10.0.0.5"] }).ToJsonString();
        Assert.Contains("tls_connection_policies", json);
        Assert.DoesNotContain("default_sni", json);
        Assert.Contains("pc01", json);
    }

    [Fact]
    public void Http_only_sites_get_no_tls_app()
    {
        var site = new SiteDefinition
        {
            Id = "plain", Name = "Plain", Type = SiteType.Static, Root = "C:\\www\\plain",
            Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100 }]
        };
        var json = CaddyConfigBuilder.Build(new WebitSettings(), Paths, [site]).ToJsonString();
        Assert.DoesNotContain("tls_connection_policies", json);
    }
}

public class PhpVersionTests
{
    private static readonly WebitPaths Paths = WebitPaths.ForDataRoot(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "webit-test"));

    [Theory]
    [InlineData("php-8.2", "8.2")]
    [InlineData("php-10.1", "10.1")]
    [InlineData("php", null)]
    [InlineData("php-8", null)]
    [InlineData("php-8.2.1", null)]
    [InlineData("php-x.y", null)]
    public void PhpBranchOf_accepts_major_minor_only(string id, string? branch) =>
        Assert.Equal(branch, ModuleIds.PhpBranchOf(id));

    [Theory]
    [InlineData("8.4.14", "8.4")]
    [InlineData("8.3.0-dev", "8.3")]
    [InlineData("8", null)]
    [InlineData(null, null)]
    public void BranchOfVersion_takes_major_minor(string? version, string? branch) =>
        Assert.Equal(branch, ModuleIds.BranchOfVersion(version));

    [Fact]
    public void Site_normalize_drops_invalid_php_version()
    {
        var site = new SiteDefinition { PhpVersion = " 8.2 " };
        site.Normalize();
        Assert.Equal("8.2", site.PhpVersion);

        site.PhpVersion = "latest";
        site.Normalize();
        Assert.Null(site.PhpVersion);
    }

    [Fact]
    public void Php_site_uses_its_own_upstream()
    {
        var site = new SiteDefinition
        {
            Id = "old", Name = "Old", Type = SiteType.Php, Root = "C:\\www\\old", PhpVersion = "8.2",
            Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100 }]
        };
        var json = CaddyConfigBuilder.Build(new WebitSettings(), Paths, [site], new CaddyBuildOptions
        {
            PhpFastCgi = "127.0.0.1:47203",
            PhpUpstream = s => s.PhpVersion == "8.2" ? "127.0.0.1:47206" : "127.0.0.1:47203"
        }).ToJsonString();
        Assert.Contains("127.0.0.1:47206", json);
        Assert.DoesNotContain("127.0.0.1:47203", json);
    }

    [Fact]
    public void Missing_php_version_never_serves_source()
    {
        var site = new SiteDefinition
        {
            Id = "old", Name = "Old", Type = SiteType.Php, Root = "C:\\www\\old", PhpVersion = "7.4",
            Bindings = [new SiteBinding { Address = "10.0.0.5", Port = 47100 }]
        };
        var json = CaddyConfigBuilder.Build(new WebitSettings(), Paths, [site], new CaddyBuildOptions
        {
            PhpFastCgi = "127.0.0.1:47203",
            PhpUpstream = _ => null
        }).ToJsonString();
        Assert.Contains("PHP 7.4 is not installed", json);
        Assert.Contains("503", json);
    }

    [Fact]
    public void Pool_ports_sit_between_modules_and_apps()
    {
        var settings = new WebitSettings();
        Assert.Equal(new[] { 47206, 47207, 47208, 47209 }, settings.PhpPoolPorts);
        Assert.DoesNotContain(settings.PhpFastCgiPort, settings.PhpPoolPorts);
        Assert.DoesNotContain(settings.AdminerPort, settings.PhpPoolPorts);
    }

    [Fact]
    public void Xdebug_port_is_normalized()
    {
        var php = new PhpSettings { XdebugPort = 80 };
        php.Normalize();
        Assert.Equal(9003, php.XdebugPort);
    }
}

public class SettingsV009Tests
{
    [Fact]
    public void Backups_and_adminer_network_are_off_by_default()
    {
        var settings = new WebitSettings();
        settings.Normalize();
        Assert.False(settings.DbBackup.Enabled);
        Assert.False(settings.AdminerNetworkAccess);
        Assert.False(settings.MariaDbNetworkAccess);
    }

    [Fact]
    public void Backup_settings_are_clamped()
    {
        var backup = new DbBackupSettings { IntervalHours = 0, Keep = 1000 };
        backup.Normalize();
        Assert.Equal(1, backup.IntervalHours);
        Assert.Equal(365, backup.Keep);
    }

    [Fact]
    public void Missing_backup_settings_in_old_files_get_defaults()
    {
        var settings = new WebitSettings { DbBackup = null! };
        settings.Normalize();
        Assert.NotNull(settings.DbBackup);
        Assert.Equal(24, settings.DbBackup.IntervalHours);
    }
}
