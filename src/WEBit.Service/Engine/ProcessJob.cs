using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WEBit.Platform.Windows;

namespace WEBit.Service.Engine;

/// <summary>Shared job object: all child processes die with the service, even on crash.</summary>
public sealed class ProcessJob : IDisposable
{
    private readonly JobObject? _job;

    public ProcessJob(ILogger<ProcessJob> log)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _job = new JobObject();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Job object unavailable; child processes are not killed on crash.");
        }
    }

    public void Assign(Process process) => _job?.Assign(process);

    public void Dispose() => _job?.Dispose();
}
