using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidalStudio.Core.Ml;

/// <summary>Simple facts about a part that taste can depend on (busy, jumpy, syncopated, repetitive …).</summary>
public static class PartFeatures
{
    public static readonly string[] Names =
    {
        "density", "pitch variety", "range", "height", "steps", "leaps", "syncopation", "repetition",
        "chord tones", "off key", "rests", "length", "on the kick", "from model", "bias",
    };

    public static double[] Of(MPart part, PartRequest request, bool fromModel)
    {
        var f = new double[Names.Length];
        var notes = part.Notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        int bars = Math.Max(1, part.Bars);
        var onsets = notes.Select(n => (int)Math.Round(n.Start * 16)).Distinct().ToList();
        f[0] = onsets.Count / (8.0 * bars);
        f[13] = fromModel ? 1 : 0;
        f[14] = 1;
        if (notes.Count == 0) return f;
        f[6] = onsets.Count(s => s % 4 != 0) / (double)onsets.Count;
        var barRhythms = Enumerable.Range(0, bars).Select(b => string.Join(",", onsets.Where(s => s / 16 == b).Select(s => s % 16))).ToList();
        f[7] = barRhythms.Count(r => r == barRhythms[0]) / (double)bars;
        if (request.KickSteps.Count > 0)
            f[12] = onsets.Count(s => request.KickSteps.Contains(s % 16)) / (double)onsets.Count;
        if (part.Role == PartRole.Drums)
        {
            f[1] = notes.Select(n => n.Pitch).Distinct().Count() / 6.0;
            f[10] = 1 - onsets.Select(s => s % 16).Distinct().Count() / 16.0;
            return f;
        }
        var pitches = notes.Select(n => n.Pitch).ToList();
        var (lo, hi) = TokenGrammar.Range(part.Role);
        f[1] = pitches.Select(p => ((p % 12) + 12) % 12).Distinct().Count() / 7.0;
        f[2] = (pitches.Max() - pitches.Min()) / 24.0;
        f[3] = (pitches.Average() - (lo + hi) / 2.0) / 12.0;
        var line = notes.GroupBy(n => n.Start).Select(g => part.Role == PartRole.Bass ? g.Min(n => n.Pitch) : g.Max(n => n.Pitch)).ToList();
        int moves = Math.Max(1, line.Count - 1);
        f[4] = line.Zip(line.Skip(1), (a, b) => Math.Abs(b - a)).Count(d => d is >= 1 and <= 2) / (double)moves;
        f[5] = line.Zip(line.Skip(1), (a, b) => Math.Abs(b - a)).Count(d => d > 5) / (double)moves;
        int chordTones = 0, offKey = 0;
        foreach (var n in notes)
        {
            int bar = Math.Clamp((int)Math.Floor(n.Start), 0, bars - 1);
            var chord = request.Chords.Count > 0 ? request.Chords[bar % request.Chords.Count] : MChord.None;
            if (chord.PitchClasses.Contains(((n.Pitch % 12) + 12) % 12)) chordTones++;
            if (!request.Key.Contains(n.Pitch)) offKey++;
        }
        f[8] = chordTones / (double)notes.Count;
        f[9] = offKey / (double)notes.Count;
        var sounding = new bool[16 * bars];
        foreach (var n in notes)
            for (int s = (int)Math.Round(n.Start * 16); s < Math.Min(sounding.Length, Math.Round((n.Start + n.Dur) * 16)); s++) sounding[s] = true;
        f[10] = sounding.Count(x => !x) / (double)sounding.Length;
        f[11] = notes.Average(n => n.Dur) * 2;
        return f;
    }
}

/// <summary>
/// Learns what you like from your ratings, instantly: a logistic model per role over <see cref="PartFeatures"/>.
/// Comparisons (A better than B) and single likes/dislikes both update it.
/// </summary>
public sealed class TasteModel
{
    public Dictionary<PartRole, double[]> Weights { get; set; } = new();
    public Dictionary<PartRole, int> Ratings { get; set; } = new();
    /// <summary>How often it predicted your choice right (recent comparisons), for the trainer panel.</summary>
    public Dictionary<PartRole, double> Accuracy { get; set; } = new();

    private const double LearningRate = 0.15, L2 = 0.002;

    private double[] W(PartRole role)
    {
        if (!Weights.TryGetValue(role, out var w) || w.Length != PartFeatures.Names.Length)
            Weights[role] = w = new double[PartFeatures.Names.Length];
        return w;
    }

    public double Score(PartRole role, double[] features) => Dot(W(role), features);

    public int RatingCount(PartRole role) => Ratings.GetValueOrDefault(role);

    /// <summary>You preferred <paramref name="better"/> over <paramref name="worse"/>.</summary>
    public void Prefer(PartRole role, double[] better, double[] worse)
    {
        var w = W(role);
        var diff = better.Zip(worse, (a, b) => a - b).ToArray();
        double p = Sigmoid(Dot(w, diff));
        Track(role, p > 0.5);
        Step(w, diff, 1 - p);
        Ratings[role] = RatingCount(role) + 1;
    }

    /// <summary>A single like (+1) or dislike (-1).</summary>
    public void Rate(PartRole role, double[] features, bool liked)
    {
        var w = W(role);
        double p = Sigmoid(Dot(w, features));
        Track(role, (p > 0.5) == liked);
        Step(w, features, (liked ? 1 : 0) - p);
        Ratings[role] = RatingCount(role) + 1;
    }

    private void Track(PartRole role, bool right)
    {
        double previous = Accuracy.GetValueOrDefault(role, 0.5);
        Accuracy[role] = previous * 0.9 + (right ? 0.1 : 0);
    }

    private static void Step(double[] w, double[] x, double error)
    {
        for (int i = 0; i < w.Length; i++) w[i] += LearningRate * (error * x[i] - L2 * w[i]);
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += a[i] * b[i];
        return s;
    }

    private static double Sigmoid(double x) => 1 / (1 + Math.Exp(-x));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static TasteModel Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<TasteModel>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>The features that matter most for a role, for showing in the trainer ("you like: syncopation …").</summary>
    public IEnumerable<(string Feature, double Weight)> Strongest(PartRole role, int count = 4) =>
        W(role).Select((w, i) => (PartFeatures.Names[i], w)).Where(x => x.Item1 != "bias")
            .OrderByDescending(x => Math.Abs(x.w)).Take(count);
}

/// <summary>One rating, as stored in ratings.jsonl (the format dpo.py reads).</summary>
public sealed class RatingRecord
{
    [JsonPropertyName("time")] public DateTime Time { get; set; } = DateTime.Now;
    [JsonPropertyName("kind")] public string Kind { get; set; } = "pair"; // pair | like | dislike | insert
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("prompt")] public List<int> Prompt { get; set; } = new();
    [JsonPropertyName("chosen")] public List<int>? Chosen { get; set; }
    [JsonPropertyName("rejected")] public List<int>? Rejected { get; set; }
    [JsonPropertyName("disliked")] public List<int>? Disliked { get; set; }
    [JsonPropertyName("reasons")] public List<string> Reasons { get; set; } = new();
    [JsonPropertyName("sound")] public string? Sound { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
}

/// <summary>Appends ratings to ratings.jsonl.</summary>
public static class RatingStore
{
    private static readonly object Lock = new();

    public static void Add(RatingRecord record, string? path = null)
    {
        path ??= MlPaths.Ratings;
        lock (Lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n");
        }
    }

    public static List<RatingRecord> All(string? path = null)
    {
        path ??= MlPaths.Ratings;
        var list = new List<RatingRecord>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Trim().Length == 0) continue;
            try { if (JsonSerializer.Deserialize<RatingRecord>(line) is { } r) list.Add(r); }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>Removes the last rating (undo).</summary>
    public static void RemoveLast(string? path = null)
    {
        path ??= MlPaths.Ratings;
        lock (Lock)
        {
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) return;
            lines.RemoveAt(lines.Count - 1);
            File.WriteAllLines(path, lines);
        }
    }
}
