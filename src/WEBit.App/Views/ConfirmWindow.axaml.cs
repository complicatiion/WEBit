using Avalonia.Controls;
using Avalonia.Interactivity;
using WEBit.App.ViewModels;

namespace WEBit.App.Views;

/// <summary>Confirmation dialog with optional checkbox.</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();

        // Short questions stay a compact dialog; only the title bar color is applied.
        WEBit.App.Services.WindowChrome.Apply(this, maximize: false);
    }

    public ConfirmWindow(string title, string message, string confirmText, bool danger, string? optionText = null, bool optionDefault = false,
        string? confirmIcon = null) : this()
    {
        Title = title;
        HeaderText.Text = title;
        MessageText.Text = message;
        ConfirmLabel.Text = confirmText;
        ConfirmLabel.Icon = Res.Icon(confirmIcon ?? (danger ? "IconDelete" : "IconCheck"));
        Glyph.Data = Res.Icon(danger ? "IconAlert" : "IconInfo");
        Glyph.Foreground = Res.Brush(danger ? "BrushDanger" : "BrushAccentHover");
        if (danger)
        {
            ConfirmButton.Classes.Remove("primary");
            ConfirmButton.Classes.Add("danger");
        }

        if (!string.IsNullOrEmpty(optionText))
        {
            OptionCheck.Content = optionText;
            OptionCheck.IsChecked = optionDefault;
            OptionCheck.IsVisible = true;
        }
    }

    /// <summary>Dialog with one text field (e.g. database name).</summary>
    public ConfirmWindow(string title, string message, string confirmText, string watermark) : this(title, message, confirmText, danger: false)
    {
        InputBox.Watermark = watermark;
        InputBox.IsVisible = true;
        Opened += (_, _) => InputBox.Focus();
    }

    /// <summary>Text field plus a second action left of the main one (e.g. "use existing folder").</summary>
    public ConfirmWindow(string title, string message, string confirmText, string watermark, string input, string secondaryText, string secondaryIcon)
        : this(title, message, confirmText, watermark)
    {
        InputBox.Text = input;
        SecondaryLabel.Text = secondaryText;
        SecondaryLabel.Icon = Res.Icon(secondaryIcon);
        SecondaryButton.IsVisible = true;
        ConfirmLabel.Icon = Res.Icon("IconPencil");
    }

    /// <summary>The second action was chosen (dialog result is true as well).</summary>
    public bool SecondaryChosen { get; private set; }

    public bool OptionChecked => OptionCheck.IsChecked == true;

    public string Input => InputBox.Text?.Trim() ?? "";

    /// <summary>Text as typed (passwords are not trimmed).</summary>
    public string RawInput => InputBox.Text ?? "";

    /// <summary>Masks the text field (passwords).</summary>
    public void MaskInput() => InputBox.PasswordChar = '•';

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSecondary(object? sender, RoutedEventArgs e)
    {
        SecondaryChosen = true;
        Close(true);
    }
}
