using System.Text.Json;
using WEBit.Core.Serialization;

namespace WEBit.Contracts;

/// <summary>Service/UI protocol: named pipe, UTF-8, one JSON <see cref="PipeEnvelope"/> per line.</summary>
public static class Protocol
{
    public const string PipeName = "WEBit.Control";

    /// <summary>Bumped on incompatible message changes.</summary>
    public const int Version = 9;

    public const string KindRequest = "req";
    public const string KindResponse = "res";
    public const string KindEvent = "evt";

    public static string Serialize(PipeEnvelope envelope) => JsonSerializer.Serialize(envelope, WebitJson.Wire);

    public static PipeEnvelope? Deserialize(string line) => JsonSerializer.Deserialize<PipeEnvelope>(line, WebitJson.Wire);

    public static JsonElement ToPayload<T>(T value) => JsonSerializer.SerializeToElement(value, WebitJson.Wire);

    public static T? FromPayload<T>(JsonElement? payload) =>
        payload is { } element && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? element.Deserialize<T>(WebitJson.Wire)
            : default;
}

/// <summary>Envelope for requests, responses and events.</summary>
public sealed class PipeEnvelope
{
    public string Kind { get; set; } = Protocol.KindRequest;

    public long Id { get; set; }

    public string? Method { get; set; }

    public JsonElement? Payload { get; set; }

    public string? Error { get; set; }
}

/// <summary>Request names.</summary>
public static class Methods
{
    public const string Hello = "hello";
    public const string GetStatus = "getStatus";
    public const string SaveSite = "saveSite";
    public const string DeleteSite = "deleteSite";
    public const string StartSite = "startSite";
    public const string StopSite = "stopSite";
    public const string StartAll = "startAll";
    public const string StopAll = "stopAll";
    public const string CheckSite = "checkSite";
    public const string CheckEndpoint = "checkEndpoint";
    public const string SuggestPort = "suggestPort";
    public const string GetNetworkInfo = "getNetworkInfo";
    public const string CheckUrls = "checkUrls";
    public const string GetLog = "getLog";
    public const string GetSettings = "getSettings";
    public const string SaveSettings = "saveSettings";
    public const string AnalyzeFolder = "analyzeFolder";
    public const string RestartEngine = "restartEngine";
    public const string Shutdown = "shutdown";
    public const string SetModuleEnabled = "setModuleEnabled";
    public const string RestartModule = "restartModule";
    public const string DownloadModule = "downloadModule";
    public const string RemoveModule = "removeModule";
    public const string CheckUpdates = "checkUpdates";
    public const string ClearLogs = "clearLogs";
    public const string RestartApp = "restartApp";
    public const string InstallApp = "installApp";
    public const string GetDbCredentials = "getDbCredentials";
    public const string CreateDatabase = "createDatabase";
    public const string BackupDatabase = "backupDatabase";
    public const string GetPhpInfo = "getPhpInfo";
    public const string CreateFromTemplate = "createFromTemplate";
    public const string GetDatabases = "getDatabases";
    public const string DropDatabase = "dropDatabase";
    public const string GetRootCertificate = "getRootCertificate";
    public const string ExportSites = "exportSites";
    public const string ImportSites = "importSites";
    public const string GetBackups = "getBackups";
    public const string RestoreBackup = "restoreBackup";
    public const string DeleteBackup = "deleteBackup";
}

/// <summary>Event names (pushed to all connected clients).</summary>
public static class Events
{
    public const string StatusChanged = "statusChanged";
}
