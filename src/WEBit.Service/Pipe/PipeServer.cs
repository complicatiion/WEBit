using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WEBit.Contracts;
using WEBit.Core.Storage;
using WEBit.Platform.Windows;

namespace WEBit.Service.Pipe;

/// <summary>
/// Control pipe server. ACL: SYSTEM, Administrators, the service account, "WEBit Admins" and users added at install.
/// </summary>
public sealed class PipeServer(
    RequestDispatcher dispatcher,
    StatusBroadcaster broadcaster,
    SettingsRepositoryAccessor settings,
    ILogger<PipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Control pipe \\\\.\\pipe\\{Pipe} ready.", Protocol.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not create the named pipe.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Connection failed.");
                await server.DisposeAsync();
                continue;
            }

            var connection = new PipeConnection(server, dispatcher, broadcaster, log);
            _ = Task.Run(() => connection.RunAsync(stoppingToken), CancellationToken.None);
        }
    }

    private NamedPipeServerStream CreateServer()
    {
        if (OperatingSystem.IsWindows())
        {
            return NamedPipeServerStreamAcl.Create(
                Protocol.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                0,
                CreateSecurity());
        }

        return new NamedPipeServerStream(
            Protocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        const PipeAccessRights clientRights = PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize;

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } self)
            {
                // Service account, or the signed-in user in desktop mode.
                security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
        }

        try
        {
            var group = (SecurityIdentifier)new NTAccount(WindowsServiceControl.AdminGroupName).Translate(typeof(SecurityIdentifier));
            security.AddAccessRule(new PipeAccessRule(group, clientRights, AccessControlType.Allow));
        }
        catch (IdentityNotMappedException)
        {
            // Group does not exist.
        }
        catch (SystemException ex)
        {
            log.LogDebug(ex, "Could not resolve group {Group}.", WindowsServiceControl.AdminGroupName);
        }

        foreach (var sid in settings.Current.AllowedUserSids)
        {
            try
            {
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), clientRights, AccessControlType.Allow));
            }
            catch (ArgumentException)
            {
                log.LogWarning("Invalid SID in settings: {Sid}", sid);
            }
        }

        return security;
    }
}

/// <summary>Current settings for components outside SiteManager.</summary>
public sealed class SettingsRepositoryAccessor(WebitPaths paths)
{
    private readonly SettingsRepository _repository = new(paths);

    public WEBit.Core.Models.WebitSettings Current => _repository.Load();
}
