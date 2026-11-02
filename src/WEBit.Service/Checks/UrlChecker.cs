using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WEBit.Contracts;
using WEBit.Core.Models;

namespace WEBit.Service.Checks;

/// <summary>Checks URLs from the server: DNS and HTTP (no proxy, no redirects).</summary>
public sealed class UrlChecker : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),

        // Reachability check only: HTTPS sites use the local WEBit CA, which the service account does not trust.
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    public async Task<List<UrlCheckResult>> CheckAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        var tasks = urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .Select(u => CheckOneAsync(u, ct));
        return [.. await Task.WhenAll(tasks)];
    }

    private async Task<UrlCheckResult> CheckOneAsync(string url, CancellationToken ct)
    {
        var result = new UrlCheckResult { Url = url };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            result.DnsLevel = IssueLevel.Error;
            result.HttpLevel = IssueLevel.Error;
            result.Error = "Invalid URL";
            return result;
        }

        // DNS
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
        {
            result.DnsLevel = IssueLevel.Ok;
            result.DnsText = "IP";
        }
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cts.Token);
                var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                result.DnsLevel = addresses.Length > 0 ? IssueLevel.Ok : IssueLevel.Error;
                result.DnsText = v4.Count > 0 ? string.Join(", ", v4) : string.Join(", ", addresses.Select(a => a.ToString()));
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                result.DnsLevel = IssueLevel.Error;
                result.DnsText = ex is SocketException se ? se.SocketErrorCode.ToString() : "Timeout";
            }
        }

        // HTTP
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            stopwatch.Stop();
            var status = (int)response.StatusCode;
            result.HttpStatus = status;
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.HttpLevel = status switch
            {
                < 300 => IssueLevel.Ok,
                < 400 => IssueLevel.Info,
                403 or 401 => IssueLevel.Warning,
                _ => IssueLevel.Error
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            result.HttpLevel = IssueLevel.Error;
            result.Error = ex is TaskCanceledException ? "Timeout" : ex.InnerException?.Message ?? ex.Message;
        }

        return result;
    }

    public void Dispose() => _http.Dispose();
}
