using WEBit.Core.Models;
using WEBit.Core.Storage;
using WEBit.Service.Modules;
using WEBit.Service.Sites;

namespace WEBit.Service.Tests;

/// <summary>Temporary folder, removed after the test.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "webit-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name, string content)
    {
        var file = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        System.IO.File.WriteAllText(file, content);
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class WordPressConfigTests
{
    private const string Config = """
        <?php
        define( 'DB_NAME', 'wp' );
        /* That's all, stop editing! Happy publishing. */
        require_once ABSPATH . 'wp-settings.php';
        """;

    [Fact]
    public void Adds_dynamic_home_before_the_stop_editing_line_once()
    {
        using var dir = new TempDir();
        var file = dir.File("wp-config.php", Config);

        SiteManager.AddWordPressSettings(file);
        SiteManager.AddWordPressSettings(file);

        var text = File.ReadAllText(file);
        Assert.Equal(1, CountOf(text, "/* WEBit */"));
        Assert.True(text.IndexOf("WP_HOME", StringComparison.Ordinal) < text.IndexOf("That's all", StringComparison.Ordinal));
        Assert.Contains("defined( 'FS_METHOD' ) || define( 'FS_METHOD', 'direct' );", text);
    }

    [Fact]
    public void Falls_back_to_the_settings_include()
    {
        using var dir = new TempDir();
        var file = dir.File("wp-config.php", "<?php\nrequire_once ABSPATH . 'wp-settings.php';\n");
        SiteManager.AddWordPressSettings(file);
        var text = File.ReadAllText(file);
        Assert.True(text.IndexOf("/* WEBit */", StringComparison.Ordinal) < text.IndexOf("require_once", StringComparison.Ordinal));
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

public class ProjectDirTests
{
    [Fact]
    public void Laravel_public_root_belongs_to_the_project_above()
    {
        using var dir = new TempDir();
        dir.File("app/composer.json", "{}");
        Directory.CreateDirectory(Path.Combine(dir.Path, "app", "public"));
        var site = new SiteDefinition { Type = SiteType.Php, Root = Path.Combine(dir.Path, "app", "public") };
        Assert.Equal(Path.Combine(dir.Path, "app"), SiteManager.ProjectDir(site));
    }

    [Fact]
    public void Other_roots_never_reach_the_parent_folder()
    {
        using var dir = new TempDir();
        dir.File("composer.json", "{}");
        Directory.CreateDirectory(Path.Combine(dir.Path, "docs"));
        var site = new SiteDefinition { Type = SiteType.Php, Root = Path.Combine(dir.Path, "docs") };
        Assert.Null(SiteManager.ProjectDir(site));
    }

    [Fact]
    public void Root_with_composer_json_is_the_project()
    {
        using var dir = new TempDir();
        dir.File("site/composer.json", "{}");
        var site = new SiteDefinition { Type = SiteType.Php, Root = Path.Combine(dir.Path, "site") + Path.DirectorySeparatorChar };
        Assert.Equal(Path.Combine(dir.Path, "site"), SiteManager.ProjectDir(site));
    }
}

public class ExportArchiveTests
{
    private sealed record Manifest(string Format, int Version);

    [Theory]
    [InlineData(null)]
    [InlineData("correct horse battery")]
    public void Round_trip_with_and_without_password(string? password)
    {
        using var dir = new TempDir();
        var source = dir.File("in/index.php", "<?php echo 'hi';");
        var zip = Path.Combine(dir.Path, "export.zip");
        using (var writer = new ExportArchiveWriter(zip, password))
        {
            writer.AddFile(source, "sites/a/files/index.php");
            writer.AddJson("webit-export.json", new Manifest("webit-site-export", 1));
        }

        using var reader = new ExportArchiveReader(zip, password);
        Assert.Equal(password is not null, reader.IsEncrypted);
        Assert.True(reader.Exists("sites/a/files/index.php"));
        Assert.Equal("webit-site-export", reader.ReadJson<Manifest>("webit-export.json")?.Format);

        var target = Path.Combine(dir.Path, "out.php");
        reader.ExtractToFile("sites/a/files/index.php", target);
        Assert.Equal("<?php echo 'hi';", File.ReadAllText(target));
    }

    [Fact]
    public void Encrypted_export_needs_the_right_password()
    {
        using var dir = new TempDir();
        var zip = Path.Combine(dir.Path, "export.zip");
        using (var writer = new ExportArchiveWriter(zip, "secret-password"))
        {
            writer.AddJson("webit-export.json", new Manifest("webit-site-export", 1));
        }

        Assert.Throws<ExportPasswordException>(() => new ExportArchiveReader(zip, null));
        using var wrong = new ExportArchiveReader(zip, "wrong-password");
        Assert.Throws<ExportPasswordException>(() => wrong.ReadJson<Manifest>("webit-export.json"));
    }
}

public class BackupNameTests
{
    [Theory]
    [InlineData("shop-20261028-101500.sql", "shop")]
    [InlineData("wp_blog_2-20261028-101500.sql", "wp_blog_2")]
    [InlineData("all-databases-20261028-101500.sql", null)]
    [InlineData("mariadb-20250101-000000.sql", null)]
    public void Parses_database_and_time(string name, string? database)
    {
        var parsed = SiteManager.ParseBackupName(name);
        Assert.NotNull(parsed);
        Assert.Equal(database, parsed.Value.Database);
    }

    [Theory]
    [InlineData("..\\shop-20261028-101500.sql")]
    [InlineData("shop.sql")]
    [InlineData("shop-2026-10-28.sql")]
    [InlineData("1shop-20261028-101500.sql")]
    public void Rejects_other_names(string name) => Assert.Null(SiteManager.ParseBackupName(name));
}

public class PhpIniTests
{
    [Fact]
    public void Xdebug_block_only_for_the_xdebug_process()
    {
        using var dir = new TempDir();
        var paths = WebitPaths.ForDataRoot(dir.Path, Path.Combine(dir.Path, "data"));
        var exe = dir.File("php/php-cgi.exe", "");
        var settings = new WebitSettings();
        settings.Php.XdebugPort = 9100;

        var plain = File.ReadAllText(PhpIni.Write(paths, settings, exe, null, "php-8.2.ini"));
        var debug = File.ReadAllText(PhpIni.Write(paths, settings, exe, @"C:\x\php_xdebug-8.2.dll", "php-8.2-xdebug.ini"));

        Assert.DoesNotContain("xdebug", plain);
        Assert.Contains("zend_extension = \"C:\\x\\php_xdebug-8.2.dll\"", debug);
        Assert.Contains("xdebug.client_port = 9100", debug);
        Assert.Contains("xdebug.client_host = 127.0.0.1", debug);
    }

    [Fact]
    public void Process_keys_name_the_ini_files()
    {
        Assert.Equal("8.2", new PhpProcessKey("8.2", false).ToString());
        Assert.Equal("8.4-xdebug", new PhpProcessKey("8.4", true).ToString());
    }
}
