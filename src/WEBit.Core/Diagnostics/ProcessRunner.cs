using System.Diagnostics;
using System.Text;

namespace WEBit.Core.Diagnostics;

/// <summary>Runs helper tools (netsh, sc, icacls …) and returns exit code and output.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output, bool TimedOut)
    {
        public bool Success => ExitCode == 0 && !TimedOut;
    }

    public static Result Run(string fileName, IEnumerable<string> arguments, TimeSpan? timeout = null, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var pair in environment ?? new Dictionary<string, string>())
        {
            psi.Environment[pair.Key] = pair.Value;
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
            {
                return new Result(-1, "", false);
            }
        }
        catch (Exception ex)
        {
            return new Result(-1, ex.Message, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (!process.WaitForExit(limit))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore.
            }

            return new Result(-1, output.ToString(), true);
        }

        // Flush async output.
        process.WaitForExit();
        lock (output)
        {
            return new Result(process.ExitCode, output.ToString(), false);
        }
    }

    public static Result Run(string fileName, params string[] arguments) => Run(fileName, arguments, null);
}
