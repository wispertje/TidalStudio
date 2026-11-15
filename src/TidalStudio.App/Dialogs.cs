using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TidalStudio.App;

public enum DialogResult { Yes, No, Cancel }

/// <summary>Small message boxes built in code.</summary>
public static class Dialogs
{
    public static async Task<DialogResult> AskSaveChanges(Window owner, string fileName)
    {
        DialogResult result = DialogResult.Cancel;
        var dialog = MakeWindow("Unsaved changes");
        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80 };
        var dontSave = new Button { Content = "Don't save", MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        save.Click += (_, _) => { result = DialogResult.Yes; dialog.Close(); };
        dontSave.Click += (_, _) => { result = DialogResult.No; dialog.Close(); };
        cancel.Click += (_, _) => { result = DialogResult.Cancel; dialog.Close(); };
        dialog.Content = Layout($"Save changes to {fileName}?", save, dontSave, cancel);
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task ShowMessage(Window owner, string title, string message, double width = 460)
    {
        var dialog = MakeWindow(title);
        dialog.Width = width;
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(message, ok);
        await dialog.ShowDialog(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string message, string okText = "OK")
    {
        bool result = false;
        var dialog = MakeWindow(title);
        var ok = new Button { Content = okText, IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(message, ok, cancel);
        await dialog.ShowDialog(owner);
        return result;
    }

    private static Window MakeWindow(string title) => new()
    {
        Title = title,
        Width = 420,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Background = new SolidColorBrush(Color.Parse("#1A1D24")),
    };

    private static Control Layout(string message, params Button[] buttons)
    {
        var text = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            FontSize = 13,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) row.Children.Add(b);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new ScrollViewer { Content = text, MaxHeight = 520 });
        panel.Children.Add(row);
        return panel;
    }
}
