using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TidalStudio.App.Explorer;

/// <summary>
/// Folder tree of the working directory. Folders load when expanded; changes on disk are picked up by a watcher and
/// merged into the tree in place, so scroll position, selection and expanded folders survive a refresh.
/// </summary>
public sealed class FileExplorer : DockPanel
{
    private const int MaxEntriesPerFolder = 2000;

    private static readonly Geometry FolderIcon = Geometry.Parse("M1.5,3 L6,3 L7.5,4.5 L14.5,4.5 L14.5,13 L1.5,13 Z");
    private static readonly Geometry FileIcon = Geometry.Parse("M3,1.5 L9.5,1.5 L13,5 L13,14.5 L3,14.5 Z M9.5,1.5 L9.5,5 L13,5");
    private static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IBrush TidalBrush = new SolidColorBrush(Color.Parse("#4ECDC4"));
    private static readonly IBrush OtherFileBrush = new SolidColorBrush(Color.Parse("#5C6475"));
    private static readonly IBrush CurrentFileBrush = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush NameBrush = new SolidColorBrush(Color.Parse("#B8C0CF"));

    private readonly TreeView _tree;
    private readonly TextBlock _rootLabel;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private FileSystemWatcher? _watcher;
    private string? _currentFile;

    private sealed record Node(string Path, bool IsDirectory)
    {
        public bool Populated { get; set; }
    }

    public FileExplorer()
    {
        Background = (IBrush?)Application.Current?.FindResource("TsWindowBackground") ?? Brushes.Transparent;

        var title = new TextBlock { Text = "EXPLORER" };
        title.Classes.Add("tileTitle");

        var up = IconButton("↑", "Parent folder (make it the working directory)");
        up.Click += (_, _) =>
        {
            if (Root is not null && Path.GetDirectoryName(Root) is { } parent) WorkingDirectoryRequested?.Invoke(parent);
        };
        var refresh = IconButton("↻", "Refresh");
        refresh.Click += (_, _) => Refresh();
        var choose = new Button { Content = new PathIcon { Data = FolderIcon, Width = 13, Height = 13 } };
        choose.Classes.Add("icon");
        ToolTip.SetTip(choose, "Set working directory…");
        choose.Click += (_, _) => ChooseFolderRequested?.Invoke();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(up);
        buttons.Children.Add(refresh);
        buttons.Children.Add(choose);
        var header = new DockPanel { Margin = new Thickness(10, 6, 4, 2) };
        SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(title);
        SetDock(header, Dock.Top);
        Children.Add(header);

        _rootLabel = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Foreground = CurrentFileBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 2, 8, 4),
        };
        SetDock(_rootLabel, Dock.Top);
        Children.Add(_rootLabel);

        _tree = new TreeView { Margin = new Thickness(0, 0, 0, 4) };
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _tree.SelectedItem is TreeViewItem { Tag: Node { IsDirectory: false } node })
            {
                e.Handled = true;
                FileOpenRequested?.Invoke(node.Path);
            }
        };
        _tree.ContextMenu = new ContextMenu { ItemsSource = FolderMenu(() => Root) };
        Children.Add(_tree);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh();
        };
    }

    /// <summary>The folder shown at the top of the tree.</summary>
    public string? Root { get; private set; }

    public event Action<string>? FileOpenRequested;
    public event Action<string>? WorkingDirectoryRequested;
    public event Action<string>? NewFileRequested;
    public event Action? ChooseFolderRequested;

    public void SetRoot(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(directory, Root, StringComparison.OrdinalIgnoreCase)) return;
        Root = directory;
        _rootLabel.Text = Path.GetFileName(directory) is { Length: > 0 } name ? name : directory;
        ToolTip.SetTip(_rootLabel, directory);
        _expanded.Clear();
        _labels.Clear();
        _tree.Items.Clear();
        Populate(_tree.Items, directory);
        Watch(directory);
    }

    /// <summary>Shows which file is open in the editor.</summary>
    public void SetCurrentFile(string? path)
    {
        if (_currentFile is not null && _labels.TryGetValue(_currentFile, out var old)) StyleLabel(old, _currentFile);
        _currentFile = path;
        if (path is not null && _labels.TryGetValue(path, out var label)) StyleLabel(label, path);
    }

    /// <summary>Re-reads every loaded folder and merges the differences into the tree.</summary>
    public void Refresh()
    {
        if (Root is null) return;
        if (!Directory.Exists(Root))
        {
            _tree.Items.Clear();
            _labels.Clear();
            _rootLabel.Text = Path.GetFileName(Root) + " (missing)";
            return;
        }
        Sync(_tree.Items, Root);
    }

    // ------------------------------------------------------------------ tree building

    private static List<(string Path, bool IsDirectory)> ReadEntries(string directory)
    {
        var result = new List<(string, bool)>();
        try
        {
            var info = new DirectoryInfo(directory);
            foreach (var entry in info.EnumerateFileSystemInfos())
            {
                if (entry.Name.StartsWith('.') || (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                result.Add((entry.FullName, entry is DirectoryInfo));
                if (result.Count >= MaxEntriesPerFolder) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        result.Sort((a, b) => a.Item2 != b.Item2
            ? (a.Item2 ? -1 : 1)
            : StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(a.Item1), Path.GetFileName(b.Item1)));
        return result;
    }

    private void Populate(ItemCollection items, string directory)
    {
        items.Clear();
        foreach (var (path, isDir) in ReadEntries(directory)) items.Add(CreateItem(path, isDir));
    }

    /// <summary>Brings <paramref name="items"/> in line with the folder's contents, recursing into loaded folders.</summary>
    private void Sync(ItemCollection items, string directory)
    {
        var entries = ReadEntries(directory);
        var wanted = new HashSet<string>(entries.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);

        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is TreeViewItem { Tag: Node node } && wanted.Contains(node.Path)) continue;
            if (items[i] is TreeViewItem { Tag: Node gone }) Forget(gone.Path);
            items.RemoveAt(i);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var (path, isDir) = entries[i];
            if (i < items.Count && items[i] is TreeViewItem { Tag: Node existing } &&
                string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase) && existing.IsDirectory == isDir)
            {
                if (existing.IsDirectory && existing.Populated) Sync(((TreeViewItem)items[i]!).Items, path);
                continue;
            }
            // Out of place (new, or a file replaced by a folder of the same name): drop any stale copy and insert.
            for (int j = items.Count - 1; j >= i; j--)
                if (items[j] is TreeViewItem { Tag: Node n } && string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase))
                    items.RemoveAt(j);
            items.Insert(i, CreateItem(path, isDir));
        }
    }

    private void Forget(string path)
    {
        _expanded.RemoveWhere(p => p.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                                   p.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        foreach (var key in _labels.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                                                    k.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
            _labels.Remove(key);
    }

    private TreeViewItem CreateItem(string path, bool isDirectory)
    {
        var node = new Node(path, isDirectory);
        var label = new TextBlock { Text = Path.GetFileName(path), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        StyleLabel(label, path);
        _labels[path] = label;

        var icon = new PathIcon
        {
            Data = isDirectory ? FolderIcon : FileIcon,
            Width = 13,
            Height = 13,
            Foreground = isDirectory ? FolderBrush : IsTidalFile(path) ? TidalBrush : OtherFileBrush,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Background = Brushes.Transparent };
        header.Children.Add(icon);
        header.Children.Add(label);
        ToolTip.SetTip(header, path);

        var item = new TreeViewItem { Header = header, Tag = node, MinHeight = 24, Padding = new Thickness(0) };
        if (isDirectory)
        {
            header.ContextMenu = new ContextMenu { ItemsSource = FolderMenu(() => path) };
            item.Items.Add(new TreeViewItem { Header = "…", IsEnabled = false }); // placeholder so the expander shows
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty) return;
                if (item.IsExpanded)
                {
                    _expanded.Add(path);
                    if (!node.Populated)
                    {
                        node.Populated = true;
                        Populate(item.Items, path);
                    }
                }
                else _expanded.Remove(path);
            };
            if (_expanded.Contains(path)) item.IsExpanded = true;
        }
        else
        {
            header.ContextMenu = new ContextMenu { ItemsSource = FileMenu(path) };
            header.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                FileOpenRequested?.Invoke(path);
            };
        }
        return item;
    }

    private void StyleLabel(TextBlock label, string path)
    {
        bool current = string.Equals(path, _currentFile, StringComparison.OrdinalIgnoreCase);
        label.FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal;
        label.Foreground = current ? CurrentFileBrush : NameBrush;
    }

    private static bool IsTidalFile(string path) =>
        path.EndsWith(".tidal", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".hs", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ menus

    private List<MenuItem> FolderMenu(Func<string?> folder)
    {
        var setWorking = new MenuItem { Header = "Set as working directory" };
        setWorking.Click += (_, _) => { if (folder() is { } f) WorkingDirectoryRequested?.Invoke(f); };
        var newFile = new MenuItem { Header = "New Tidal file here…" };
        newFile.Click += (_, _) => { if (folder() is { } f) NewFileRequested?.Invoke(f); };
        var reveal = new MenuItem { Header = "Show in file manager" };
        reveal.Click += (_, _) => { if (folder() is { } f) Reveal(f, select: false); };
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += (_, _) => { if (folder() is { } f) CopyPath(f); };
        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => Refresh();
        return new List<MenuItem> { newFile, setWorking, reveal, copy, refresh };
    }

    private List<MenuItem> FileMenu(string path)
    {
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => FileOpenRequested?.Invoke(path);
        var reveal = new MenuItem { Header = "Show in file manager" };
        reveal.Click += (_, _) => Reveal(path, select: true);
        var copy = new MenuItem { Header = "Copy path" };
        copy.Click += (_, _) => CopyPath(path);
        return new List<MenuItem> { open, reveal, copy };
    }

    private static void Reveal(string path, bool select)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"");
            else
                Process.Start(new ProcessStartInfo(select ? Path.GetDirectoryName(path)! : path) { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private async void CopyPath(string path)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(path);
        }
        catch (Exception) { }
    }

    private static Button IconButton(string text, string tip)
    {
        var b = new Button { Content = text };
        b.Classes.Add("icon");
        ToolTip.SetTip(b, tip);
        return b;
    }

    // ------------------------------------------------------------------ watching

    private void Watch(string directory)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                // Watching a whole drive recursively is expensive; then only the top level is kept up to date.
                IncludeSubdirectories = Path.GetPathRoot(directory) is not { } r || !string.Equals(Path.TrimEndingDirectorySeparator(r), directory, StringComparison.OrdinalIgnoreCase),
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(ScheduleRefresh);
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(ScheduleRefresh);
            watcher.Error += (_, _) => Dispatcher.UIThread.Post(ScheduleRefresh);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _watcher?.Dispose();
        _watcher = null;
        _refreshTimer.Stop();
    }
}
