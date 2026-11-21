using System.Globalization;
using TidalStudio.App.Services;
using TidalStudio.Core.Ml;
using TidalStudio.Core.Tidal;

namespace TidalStudio.App.Muse;

/// <summary>
/// Tidepool Muse inside TidalStudio: the sample catalog, the model in use, your taste, and the link to the live
/// Tidal (what's playing, listening to suggestions on spare channels).
/// </summary>
public sealed class MuseService : IDisposable
{
    /// <summary>Channels used to listen to a suggestion without touching what plays.</summary>
    public static readonly int[] AuditionChannels = { 14, 15, 16 };

    private readonly AppServices _services;
    private bool _helpersDefined;
    private SampleCatalog? _catalog;
    private MuseModel? _model;
    private string? _modelLoadError;

    public TasteModel Taste { get; }
    public bool Auditioning { get; private set; }

    public MuseService(AppServices services)
    {
        _services = services;
        Taste = TasteModel.Load(MlPaths.Taste);
        services.Ghci.StateChanged += _ => _helpersDefined = false;
    }

    public string SamplesRoot => Environment.GetEnvironmentVariable("TIDALSTUDIO_MUSE_SAMPLES") is { Length: > 0 } env ? env
        : _services.Settings.SamplesFolder is { Length: > 0 } f ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "samples");

    public SampleCatalog Catalog => _catalog ??= SampleCatalog.Scan(SamplesRoot, SampleCatalog.LoadOverrides(MlPaths.RoleOverrides));

    public void RefreshCatalog() => _catalog = null;

    /// <summary>The model version in use: the chosen one, else the newest.</summary>
    public ModelVersion? ActiveVersion
    {
        get
        {
            var all = ModelVersion.All();
            return all.FirstOrDefault(v => v.Name == _services.Settings.Muse.ActiveModel) ?? all.FirstOrDefault();
        }
    }

    /// <summary>The loaded model (loads it the first time), or null when there is none or it failed.</summary>
    public MuseModel? Model
    {
        get
        {
            var version = ActiveVersion;
            if (version is null) { DisposeModel(); return null; }
            if (_model is not null && string.Equals(_model.Path, version.Onnx, StringComparison.OrdinalIgnoreCase)) return _model;
            DisposeModel();
            try
            {
                _model = MuseModel.Load(version.Onnx);
                _modelLoadError = null;
            }
            catch (Exception e)
            {
                _modelLoadError = e.Message;
            }
            return _model;
        }
    }

    public string ModelStatus
    {
        get
        {
            if (!_services.Settings.Muse.UseModel) return "rules (model switched off)";
            var m = Model;
            if (m is null) return _modelLoadError is null ? "rules (no model trained yet)" : $"rules (model failed to load: {_modelLoadError})";
            return $"model {ActiveVersion?.Name} on {(m.OnGpu ? "GPU" : "CPU")}";
        }
    }

    public void UseVersion(string? name)
    {
        _services.Settings.Muse.ActiveModel = name;
        DisposeModel();
    }

    private void DisposeModel()
    {
        _model?.Dispose();
        _model = null;
    }

    public Suggester CreateSuggester() =>
        new(Catalog, Taste, _services.Settings.Muse.UseModel ? Model : null);

    /// <summary>What plays right now (the next <paramref name="bars"/> cycles), worked out by Tidal itself.</summary>
    public async Task<(TidalContext Context, double Bpm)> LiveContextAsync(int bars)
    {
        var ghci = _services.Ghci;
        if (!ghci.IsReady) return (TidalContext.Build(Array.Empty<DumpEvent>(), Catalog, 0, bars), 120);
        if (!_helpersDefined) _helpersDefined = await TidalRunner.DefineHelpersAsync(ghci);
        // Tidal's "<a b c d>" follows the cycle number, and so will the suggestion: read the music from a cycle that
        // is a multiple of 8, so bar k of the context is what plays with bar k of the suggestion.
        TidalContext.Parse(await ghci.QueryAsync("tsDumpNow 0", TimeSpan.FromSeconds(10)), out double? now);
        int span = Math.Max(8, bars);
        double from = Math.Floor((now ?? 0) / span) * span;
        var lines = await ghci.QueryAsync(
            $"tsDump {from.ToString(CultureInfo.InvariantCulture)} {(from + span).ToString(CultureInfo.InvariantCulture)}", TimeSpan.FromSeconds(10));
        var events = TidalContext.Parse(lines, out _);
        bars = span;
        // Leave out what we're listening to ourselves.
        events = events.Where(e => !AuditionChannels.Contains(int.TryParse(e.Pattern, out int n) ? n : 0)).ToList();
        double bpm = 120;
        foreach (var l in await ghci.QueryAsync("getcps >>= print", TimeSpan.FromSeconds(5)))
            if (double.TryParse(l.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cps) && cps > 0)
                bpm = Math.Round(cps * 240);
        return (TidalContext.Build(events, Catalog, from, bars, now), bpm);
    }

    /// <summary>Plays a suggestion on the spare channels (d14–d16), in time with the music.</summary>
    public async Task<EvalResult> AuditionAsync(Suggestion s)
    {
        if (!_services.Ghci.IsReady) return EvalResult.NotRunning;
        await StopAuditionAsync();
        var result = await _services.Ghci.EvalAsync(s.CodeOnChannels(AuditionChannels));
        Auditioning = result.Success;
        return result;
    }

    public async Task StopAuditionAsync()
    {
        if (!_services.Ghci.IsReady) return;
        await _services.Ghci.EvalQuietAsync(string.Join("\n", new[] { "do" }.Concat(AuditionChannels.Select(c => $"  d{c} silence"))));
        Auditioning = false;
    }

    // ------------------------------------------------------------------ ratings

    public int RatingCount => RatingStore.All().Count;

    /// <summary>You inserted a suggestion: a like for every layer.</summary>
    public void RecordInsert(Suggestion s) => Like(s, "insert");

    public void Like(Suggestion s, string kind = "like", IEnumerable<string>? reasons = null)
    {
        foreach (var l in s.Layers)
        {
            Taste.Rate(l.Part.Role, PartFeatures.Of(l.Part, l.Request, s.Source == "model"), true);
            RatingStore.Add(new RatingRecord
            {
                Kind = kind, Role = l.Part.Role.ToString(), Prompt = Prompt(l.Tokens), Chosen = Body(l.Tokens),
                Sound = l.SoundName, Source = s.Source, Reasons = reasons?.ToList() ?? new(),
            });
        }
        Taste.Save(MlPaths.Taste);
    }

    public void Dislike(Suggestion s, IEnumerable<string>? reasons = null)
    {
        foreach (var l in s.Layers)
        {
            Taste.Rate(l.Part.Role, PartFeatures.Of(l.Part, l.Request, s.Source == "model"), false);
            RatingStore.Add(new RatingRecord
            {
                Kind = "dislike", Role = l.Part.Role.ToString(), Prompt = Prompt(l.Tokens), Disliked = Body(l.Tokens),
                Sound = l.SoundName, Source = s.Source, Reasons = reasons?.ToList() ?? new(),
            });
        }
        Taste.Save(MlPaths.Taste);
    }

    /// <summary>You preferred <paramref name="better"/> over <paramref name="worse"/> (same request).</summary>
    public void Prefer(Suggestion better, Suggestion worse, IEnumerable<string>? reasons = null)
    {
        var b = better.Layers[0];
        var w = worse.Layers[0];
        Taste.Prefer(b.Part.Role, PartFeatures.Of(b.Part, b.Request, better.Source == "model"), PartFeatures.Of(w.Part, w.Request, worse.Source == "model"));
        RatingStore.Add(new RatingRecord
        {
            Kind = "pair", Role = b.Part.Role.ToString(), Prompt = Prompt(b.Tokens), Chosen = Body(b.Tokens), Rejected = Body(w.Tokens),
            Sound = b.SoundName, Source = better.Source, Reasons = reasons?.ToList() ?? new(),
        });
        Taste.Save(MlPaths.Taste);
    }

    private static List<int> Prompt(List<int> tokens) => tokens.Take(tokens.IndexOf(Tokenizer.Sep) + 1).ToList();
    private static List<int> Body(List<int> tokens) => tokens.Skip(tokens.IndexOf(Tokenizer.Sep) + 1).ToList();

    public void Dispose() => DisposeModel();
}
