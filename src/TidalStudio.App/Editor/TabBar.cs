using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Document;

namespace TidalStudio.App.Editor;

/// <summary>One open file (or untitled buffer). The editor control is shared; each tab has its own document.</summary>
public sealed class DocumentTab
{
    public DocumentTab(TextDocument document, string? path, string untitledName)
    {
        Document = document;
        Path = path;
        UntitledName = untitledName;
    }

    public TextDocument Document { get; }
    public string? Path { get; set; }
    public string UntitledName { get; }
    public bool Dirty { get; set; }

    // View state restored when the tab is activated again.
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }

    public string DisplayName => Path is null ? UntitledName : System.IO.Path.GetFileName(Path);
}

/// <summary>The strip of tabs above the editor.</summary>
public sealed class TabBar : Border
{
    private static readonly IBrush ActiveBackground = new SolidColorBrush(Color.Parse("#171A21"));
    private static readonly IBrush HoverBackground = new SolidColorBrush(Color.Parse("#1F232C"));
    private static readonly IBrush ActiveText = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush InactiveText = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4ECDC4"));
    private static readonly IBrush DirtyBrush = new SolidColorBrush(Color.Parse("#FFD166"));

    private readonly StackPanel _strip;
    private readonly ScrollViewer _scroll;

    public TabBar()
    {
        Background = new SolidColorBrush(Color.Parse("#1A1D24"));
        BorderBrush = new SolidColorBrush(Color.Parse("#2A2F3A"));
        BorderThickness = new Thickness(0, 0, 0, 1);
        Height = 32;

        _strip = new StackPanel { Orientation = Orientation.Horizontal };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _strip,
        };
        // Plain mouse wheel scrolls the tabs sideways.
        _scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            _scroll.Offset = _scroll.Offset.WithX(Math.Max(0, _scroll.Offset.X - e.Delta.Y * 40));
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var add = new Button { Content = "+", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        add.Classes.Add("icon");
        ToolTip.SetTip(add, "New file (Ctrl+N)");
        add.Click += (_, _) => NewRequested?.Invoke();

        var dock = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        dock.Children.Add(add);
        dock.Children.Add(_scroll);
        Child = dock;
    }

    public event Action<DocumentTab>? Activated;
    public event Action<DocumentTab>? CloseRequested;
    public event Action<DocumentTab>? CloseOthersRequested;
    public event Action? CloseAllRequested;
    public event Action? NewRequested;

    /// <summary>Redraws the strip. Cheap: there are only ever a handful of tabs.</summary>
    public void Update(IReadOnlyList<DocumentTab> tabs, DocumentTab? active)
    {
        _strip.Children.Clear();
        Control? activeView = null;
        foreach (var tab in tabs)
        {
            var view = BuildTab(tab, ReferenceEquals(tab, active));
            if (ReferenceEquals(tab, active)) activeView = view;
            _strip.Children.Add(view);
        }
        activeView?.BringIntoView();
    }

    private Control BuildTab(DocumentTab tab, bool isActive)
    {
        var name = new TextBlock
        {
            Text = tab.DisplayName,
            Foreground = isActive ? ActiveText : InactiveText,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            FontStyle = tab.Path is null ? FontStyle.Italic : FontStyle.Normal,
        };
        var dirty = new TextBlock
        {
            Text = "●",
            Foreground = DirtyBrush,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = tab.Dirty,
            Margin = new Thickness(6, 0, 0, 0),
        };
        var close = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(4, 1),
            MinHeight = 0,
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Opacity = isActive ? 0.9 : 0.45,
        };
        ToolTip.SetTip(close, "Close (Ctrl+W)");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke(tab);
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(name);
        content.Children.Add(dirty);
        content.Children.Add(close);

        var view = new Border
        {
            Child = content,
            Padding = new Thickness(12, 0, 6, 0),
            Background = isActive ? ActiveBackground : Brushes.Transparent,
            BorderBrush = isActive ? Accent : new SolidColorBrush(Color.Parse("#2A2F3A")),
            BorderThickness = isActive ? new Thickness(0, 2, 1, 0) : new Thickness(0, 0, 1, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(view, tab.Path ?? "Not saved yet");
        if (!isActive)
        {
            view.PointerEntered += (_, _) => view.Background = HoverBackground;
            view.PointerExited += (_, _) => view.Background = Brushes.Transparent;
        }
        view.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(view).Properties;
            if (props.IsLeftButtonPressed) Activated?.Invoke(tab);
            else if (props.IsMiddleButtonPressed)
            {
                e.Handled = true;
                CloseRequested?.Invoke(tab);
            }
        };
        view.ContextMenu = new ContextMenu { ItemsSource = Menu(tab) };
        return view;
    }

    private List<Control> Menu(DocumentTab tab)
    {
        var close = new MenuItem { Header = "Close", InputGesture = new KeyGesture(Key.W, KeyModifiers.Control) };
        close.Click += (_, _) => CloseRequested?.Invoke(tab);
        var others = new MenuItem { Header = "Close others" };
        others.Click += (_, _) => CloseOthersRequested?.Invoke(tab);
        var all = new MenuItem { Header = "Close all" };
        all.Click += (_, _) => CloseAllRequested?.Invoke();
        var items = new List<Control> { close, others, all };
        if (tab.Path is { } path)
        {
            var copy = new MenuItem { Header = "Copy path" };
            copy.Click += async (_, _) =>
            {
                try
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(path);
                }
                catch (Exception) { }
            };
            var reveal = new MenuItem { Header = "Show in file manager" };
            reveal.Click += (_, _) =>
            {
                try
                {
                    if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
                    else Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(path)!) { UseShellExecute = true });
                }
                catch (Exception) { }
            };
            items.Add(new Separator());
            items.Add(copy);
            items.Add(reveal);
        }
        return items;
    }
}
