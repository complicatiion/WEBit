using System.Text.Json;
using WEBit.Contracts;
using WEBit.Core.Models;
using WEBit.Service.Checks;
using WEBit.Service.Sites;

namespace WEBit.Service.Pipe;

/// <summary>Maps pipe requests to service functions.</summary>
public sealed class RequestDispatcher(SiteManager manager, UrlChecker urlChecker)
{
    public async Task<JsonElement?> DispatchAsync(string method, JsonElement? payload, CancellationToken ct)
    {
        switch (method)
        {
            case Methods.Hello:
                return Out(manager.GetHello());

            case Methods.GetStatus:
                return Out(manager.GetStatus());

            case Methods.SaveSite:
                return Out(await manager.SaveSiteAsync(In<SiteDefinition>(payload), ct));

            case Methods.DeleteSite:
                await manager.DeleteSiteAsync(In<IdRequest>(payload).Id, ct);
                return null;

            case Methods.StartSite:
                await manager.SetEnabledAsync(In<IdRequest>(payload).Id, true, ct);
                return null;

            case Methods.StopSite:
                await manager.SetEnabledAsync(In<IdRequest>(payload).Id, false, ct);
                return null;

            case Methods.StartAll:
                await manager.SetEnabledAsync(null, true, ct);
                return null;

            case Methods.StopAll:
                await manager.SetEnabledAsync(null, false, ct);
                return null;

            case Methods.CheckSite:
                return Out(manager.CheckSite(In<SiteDefinition>(payload)));

            case Methods.CheckEndpoint:
                return Out(manager.CheckEndpoint(In<EndpointCheckRequest>(payload)));

            case Methods.SuggestPort:
                return Out(manager.SuggestPort(In<SuggestPortRequest>(payload)));

            case Methods.GetNetworkInfo:
                return Out(manager.GetNetworkInfo());

            case Methods.CheckUrls:
                return Out(await urlChecker.CheckAsync(In<UrlCheckRequest>(payload).Urls, ct));

            case Methods.GetLog:
                return Out(manager.GetLog(In<LogRequest>(payload)));

            case Methods.GetSettings:
                return Out(manager.GetSettings());

            case Methods.SaveSettings:
                return Out(await manager.SaveSettingsAsync(In<WebitSettings>(payload), ct));

            case Methods.AnalyzeFolder:
                return Out(manager.AnalyzeFolder(In<PathRequest>(payload)));

            case Methods.RestartEngine:
                await manager.RestartEngineAsync(ct);
                return null;

            case Methods.RemoveModule:
                await manager.RemoveModuleAsync(In<IdRequest>(payload), ct);
                return null;

            case Methods.ClearLogs:
                return Out(manager.ClearLogs());

            case Methods.CheckUpdates:
                manager.CheckUpdates();
                return null;

            case Methods.DownloadModule:
                manager.DownloadModule(In<IdRequest>(payload));
                return null;

            case Methods.SetModuleEnabled:
                await manager.SetModuleEnabledAsync(In<ModuleToggleRequest>(payload), ct);
                return null;

            case Methods.RestartModule:
                await manager.RestartModuleAsync(In<IdRequest>(payload), ct);
                return null;

            case Methods.RestartApp:
                manager.RestartApp(In<IdRequest>(payload));
                return null;

            case Methods.InstallApp:
                return Out(await manager.InstallAppAsync(In<IdRequest>(payload), ct));

            case Methods.CreateFromTemplate:
                return Out(await manager.CreateFromTemplateAsync(In<TemplateRequest>(payload), ct));

            case Methods.GetDatabases:
                return Out(manager.GetDatabases());

            case Methods.DropDatabase:
                await manager.DropDatabaseAsync(In<IdRequest>(payload), ct);
                return null;

            case Methods.GetRootCertificate:
                return Out(manager.GetRootCertificate());

            case Methods.ExportSites:
                return Out(await manager.ExportSitesAsync(In<ExportRequest>(payload), ct));

            case Methods.ImportSites:
                return Out(await manager.ImportSitesAsync(In<ImportRequest>(payload), ct));

            case Methods.GetPhpInfo:
                return Out(manager.GetPhpInfo());

            case Methods.GetDbCredentials:
                return Out(manager.GetDbCredentials());

            case Methods.CreateDatabase:
                return Out(await manager.CreateDatabaseAsync(In<CreateDatabaseRequest>(payload), ct));

            case Methods.BackupDatabase:
                return Out(await manager.BackupDatabaseAsync(Protocol.FromPayload<BackupRequest>(payload), ct));

            case Methods.GetBackups:
                return Out(manager.GetBackups());

            case Methods.RestoreBackup:
                await manager.RestoreBackupAsync(In<BackupFileRequest>(payload), ct);
                return null;

            case Methods.DeleteBackup:
                manager.DeleteBackup(In<BackupFileRequest>(payload));
                return null;

            case Methods.Shutdown:
                manager.Shutdown();
                return null;

            default:
                throw new NotSupportedException($"Unknown request: {method}");
        }
    }

    private static JsonElement Out<T>(T value) => Protocol.ToPayload(value);

    private static T In<T>(JsonElement? payload) where T : class =>
        Protocol.FromPayload<T>(payload) ?? throw new ArgumentException("Request without payload.");
}
