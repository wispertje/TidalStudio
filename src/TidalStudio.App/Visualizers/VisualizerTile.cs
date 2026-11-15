using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TidalStudio.Core.Settings;

namespace TidalStudio.App.Visualizers;

/// <summary>A panel tile: title bar (behind-code toggle, options, move, close) + the visualizer + a resize grip.</summary>
public sealed class VisualizerTile : Border
{
    private readonly Border _host;
    private double _dragStartY;
    private double _dragStartHeight;
    private bool _dragging;

    public VisualizerTile(VisualizerBase visualizer, VisualizerConfig config)
    {
        Visualizer = visualizer;
        Config = config;
        Classes.Add("tile");

        var title = new TextBlock { Text = visualizer.Title.ToUpperInvariant() };
        title.Classes.Add("tileTitle");

        var behind = new ToggleButton
        {
            Content = "▣ behind code",
            IsChecked = config.BehindCode,
        };
        behind.Classes.Add("icon");
        ToolTip.SetTip(behind, "Also draw this visualizer behind the code");
        behind.IsCheckedChanged += (_, _) =>
        {
            config.BehindCode = behind.IsChecked == true;
            BehindCodeChanged?.Invoke(this);
        };

        var optionsButton = new Button { Content = "⚙" };
        optionsButton.Classes.Add("icon");
        ToolTip.SetTip(optionsButton, "Options");
        optionsButton.Flyout = new Flyout
        {
            Content = new ScrollViewer { MaxHeight = 520, Content = OptionsPanel.Build(visualizer.Options) },
            Placement = PlacementMode.LeftEdgeAlignedTop,
        };

        var up = new Button { Content = "▲" };
        up.Classes.Add("icon");
        ToolTip.SetTip(up, "Move up");
        up.Click += (_, _) => MoveRequested?.Invoke(this, -1);
        var down = new Button { Content = "▼" };
        down.Classes.Add("icon");
        ToolTip.SetTip(down, "Move down");
        down.Click += (_, _) => MoveRequested?.Invoke(this, 1);

        var close = new Button { Content = "✕" };
        close.Classes.Add("icon");
        ToolTip.SetTip(close, "Remove");
        close.Click += (_, _) => RemoveRequested?.Invoke(this);

        var header = new DockPanel { Margin = new Thickness(8, 4, 4, 2), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        buttons.Children.Add(behind);
        buttons.Children.Add(optionsButton);
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);

        _host = new Border
        {
            Child = visualizer,
            Height = Math.Clamp(config.Height, 60, 900),
            Margin = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
        };

        var grip = new Border
        {
            Height = 8,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
        };
        var gripLine = new Border
        {
            Height = 2,
            Width = 36,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grip.Child = gripLine;
        grip.PointerPressed += (_, e) =>
        {
            _dragging = true;
            _dragStartY = e.GetPosition(this).Y;
            _dragStartHeight = _host.Height;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            double h = Math.Clamp(_dragStartHeight + e.GetPosition(this).Y - _dragStartY, 60, 900);
            _host.Height = h;
            config.Height = h;
        };
        grip.PointerReleased += (_, e) =>
        {
            _dragging = false;
            e.Pointer.Capture(null);
            LayoutChanged?.Invoke();
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_host);
        stack.Children.Add(grip);
        Child = stack;
    }

    public VisualizerBase Visualizer { get; }
    public VisualizerConfig Config { get; }

    public event Action<VisualizerTile>? RemoveRequested;
    public event Action<VisualizerTile, int>? MoveRequested;
    public event Action<VisualizerTile>? BehindCodeChanged;
    public event Action? LayoutChanged;
}
