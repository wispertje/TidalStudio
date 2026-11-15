using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using TidalStudio.App.Services;
using TidalStudio.App.Visualizers;
using TidalStudio.Core.Completion;

namespace TidalStudio.App.Editor;

/// <summary>One entry in the completion list.</summary>
public sealed class CompletionItem : ICompletionData
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");
    private static readonly IBrush NameBrush = new SolidColorBrush(Color.Parse("#E6EAF2"));
    private static readonly IBrush DetailBrush = new SolidColorBrush(Color.Parse("#7F889A"));
    private static readonly IBrush KindBrush = new SolidColorBrush(Color.Parse("#4ECDC4"));

    private readonly string _insert;
    private readonly string? _detail;
    private readonly string? _doc;
    private readonly string _kind;
    private object? _content;
    private object? _description;

    public CompletionItem(string text, string kind, string? detail = null, string? doc = null, double priority = 0, string? insert = null)
    {
        Text = text;
        _kind = kind;
        _detail = detail;
        _doc = doc;
        _insert = insert ?? text;
        Priority = priority;
    }

    /// <summary>Called after insertion (e.g. to open value completion after "key=").</summary>
    public Action? AfterInsert { get; init; }

    public string Text { get; }
    public IImage? Image => null;
    public double Priority { get; }

    // Built on demand: the list is virtualised, so most items never need a control.
    public object Content => _content ??= BuildContent();

    public object? Description => _description ??= BuildDescription();

    private Control BuildContent()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = _kind, Foreground = KindBrush, FontSize = 10, Width = 14, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = Text, FontFamily = Mono, FontSize = 13, Foreground = NameBrush });
        if (!string.IsNullOrEmpty(_detail))
            panel.Children.Add(new TextBlock
            {
                Text = _detail.Length > 48 ? _detail[..47] + "…" : _detail,
                FontFamily = Mono,
                FontSize = 11,
                Foreground = DetailBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
        return panel;
    }

    private object? BuildDescription()
    {
        if (_detail is null && _doc is null) return null;
        var panel = new StackPanel { MaxWidth = 460, Spacing = 4 };
        if (_detail is not null)
            panel.Children.Add(new TextBlock { Text = $"{Text} :: {_detail}", FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (_doc is not null)
            panel.Children.Add(new TextBlock { Text = _doc, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, _insert);
        if (AfterInsert is { } after) Dispatcher.UIThread.Post(after, DispatcherPriority.Background);
    }
}

/// <summary>Knows every name that can be completed and builds the list for a request.</summary>
public sealed partial class CompletionCatalog
{
    private readonly AppServices _services;
    private Dictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private List<SampleEntry>? _samples;
    private readonly Dictionary<string, IReadOnlyList<VisOption>> _visualOptions = new();

    public CompletionCatalog(AppServices services)
    {
        _services = services;
    }

    /// <summary>Asks the running Tidal for every name it exports, with its type.</summary>
    public async Task LoadFromTidalAsync()
    {
        var lines = await _services.Ghci.QueryAsync(":browse Sound.Tidal.Context", TimeSpan.FromSeconds(20));
        var parsed = TidalNames.ParseBrowse(lines);
        if (parsed.Count > 0) _signatures = parsed;
    }

    public int TidalNameCount => _signatures.Count;

    /// <summary>Re-reads the sample folders next time samples are completed.</summary>
    public void InvalidateSamples() => _samples = null;

    public List<CompletionItem> Items(CompletionRequest request, TextDocument document, Action reopen) => request.Kind switch
    {
        CompletionKind.Code => CodeItems(request, document),
        CompletionKind.Sample => SampleItems(),
        CompletionKind.VisualName => VisualNameItems(),
        CompletionKind.VisualOptionKey => OptionKeyItems(request, reopen),
        CompletionKind.VisualOptionValue => OptionValueItems(request),
        _ => new List<CompletionItem>(),
    };

    // ------------------------------------------------------------------ code

    private List<CompletionItem> CodeItems(CompletionRequest request, TextDocument document)
    {
        var names = new Dictionary<string, (string? Signature, string? Doc, double Priority)>(StringComparer.Ordinal);
        // The half-typed word at the caret is itself in the document; don't suggest it back.
        foreach (var word in DocumentWords(document))
            if (word != request.Prefix) names[word] = (null, null, 0);
        foreach (var (name, sig) in _signatures) names[name] = (sig, null, 1);
        foreach (var name in TidalNames.BootNames) names[name] = (names.TryGetValue(name, out var e) ? e.Signature : null, null, 1);
        foreach (var (name, doc) in TidalNames.Docs)
            names[name] = (names.TryGetValue(name, out var e) ? e.Signature : null, doc, 2);

        var items = new List<CompletionItem>();
        foreach (var (name, (sig, doc, priority)) in names)
        {
            bool isEffect = sig is not null && sig.EndsWith("ControlPattern", StringComparison.Ordinal) && !sig.Contains("ControlPattern ->", StringComparison.Ordinal);
            double p = priority + (request.AfterHash && isEffect ? 3 : 0);
            string kind = sig is null && doc is null ? "ab" : isEffect ? "#" : "ƒ";
            items.Add(new CompletionItem(name, kind, sig, doc, p));
        }
        return items;
    }

    [GeneratedRegex(@"[A-Za-z_][\w']{2,}")]
    private static partial Regex Word();

    /// <summary>Identifiers used in the document's code (outside strings and comments).</summary>
    private static HashSet<string> DocumentWords(TextDocument document)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in document.Lines)
        {
            string text = document.GetText(line.Offset, line.Length);
            var code = new System.Text.StringBuilder(text.Length);
            bool inString = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    code.Append(' ');
                }
                else if (c == '"') { inString = true; code.Append(' '); }
                else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') break;
                else code.Append(c);
            }
            foreach (Match m in Word().Matches(code.ToString())) words.Add(m.Value);
        }
        return words;
    }

    // ------------------------------------------------------------------ samples

    private List<CompletionItem> SampleItems()
    {
        _samples ??= SampleLibrary.Scan();
        return _samples.Select(s => new CompletionItem(s.Name, s.IsSynth ? "♪" : "▪",
            s.IsSynth ? "synth" : s.Count == 1 ? "1 sample" : $"{s.Count} samples (:0 … :{s.Count - 1})")).ToList();
    }

    // ------------------------------------------------------------------ inline visuals

    private IReadOnlyList<VisOption> OptionsFor(string type)
    {
        if (type == "tscope") type = "scope";
        if (_visualOptions.TryGetValue(type, out var options)) return options;
        options = VisualizerFactory.Create(type, _services)?.DefineOptions() ?? Array.Empty<VisOption>();
        _visualOptions[type] = options;
        return options;
    }

    private List<CompletionItem> VisualNameItems() =>
        VisualizerFactory.Available.Select(v =>
        {
            var keys = OptionsFor(v.Type).Select(o => o.Key).Append("width").Append("height");
            return new CompletionItem("_" + v.Type, "◫", v.Title, "Options: " + string.Join(", ", keys));
        }).ToList();

    private List<CompletionItem> OptionKeyItems(CompletionRequest request, Action reopen)
    {
        var used = request.UsedKeys ?? new HashSet<string>();
        var items = new List<CompletionItem>();
        foreach (var o in OptionsFor(request.VisualType!))
        {
            if (used.Contains(o.Key)) continue;
            items.Add(new CompletionItem(o.Key, "=", Describe(o), o.Label, insert: o.Key + "=") { AfterInsert = reopen });
        }
        if (!used.Contains("width")) items.Add(new CompletionItem("width", "=", "pixels (default 360)", "Width of the inline visual", insert: "width="));
        if (!used.Contains("height")) items.Add(new CompletionItem("height", "=", "pixels", "Height of the inline visual", insert: "height="));
        return items;
    }

    private static string Describe(VisOption o) => o.Kind switch
    {
        OptionKind.Bool => $"true / false (default {o.Default})",
        OptionKind.Number => $"{Fmt(o.Min)}–{Fmt(o.Max)} (default {o.Default})",
        OptionKind.Choice => $"{string.Join(" / ", o.Choices ?? Array.Empty<string>())} (default {o.Default})",
        _ => $"colour, e.g. ff8800 (default {o.Default})",
    };

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private List<CompletionItem> OptionValueItems(CompletionRequest request)
    {
        var option = OptionsFor(request.VisualType!).FirstOrDefault(o => o.Key == request.OptionKey);
        if (option is null) return new List<CompletionItem>();
        IEnumerable<string> values = option.Kind switch
        {
            OptionKind.Bool => new[] { "true", "false" },
            OptionKind.Choice => option.Choices ?? Array.Empty<string>(),
            _ => new[] { option.Default },
        };
        return values.Where(v => v.Length > 0).Select(v => new CompletionItem(v, "·", v == option.Default ? "default" : null, option.Label)).ToList();
    }
}

/// <summary>Opens the completion popup while typing (or on Ctrl+Space).</summary>
public sealed class CompletionController
{
    private readonly TextEditor _editor;
    private readonly CompletionCatalog _catalog;
    private CompletionWindow? _window;

    public CompletionController(TextEditor editor, CompletionCatalog catalog)
    {
        _editor = editor;
        _catalog = catalog;
        editor.TextArea.TextEntered += OnTextEntered;
        // On the editor, not the text area: tunnelling reaches the editor first, so this runs before AvaloniaEdit
        // forwards the key to the completion popup (which would otherwise take Enter).
        editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public bool Enabled { get; set; } = true;

    public bool IsOpen => _window is not null;

    /// <summary>The suggestions currently listed (after filtering), for the self-test.</summary>
    public IReadOnlyList<string> VisibleItems =>
        _window?.CompletionList.CurrentList?.Select(i => i.Text).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

    public string? SelectedItem => _window?.CompletionList.SelectedItem?.Text;

    /// <summary>The popup's content, for the self-test's screenshot.</summary>
    public Control? PopupContent => _window?.Child as Control;

    public void Close() => _window?.Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            Open(force: true);
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _window is { } window)
        {
            // A word that is already complete: Enter means "new line", not "accept the suggestion".
            int caret = _editor.TextArea.Caret.Offset;
            string typed = caret > window.StartOffset ? _editor.Document.GetText(window.StartOffset, caret - window.StartOffset) : "";
            if (window.CompletionList.SelectedItem?.Text == typed || window.CompletionList.SelectedItem is null) window.Close();
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (!Enabled || string.IsNullOrEmpty(e.Text)) return;
        if (_window is not null)
        {
            // Close when nothing matches any more, instead of showing an empty list.
            if (_window.CompletionList.CurrentList is { Count: 0 }) _window.Close();
            return;
        }
        char c = e.Text[^1];
        if (char.IsLetterOrDigit(c) || c is '_' or '=' or ' ') Open(force: false, typed: c);
    }

    public void Open(bool force, char typed = '\0')
    {
        _window?.Close();
        var textArea = _editor.TextArea;
        var document = _editor.Document;
        if (document is null) return;
        int caret = textArea.Caret.Offset;
        var line = document.GetLineByOffset(caret);
        string text = document.GetText(line.Offset, line.Length);
        var request = CompletionContext.Analyze(text, caret - line.Offset);

        if (!force && !ShouldAutoOpen(request, typed)) return;

        var items = _catalog.Items(request, document, () => Open(force: true));
        if (request.Prefix.Length > 0)
            items = items.Where(i => i.Text.StartsWith(request.Prefix, StringComparison.OrdinalIgnoreCase) ||
                                     (request.Prefix.Length >= 3 && i.Text.Contains(request.Prefix, StringComparison.OrdinalIgnoreCase)))
                         .ToList();
        if (items.Count == 0) return;
        items.Sort((a, b) =>
        {
            bool pa = a.Text.StartsWith(request.Prefix, StringComparison.OrdinalIgnoreCase);
            bool pb = b.Text.StartsWith(request.Prefix, StringComparison.OrdinalIgnoreCase);
            if (pa != pb) return pa ? -1 : 1;
            return StringComparer.OrdinalIgnoreCase.Compare(a.Text, b.Text);
        });

        var window = new CompletionWindow(textArea)
        {
            StartOffset = line.Offset + request.Start,
            EndOffset = caret,
            CloseWhenCaretAtBeginning = request.Prefix.Length > 0,
            MinWidth = 320,
        };
        foreach (var item in items) window.CompletionList.CompletionData.Add(item);
        window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
        _window = window;
        window.Show();
        if (request.Prefix.Length > 0) window.CompletionList.SelectItem(request.Prefix);
    }

    private static bool ShouldAutoOpen(CompletionRequest request, char typed) => request.Kind switch
    {
        // Two letters before suggesting functions, so normal typing isn't interrupted.
        CompletionKind.Code => request.Prefix.Length >= 2 && (char.IsLetter(typed) || typed == '_'),
        CompletionKind.Sample => request.Prefix.Length >= 1 && char.IsLetterOrDigit(typed),
        CompletionKind.VisualName => typed is '_' || char.IsLetter(typed),
        CompletionKind.VisualOptionKey => typed == ' ' || (char.IsLetter(typed) && request.Prefix.Length >= 1),
        CompletionKind.VisualOptionValue => typed == '=' || char.IsLetter(typed),
        _ => false,
    };
}
