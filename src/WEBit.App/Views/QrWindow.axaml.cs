using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using QRCoder;
using WEBit.App.Localization;

namespace WEBit.App.Views;

/// <summary>QR code of a site address, to open it on a phone in the same network.</summary>
public partial class QrWindow : Window
{
    public QrWindow()
    {
        InitializeComponent();
        WEBit.App.Services.WindowChrome.Apply(this, maximize: false);
    }

    /// <param name="urls">Addresses of the site; the first one that works from other devices is preselected.</param>
    public QrWindow(string siteName, IReadOnlyList<string> urls) : this()
    {
        Title = Loc.T("QrTitle");
        HeaderText.Text = siteName;
        HintText.Text = Loc.T("QrHint");
        UrlBox.ItemsSource = urls;
        UrlBox.SelectedItem = urls.FirstOrDefault(IsReachableFromNetwork) ?? urls.FirstOrDefault();
    }

    /// <summary>IP address or host name other than loopback/localhost.</summary>
    private static bool IsReachableFromNetwork(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsLoopback
        && uri.HostNameType is UriHostNameType.IPv4;

    private void OnUrlChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (UrlBox.SelectedItem is string url)
        {
            Code.Data = Render(url);
        }
    }

    /// <summary>One square per dark module (QRCoder matrix incl. quiet zone) as path geometry.</summary>
    private static Geometry Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var path = new StringBuilder();
        for (var y = 0; y < matrix.Count; y++)
        {
            for (var x = 0; x < matrix[y].Length; x++)
            {
                if (matrix[y][x])
                {
                    path.Append(CultureInfo.InvariantCulture, $"M{x},{y}h1v1h-1z");
                }
            }
        }

        // The white border of the window provides the quiet zone.
        return Geometry.Parse(path.ToString());
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (UrlBox.SelectedItem is string url && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(url);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
