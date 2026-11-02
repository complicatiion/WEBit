using Avalonia.Controls;
using Avalonia.Interactivity;
using WEBit.App.Localization;
using WEBit.App.ViewModels;
using WEBit.Contracts;

namespace WEBit.App.Views;

/// <summary>New site from a template: name and (WordPress) the admin account.</summary>
public partial class TemplateWindow : Window
{
    private readonly string _template = SiteTemplates.WordPress;

    public TemplateWindow()
    {
        InitializeComponent();
        WEBit.App.Services.WindowChrome.Apply(this, maximize: false);
    }

    public TemplateWindow(string template, string defaultName) : this()
    {
        _template = template;
        var wordpress = template == SiteTemplates.WordPress;
        Title = wordpress ? Loc.T("TplWordPressTitle") : Loc.T("TplLaravelTitle");
        HeaderText.Text = Title;
        InfoText.Text = wordpress ? Loc.T("TplWordPressInfo") : Loc.T("TplLaravelInfo");
        Glyph.Data = Res.Icon(wordpress ? "IconWordpress" : "IconPackage");
        AdminPanel.IsVisible = wordpress;
        NameBox.Text = defaultName;
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public TemplateRequest? Request { get; private set; }

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        Request = new TemplateRequest
        {
            Template = _template,
            Name = name,
            AdminUser = UserBox.Text?.Trim(),
            AdminEmail = EmailBox.Text?.Trim(),
            Locale = Loc.Language == "de" ? "de_DE" : "en_US"
        };
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
