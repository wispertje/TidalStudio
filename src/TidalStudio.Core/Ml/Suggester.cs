using System.Text;

namespace TidalStudio.Core.Ml;

/// <summary>What to suggest.</summary>
public enum SuggestKind { Melody, Bass, Drums, Chords, NewPart }

public sealed record SuggestOptions
{
    public SuggestKind Kind { get; init; } = SuggestKind.Melody;
    public int Bars { get; init; } = 4;
    /// <summary>0–4, or -1 to match what's playing.</summary>
    public int Density { get; init; } = -1;
    public double Surprise { get; init; } = 0.4;
    public bool InKeyOnly { get; init; } = true;
    /// <summary>A sound (pitched parts) or kit (drums) to use, or null to choose.</summary>
    public string? Sound { get; init; }
    /// <summary>Use the trained model when there is one (else the rules).</summary>
    public bool UseModel { get; init; } = true;
    public int Count { get; init; } = 4;
    public double Bpm { get; init; } = 120;
}

/// <summary>One suggested part (or several layers for a new part), ready to paste.</summary>
public sealed class Suggestion
{
    public required string Code { get; init; }
    public required string Title { get; init; }
    public required string Source { get; init; } // "model" or "rules"
    public List<Layer> Layers { get; init; } = new();
    public double Taste { get; init; }

    /// <summary>One generated layer: the part, its tokens (for training on your ratings) and where it plays.</summary>
    public sealed record Layer(MPart Part, PartRequest Request, List<int> Tokens, int Channel, string Code, string SoundName);

    /// <summary>The same code on other channels (for listening without replacing what plays).</summary>
    public string CodeOnChannels(IReadOnlyList<int> channels)
    {
        var sb = new StringBuilder(Layers.Count > 1 ? "do\n" : "");
        for (int i = 0; i < Layers.Count; i++)
        {
            // The layer's code is "-- comment\ndN $ …": keep what follows "dN $".
            var line = Layers[i].Code.Split('\n').First(l => !l.StartsWith("--", StringComparison.Ordinal));
            string body = line[(line.IndexOf(" $ ", StringComparison.Ordinal) + 3)..];
            sb.Append(Layers.Count > 1 ? "  " : "").Append('d').Append(channels[Math.Min(i, channels.Count - 1)]).Append(" $ ").Append(body).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Makes suggestions that fit what's playing, with the model when there is one, else with the rules.</summary>
public sealed class Suggester
{
    public SampleCatalog Catalog { get; set; }
    public MuseModel? Model { get; set; }
    public TasteModel Taste { get; set; }

    public Suggester(SampleCatalog catalog, TasteModel taste, MuseModel? model = null)
    {
        Catalog = catalog;
        Taste = taste;
        Model = model;
    }

    public static PartRole RoleOf(SuggestKind kind) => kind switch
    {
        SuggestKind.Bass => PartRole.Bass,
        SuggestKind.Drums => PartRole.Drums,
        SuggestKind.Chords => PartRole.Chords,
        _ => PartRole.Melody,
    };

    /// <summary>The request for a role, from what's playing.</summary>
    public PartRequest RequestFor(PartRole role, TidalContext context, SuggestOptions o)
    {
        bool pitched = context.Parts.Any(p => p.Part.Role != PartRole.Drums);
        var key = pitched ? context.Key : MKey.CMinor;
        var chords = pitched && context.Chords.Any(c => c.Quality != ChordQuality.None)
            ? FillChords(context.Chords, key)
            : new List<MChord> { new(key.Tonic, key.Minor ? ChordQuality.Minor : ChordQuality.Major) };
        // Chords of the context repeat over the part's bars.
        var kicks = context.Parts.Where(p => p.Part.Role == PartRole.Drums).SelectMany(p => p.Part.Notes)
            .Where(n => n.Pitch == 0 && n.Start < 1).Select(n => (int)Math.Round(n.Start * 16)).Where(s => s < 16).Distinct().ToList();
        int density = o.Density;
        if (density < 0)
        {
            var same = context.Parts.Where(p => p.Part.Role == role).Select(p => p.Part).FirstOrDefault();
            density = same is not null ? Tokenizer.DensityOf(same) : role switch { PartRole.Drums => 2, PartRole.Chords => 1, _ => 2 };
        }
        return new PartRequest
        {
            Role = role, Key = key, Bars = Tokenizer.LengthChoices.Contains(o.Bars) ? o.Bars : 4, Density = density,
            Bpm = o.Bpm, Chords = chords, KickSteps = role == PartRole.Drums ? Array.Empty<int>() : kicks,
        };
    }

    private static List<MChord> FillChords(List<MChord> chords, MKey key)
    {
        var list = chords.ToList();
        var tonic = new MChord(key.Tonic, key.Minor ? ChordQuality.Minor : ChordQuality.Major);
        for (int i = 0; i < list.Count; i++)
            if (list[i].Quality == ChordQuality.None) list[i] = i > 0 ? list[i - 1] : tonic;
        return list;
    }

    /// <summary>The lowest dN not used by what's playing (and not in <paramref name="taken"/>).</summary>
    public static int FreeChannel(TidalContext context, IEnumerable<int>? taken = null)
    {
        var used = context.Parts.Select(p => int.TryParse(p.Pattern, out int n) ? n : 0).Concat(taken ?? Array.Empty<int>()).ToHashSet();
        for (int c = 1; c <= 12; c++)
            if (!used.Contains(c)) return c;
        return 12;
    }

    /// <summary>Suggestions for the options. Runs the model (if any) — call it off the UI thread.</summary>
    public List<Suggestion> Suggest(TidalContext context, SuggestOptions o, Random random, CancellationToken token = default)
    {
        if (o.Kind == SuggestKind.NewPart) return NewParts(context, o, random, token);
        var role = RoleOf(o.Kind);
        var request = RequestFor(role, context, o);
        int channel = FreeChannel(context);
        var (sound, kit) = ChooseSound(role, context, o.Sound, random);
        var parts = Generate(request, Math.Max(1, o.Count), o, random, token);
        var result = new List<Suggestion>();
        foreach (var (part, tokens, source, taste) in parts)
        {
            var layer = MakeLayer(part, request, tokens, channel, sound, kit);
            if (layer is null) continue;
            result.Add(new Suggestion
            {
                Code = layer.Code, Title = $"{Describe(role)} · {layer.SoundName}", Source = source, Layers = { layer }, Taste = taste,
            });
        }
        return result;
    }

    private static string Describe(PartRole role) => role switch
    {
        PartRole.Bass => "bassline",
        PartRole.Drums => "drums",
        PartRole.Chords => "chords",
        _ => "melody",
    };

    /// <summary>
    /// Generates parts: the model makes twice as many as asked and your taste picks the best (always keeping one
    /// unexpected one, so it doesn't get stuck on one idea); without a model the rules make them.
    /// </summary>
    public List<(MPart Part, List<int> Tokens, string Source, double Taste)> Generate(PartRequest request, int count, SuggestOptions o, Random random, CancellationToken token = default)
    {
        var made = new List<(MPart Part, List<int> Tokens, string Source)>();
        if (o.UseModel && Model is not null)
        {
            try
            {
                var sampling = new SamplingOptions { Surprise = o.Surprise, InKeyOnly = o.InKeyOnly };
                foreach (var seq in Model.Generate(request, count + 2, sampling, random, token))
                {
                    var part = Tokenizer.Decode(seq, request.Key);
                    part.Role = request.Role;
                    if (part.Notes.Count > 0) made.Add((part, seq, "model"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* fall back to the rules */ }
        }
        if (made.Count == 0)
        {
            // The rules: try new seeds until there are enough different ideas (simple requests repeat often).
            var seen = new HashSet<ulong>();
            for (int attempt = 0; attempt < count * 8 && made.Count < count * 2; attempt++)
            {
                var part = RuleGenerator.Generate(request, new Random(random.Next()));
                var tokens = Tokenizer.Encode(part, request);
                if (seen.Add(Dataset.Hash(tokens))) made.Add((part, tokens, "rules"));
            }
        }
        // Drop exact duplicates.
        made = made.GroupBy(m => Dataset.Hash(m.Tokens)).Select(g => g.First()).ToList();
        var scored = made.Select(m => (m.Part, m.Tokens, m.Source, Taste: Taste.Score(request.Role, PartFeatures.Of(m.Part, request, m.Source == "model")) + Variety(m.Part)))
            .OrderByDescending(m => m.Taste).ToList();
        var pick = scored.Take(Math.Max(1, count - 1)).ToList();
        var rest = scored.Skip(pick.Count).ToList();
        if (rest.Count > 0 && pick.Count < count) pick.Add(rest[random.Next(rest.Count)]);
        return pick;
    }


    /// <summary>
    /// A small built-in preference against monotony (one note repeated all the way): models trained on MIDI like to
    /// repeat. Your taste (learned from ratings) comes on top and can overrule it.
    /// </summary>
    public static double Variety(MPart part)
    {
        if (part.Role == PartRole.Drums || part.Notes.Count < 4) return 0;
        int pitches = part.Notes.Select(n => n.Pitch).Distinct().Count();
        int rhythms = Enumerable.Range(0, Math.Max(1, part.Bars))
            .Select(b => string.Join(",", part.Notes.Where(n => (int)Math.Floor(n.Start) == b).Select(n => Math.Round((n.Start - b) * 16)))).Distinct().Count();
        double score = 0;
        if (pitches == 1) score -= part.Role == PartRole.Bass ? 0.15 : 0.5;
        else if (pitches == 2 && part.Role == PartRole.Melody) score -= 0.2;
        if (part.Role == PartRole.Melody) score += Math.Min(5, pitches) * 0.04;
        // Exactly the same steady pulse in every bar.
        var gaps = part.Notes.Select(n => n.Start).Distinct().OrderBy(s => s).Zip(part.Notes.Select(n => n.Start).Distinct().OrderBy(s => s).Skip(1), (a, b) => Math.Round((b - a) * 16)).Distinct().Count();
        if (gaps <= 1 && rhythms <= 1) score -= 0.15;
        return score;
    }
    /// <summary>Picks the sound: the one asked for, else one of the role that isn't playing yet.</summary>
    public (SoundInfo? Sound, string? Kit) ChooseSound(PartRole role, TidalContext context, string? wanted, Random random)
    {
        if (role == PartRole.Drums)
        {
            var kits = Catalog.Kits.Keys.ToList();
            if (wanted is not null && kits.Contains(wanted)) return (null, wanted);
            // The kit that is playing, so the new drums match.
            var playing = context.Parts.Where(p => p.Part.Role == PartRole.Drums && p.Part.Sound is not null)
                .Select(p => Catalog.Find(p.Part.Sound!)?.Kit).OfType<string>().FirstOrDefault();
            if (playing is not null) return (null, playing);
            return (null, kits.Count == 0 ? null : kits[random.Next(kits.Count)]);
        }
        if (wanted is not null && Catalog.Find(wanted) is { Kind: SoundKind.Pitched } chosen) return (chosen, null);
        var playingSounds = context.Parts.Select(p => p.Part.Sound).OfType<string>().ToHashSet();
        var options = Catalog.ForRole(role).Where(s => !playingSounds.Contains(s.Name)).ToList();
        if (options.Count == 0) options = Catalog.ForRole(role).ToList();
        if (options.Count == 0) options = Catalog.Sounds.Where(s => s.Kind == SoundKind.Pitched).ToList();
        return (options.Count == 0 ? null : options[random.Next(options.Count)], null);
    }

    private Suggestion.Layer? MakeLayer(MPart part, PartRequest request, List<int> tokens, int channel, SoundInfo? sound, string? kit)
    {
        string comment = $"suggestion: {Describe(part.Role)} · {request.Key} · {part.Bars} bar{(part.Bars > 1 ? "s" : "")}";
        if (part.Role == PartRole.Drums)
        {
            if (kit is null) return null;
            return new Suggestion.Layer(part, request, tokens, channel, TidalWriter.DrumPart(part, Catalog, kit, channel, comment + $" · kit {kit}"), kit);
        }
        if (sound is null) return null;
        return new Suggestion.Layer(part, request, tokens, channel, TidalWriter.Pitched(part, sound, channel, comment + $" · {sound.Name}"), sound.Name);
    }

    /// <summary>
    /// A new part of the track: the roles that aren't playing get a new layer, and one playing role gets a fresh
    /// variation, as one block. The comments say which playing layers to drop for a breakdown.
    /// </summary>
    private List<Suggestion> NewParts(TidalContext context, SuggestOptions o, Random random, CancellationToken token)
    {
        var result = new List<Suggestion>();
        var playingRoles = context.Parts.Select(p => p.Part.Role).Distinct().ToList();
        for (int k = 0; k < Math.Max(1, o.Count); k++)
        {
            var rng = new Random(random.Next());
            var roles = new List<PartRole>();
            var missing = new[] { PartRole.Drums, PartRole.Bass, PartRole.Chords, PartRole.Melody }.Where(r => !playingRoles.Contains(r)).ToList();
            roles.AddRange(missing.OrderBy(_ => rng.Next()).Take(2));
            // Replace something that plays, with a variation (different density).
            var replace = playingRoles.Where(r => !roles.Contains(r)).OrderBy(_ => rng.Next()).FirstOrDefault(PartRole.Melody);
            if (roles.Count < 3 && !roles.Contains(replace)) roles.Add(replace);
            var layers = new List<Suggestion.Layer>();
            var taken = new List<int>();
            double taste = 0;
            string source = "rules";
            foreach (var role in roles)
            {
                var existing = context.Parts.FirstOrDefault(p => p.Part.Role == role);
                int channel = existing.Part is not null && int.TryParse(existing.Pattern, out int ch) ? ch : FreeChannel(context, taken);
                taken.Add(channel);
                var req = RequestFor(role, context, o with { Count = 1 });
                if (existing.Part is not null) req = req with { Density = Math.Clamp(Tokenizer.DensityOf(existing.Part) + (rng.Next(2) == 0 ? -1 : 1), 0, 4) };
                var generated = Generate(req, 1, o, rng, token);
                if (generated.Count == 0) continue;
                var (part, tokens, src, t) = generated[0];
                var (sound, kit) = ChooseSound(role, context, null, rng);
                if (existing.Part?.Sound is { } playingSound && role != PartRole.Drums && Catalog.Find(playingSound) is { Kind: SoundKind.Pitched } same) sound = same;
                var layer = MakeLayer(part, req, tokens, channel, sound, kit);
                if (layer is null) continue;
                layers.Add(layer);
                taste += t;
                if (src == "model") source = "model";
            }
            if (layers.Count == 0) continue;
            var sb = new StringBuilder();
            sb.Append("-- suggestion: new part · ").Append(string.Join(", ", layers.Select(l => $"{Describe(l.Part.Role)} (d{l.Channel})"))).Append('\n');
            var keep = context.Parts.Where(p => !layers.Any(l => l.Channel.ToString() == p.Pattern)).Select(p => p.Pattern).Distinct().ToList();
            if (keep.Count > 0 && rng.NextDouble() < 0.5)
                sb.Append("-- for a breakdown, also drop: ").Append(string.Join(", ", keep.Take(2).Select(p => $"d{p} silence"))).Append('\n');
            sb.Append("do\n");
            foreach (var l in layers)
                foreach (var line in l.Code.Split('\n').Where(x => !x.StartsWith("--", StringComparison.Ordinal)))
                    sb.Append("  ").Append(line).Append('\n');
            result.Add(new Suggestion
            {
                Code = sb.ToString().TrimEnd(), Title = "new part · " + string.Join(" + ", layers.Select(l => Describe(l.Part.Role))),
                Source = source, Layers = layers, Taste = taste / layers.Count,
            });
        }
        return result;
    }
}
