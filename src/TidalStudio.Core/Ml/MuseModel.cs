using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TidalStudio.Core.Ml;

/// <summary>Where Tidepool Muse keeps its things.</summary>
public static class MlPaths
{
    /// <summary>Big things (Python env, downloads, datasets, models): %LOCALAPPDATA%\TidalStudio\ml.</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("TIDALSTUDIO_ML") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidalStudio", "ml");

    public static string Venv => Path.Combine(Root, "venv");
    public static string Python => Path.Combine(Venv, "Scripts", "python.exe");
    public static string Downloads => Path.Combine(Root, "downloads");
    public static string Data => Path.Combine(Root, "data");
    public static string PretrainData => Path.Combine(Data, "pretrain");
    public static string OwnData => Path.Combine(Data, "own");
    public static string Models => Path.Combine(Root, "models");
    public static string Ratings => Path.Combine(Root, "ratings.jsonl");
    public static string Taste => Path.Combine(Root, "taste.json");
    public static string RoleOverrides => Path.Combine(Root, "sound-roles.json");
}

/// <summary>A saved model version: models\&lt;name&gt;\model.onnx (+ model.pt, info.json).</summary>
public sealed record ModelVersion(string Name, string Folder, DateTime Created, string Description)
{
    public string Onnx => Path.Combine(Folder, "model.onnx");
    public string Checkpoint => Path.Combine(Folder, "model.pt");

    public static List<ModelVersion> All()
    {
        var list = new List<ModelVersion>();
        if (!Directory.Exists(MlPaths.Models)) return list;
        foreach (var dir in Directory.EnumerateDirectories(MlPaths.Models))
        {
            if (!File.Exists(Path.Combine(dir, "model.onnx"))) continue;
            string description = "";
            try
            {
                string infoPath = Path.Combine(dir, "info.json");
                if (File.Exists(infoPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(infoPath));
                    var r = doc.RootElement;
                    var bits = new List<string>();
                    if (r.TryGetProperty("kind", out var k)) bits.Add(k.GetString() ?? "");
                    if (r.TryGetProperty("val_loss", out var v) && v.ValueKind == JsonValueKind.Number) bits.Add($"loss {v.GetDouble():0.000}");
                    if (r.TryGetProperty("pairs", out var p)) bits.Add($"{p.GetInt32()} pairs");
                    if (r.TryGetProperty("liked", out var l)) bits.Add($"{l.GetInt32()} liked");
                    if (r.TryGetProperty("parameters", out var n)) bits.Add($"{n.GetInt64() / 1e6:0.#}M parameters");
                    description = string.Join(", ", bits.Where(b => b.Length > 0));
                }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { }
            list.Add(new ModelVersion(Path.GetFileName(dir), dir, Directory.GetCreationTime(dir), description));
        }
        return list.OrderByDescending(v => v.Created).ToList();
    }
}

/// <summary>How surprising generation is.</summary>
public sealed record SamplingOptions
{
    /// <summary>0 = safe … 1 = wild. Sets temperature and nucleus size.</summary>
    public double Surprise { get; init; } = 0.4;
    public bool InKeyOnly { get; init; } = true;

    public double Temperature => 0.6 + Surprise * 0.8;
    public double TopP => 0.85 + Surprise * 0.13;
}

/// <summary>The trained model (ONNX), run on the GPU (DirectML) when possible, else the CPU.</summary>
public sealed class MuseModel : IDisposable
{
    private readonly InferenceSession _session;
    /// <summary>The cached step model (model-step.onnx), when there is one: much faster generation.</summary>
    private readonly InferenceSession? _step;
    private readonly int _layers, _heads, _headDim;
    public bool OnGpu { get; }
    public bool Cached => _step is not null;
    public string Path { get; }

    private MuseModel(InferenceSession session, InferenceSession? step, bool gpu, string path)
    {
        _session = session;
        _step = step;
        OnGpu = gpu;
        Path = path;
        if (step is not null && step.InputMetadata.TryGetValue("past", out var past))
        {
            var d = past.Dimensions; // [layers, 2, batch, heads, length, headDim]
            (_layers, _heads, _headDim) = (d[0], d[3], d[5]);
            if (_layers <= 0 || _heads <= 0 || _headDim <= 0) _step = null;
        }
        else _step = null;
    }

    /// <summary>
    /// Loads a model. The CPU is the default: with the cache a small model is fastest there (DirectML copies the cache
    /// between CPU and GPU every step). TIDALSTUDIO_ML_DEVICE=gpu tries DirectML.
    /// </summary>
    public static MuseModel Load(string onnxPath, bool preferGpu = false)
    {
        string stepPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(onnxPath)!, System.IO.Path.GetFileNameWithoutExtension(onnxPath) + "-step.onnx");
        bool hasStep = File.Exists(stepPath);
        if (preferGpu || Environment.GetEnvironmentVariable("TIDALSTUDIO_ML_DEVICE") == "gpu")
        {
            try
            {
                SessionOptions Gpu()
                {
                    var o = new SessionOptions();
                    o.AppendExecutionProvider_DML(0);
                    return o;
                }
                return new MuseModel(new InferenceSession(onnxPath, Gpu()), hasStep ? new InferenceSession(stepPath, Gpu()) : null, true, onnxPath);
            }
            catch (Exception) { /* no DirectML: fall back to the CPU */ }
        }
        SessionOptions Cpu() => new() { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new MuseModel(new InferenceSession(onnxPath, Cpu()), hasStep ? new InferenceSession(stepPath, Cpu()) : null, false, onnxPath);
    }

    /// <summary>Feeds tokens to the cached step model and keeps its cache between calls.</summary>
    private sealed class CachedStepper : IDisposable
    {
        private readonly MuseModel _m;
        private readonly int _batch;
        private IDisposableReadOnlyCollection<OrtValue>? _outputs;
        private readonly RunOptions _run = new();
        private static readonly string[] OutputNames = { "logits", "present" };

        public CachedStepper(MuseModel m, int batch)
        {
            _m = m;
            _batch = batch;
        }

        /// <summary>Adds tokens (each row the same length) and returns the logits after the last one.</summary>
        public float[][] Feed(IReadOnlyList<IReadOnlyList<int>> tokens)
        {
            int length = tokens[0].Count;
            var data = new long[_batch * length];
            for (int b = 0; b < _batch; b++)
                for (int t = 0; t < length; t++) data[b * length + t] = tokens[b][t];
            using var tokenValue = OrtValue.CreateTensorValueFromMemory(data, new long[] { _batch, length });
            OrtValue? emptyPast = null;
            try
            {
                OrtValue past;
                if (_outputs is null)
                {
                    emptyPast = OrtValue.CreateTensorValueFromMemory(Array.Empty<float>(), new long[] { _m._layers, 2, _batch, _m._heads, 0, _m._headDim });
                    past = emptyPast;
                }
                else past = _outputs[1];
                var inputs = new Dictionary<string, OrtValue> { ["tokens"] = tokenValue, ["past"] = past };
                var next = _m._step!.Run(_run, inputs, OutputNames);
                _outputs?.Dispose();
                _outputs = next;
            }
            finally { emptyPast?.Dispose(); }
            var logits = _outputs[0].GetTensorDataAsSpan<float>();
            var result = new float[_batch][];
            for (int b = 0; b < _batch; b++) result[b] = logits.Slice(b * Tokenizer.VocabSize, Tokenizer.VocabSize).ToArray();
            return result;
        }

        public void Dispose()
        {
            _outputs?.Dispose();
            _run.Dispose();
        }
    }

    /// <summary>Logits of the last position for a batch of equally long sequences.</summary>
    public float[][] LastLogits(IReadOnlyList<IReadOnlyList<int>> sequences)
    {
        int batch = sequences.Count, length = sequences[0].Count;
        var input = new DenseTensor<long>(new[] { batch, length });
        for (int b = 0; b < batch; b++)
            for (int t = 0; t < length; t++) input[b, t] = sequences[b][t];
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("tokens", input) });
        var output = results.First().AsTensor<float>();
        var logits = new float[batch][];
        for (int b = 0; b < batch; b++)
        {
            logits[b] = new float[Tokenizer.VocabSize];
            for (int v = 0; v < Tokenizer.VocabSize; v++) logits[b][v] = output[b, v];
        }
        return logits;
    }

    /// <summary>
    /// Generates <paramref name="count"/> parts for the request, all at once (one batch). Every token is drawn only
    /// from what the grammar allows, so the result is always a valid part of the right length.
    /// </summary>
    public List<List<int>> Generate(PartRequest request, int count, SamplingOptions options, Random random, CancellationToken token = default)
    {
        var header = Tokenizer.Header(request);
        int bars = Tokenizer.LengthChoices.Contains(request.Bars) ? request.Bars : 4;
        var grammar = new TokenGrammar(request.Role, bars, request.Key.Minor, options.InKeyOnly);
        var seqs = Enumerable.Range(0, count).Select(_ => header.ToList()).ToList();
        var bodies = Enumerable.Range(0, count).Select(_ => new List<int>()).ToList();
        var done = new bool[count];
        int budget = Tokenizer.MaxLength - 1;
        using var stepper = _step is not null ? new CachedStepper(this, count) : null;
        bool first = true;
        while (!done.All(d => d) && seqs[0].Count < budget)
        {
            token.ThrowIfCancellationRequested();
            float[][] logits;
            if (stepper is null) logits = LastLogits(seqs);
            else
            {
                // The whole header the first time, then only the newest token (the cache has the rest).
                logits = stepper.Feed(first ? seqs : seqs.Select(q => (IReadOnlyList<int>)new[] { q[^1] }).ToList());
                first = false;
            }
            for (int b = 0; b < count; b++)
            {
                int next;
                if (done[b]) next = Tokenizer.Pad;
                else
                {
                    var allowed = grammar.Allowed(bodies[b]);
                    // Running out of room: close the bars.
                    if (seqs[b].Count > budget - 4 * bars && Tokenizer.IsVel(bodies[b].LastOrDefault()))
                        for (int v = 0; v < allowed.Length; v++) allowed[v] &= v == Tokenizer.Bar || v == Tokenizer.Eos;
                    next = Sample(logits[b], allowed, options, random);
                    bodies[b].Add(next);
                    if (next == Tokenizer.Eos) done[b] = true;
                }
                seqs[b].Add(next);
            }
        }
        return bodies.Select(body => header.Concat(body.TakeWhile(t => t != Tokenizer.Pad)).ToList()).ToList();
    }

    public static int Sample(float[] logits, bool[] allowed, SamplingOptions options, Random random)
    {
        double temperature = Math.Max(0.05, options.Temperature);
        var candidates = new List<(int Token, double P)>();
        double max = double.NegativeInfinity;
        for (int i = 0; i < logits.Length; i++)
            if (allowed[i]) max = Math.Max(max, logits[i]);
        if (double.IsNegativeInfinity(max))
        {
            // Nothing allowed (shouldn't happen): end.
            return Tokenizer.Eos;
        }
        double sum = 0;
        for (int i = 0; i < logits.Length; i++)
            if (allowed[i])
            {
                double p = Math.Exp((logits[i] - max) / temperature);
                candidates.Add((i, p));
                sum += p;
            }
        candidates.Sort((a, b) => b.P.CompareTo(a.P));
        // Nucleus: the most likely tokens that together make up TopP.
        double cumulative = 0, limit = options.TopP * sum;
        int keep = 0;
        while (keep < candidates.Count && (cumulative < limit || keep == 0)) cumulative += candidates[keep++].P;
        double r = random.NextDouble() * cumulative;
        for (int i = 0; i < keep; i++)
        {
            r -= candidates[i].P;
            if (r <= 0) return candidates[i].Token;
        }
        return candidates[keep - 1].Token;
    }

    public void Dispose()
    {
        _session.Dispose();
        _step?.Dispose();
    }
}
