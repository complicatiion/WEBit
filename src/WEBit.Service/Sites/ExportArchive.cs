using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;
using WEBit.Core.Serialization;

namespace WEBit.Service.Sites;

/// <summary>Export ZIP writer; with a password every entry is AES-256 encrypted (7-Zip, WinRAR, WinZip open it).</summary>
internal sealed class ExportArchiveWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly ZipOutputStream _zip;
    private readonly bool _encrypted;

    public ExportArchiveWriter(string path, string? password)
    {
        _file = File.Create(path);
        _zip = new ZipOutputStream(_file) { IsStreamOwner = false };
        _zip.SetLevel(6);
        _encrypted = !string.IsNullOrEmpty(password);
        if (_encrypted)
        {
            _zip.Password = password;
        }
    }

    /// <summary>
    /// The file is read completely before the entry is opened: a locked or changing file (log, SQLite) fails here as
    /// IOException and is skipped by the caller, without leaving a half-written entry.
    /// </summary>
    public void AddFile(string path, string name)
    {
        string? snapshot = null;
        try
        {
            snapshot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "webit-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var copy = File.Create(snapshot))
            {
                source.CopyTo(copy);
            }

            using var data = File.OpenRead(snapshot);
            _zip.PutNextEntry(NewEntry(name, File.GetLastWriteTime(path), data.Length));
            data.CopyTo(_zip);
            _zip.CloseEntry();
        }
        finally
        {
            if (snapshot is not null && File.Exists(snapshot))
            {
                File.Delete(snapshot);
            }
        }
    }

    public void AddJson<T>(string name, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WebitJson.File);
        _zip.PutNextEntry(NewEntry(name, DateTime.Now, bytes.Length));
        _zip.Write(bytes, 0, bytes.Length);
        _zip.CloseEntry();
    }

    private ZipEntry NewEntry(string name, DateTime modified, long size)
    {
        var entry = new ZipEntry(ZipEntry.CleanName(name)) { DateTime = modified, Size = size, IsUnicodeText = true };
        if (_encrypted)
        {
            entry.AESKeySize = 256;
        }

        return entry;
    }

    public void Dispose()
    {
        try
        {
            _zip.Finish();
        }
        finally
        {
            _zip.Dispose();
            _file.Dispose();
        }
    }
}

/// <summary>Export ZIP reader for plain and AES/ZipCrypto encrypted archives.</summary>
internal sealed class ExportArchiveReader : IDisposable
{
    private readonly ZipFile _zip;

    public ExportArchiveReader(string path, string? password)
    {
        _zip = new ZipFile(path);
        IsEncrypted = _zip.Cast<ZipEntry>().Any(e => e.IsCrypted);
        if (IsEncrypted)
        {
            if (string.IsNullOrEmpty(password))
            {
                _zip.Close();
                throw new ExportPasswordException("The export is protected with a password.");
            }

            _zip.Password = password;
        }
    }

    public bool IsEncrypted { get; }

    public IEnumerable<string> FileNames => _zip.Cast<ZipEntry>().Where(e => e.IsFile).Select(e => e.Name);

    public bool Exists(string name) => _zip.GetEntry(name) is { IsFile: true };

    public T? ReadJson<T>(string name)
    {
        if (_zip.GetEntry(name) is not { IsFile: true } entry)
        {
            return default;
        }

        using var stream = Open(entry);
        return JsonSerializer.Deserialize<T>(stream, WebitJson.File);
    }

    public void ExtractToFile(string name, string path)
    {
        var entry = _zip.GetEntry(name) ?? throw new FileNotFoundException(name);
        using var source = Open(entry);
        using var target = File.Create(path);
        source.CopyTo(target);
    }

    private Stream Open(ZipEntry entry)
    {
        try
        {
            return _zip.GetInputStream(entry);
        }
        catch (ZipException ex) when (IsEncrypted)
        {
            throw new ExportPasswordException("Wrong password for the export (" + ex.Message + ").");
        }
    }

    public void Dispose() => _zip.Close();
}

/// <summary>Missing or wrong export password (the app asks again).</summary>
public sealed class ExportPasswordException(string message) : InvalidOperationException(message);
