using TidalStudio.Core.Tidal;

namespace TidalStudio.Core.Ml;

/// <summary>
/// Command-line tools (TidalStudio.exe --ml …), used by the trainer and for checks. Output goes to a log file.
/// <list type="bullet">
/// <item><c>--ml roundtrip &lt;samples&gt; &lt;log&gt;</c>: writes code for generated parts, plays it in a silent Tidal
/// and checks the notes come back the same.</item>
/// <item><c>--ml dataset &lt;samples&gt; &lt;out folder&gt; &lt;log&gt; &lt;input&gt;…</c>: turns MIDI files/folders and .tidal
/// files/folders into training data.</item>
/// </list>
/// </summary>
public static class MlCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        int i = Array.IndexOf(args, "--ml");
        var a = args.Skip(i + 1).ToArray();
        if (a.Length == 0) return 2;
        switch (a[0])
        {
            case "roundtrip" when a.Length >= 3:
                return await WithLog(a[2], log => RoundTripAsync(a[1], log));
            case "generate" when a.Length >= 4:
                return await WithLog(a[3], log => GenerateCheckAsync(a[1], a[2], log));
            case "dataset" when a.Length >= 5:
                return await WithLog(a[3], log => BuildDatasetAsync(a[1], a[2], a[4..], log));
            default:
                return 2;
        }
    }

    private static async Task<int> WithLog(string path, Func<Action<string>, Task<int>> run)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var writer = new StreamWriter(path) { AutoFlush = true };
        void Log(string s)
        {
            lock (writer) writer.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
        }
        try { return await run(Log); }
        catch (Exception e)
        {
            Log("FAILED: " + e);
            return 1;
        }
    }

    private static async Task<TidalRunner> StartRunnerAsync(Action<string> log)
    {
        string ghci = ToolLocator.FindGhci() ?? throw new InvalidOperationException("ghci not found");
        var runner = new TidalRunner();
        runner.Output += o => { if (o.Kind == OutputKind.Error) log("[ghci] " + o.Text); };
        await runner.StartAsync(ghci);
        log("silent Tidal ready");
        return runner;
    }

    public static async Task<int> RoundTripAsync(string samplesRoot, Action<string> log)
    {
        var catalog = SampleCatalog.Scan(samplesRoot);
        log($"catalog: {catalog.Sounds.Count} sounds");
        await using var runner = await StartRunnerAsync(log);
        var key = new MKey(0, true);
        var chords = new[] { new MChord(0, ChordQuality.Minor), new MChord(8, ChordQuality.Major), new MChord(3, ChordQuality.Major), new MChord(10, ChordQuality.Major) };
        int failures = 0, checks = 0;
        var kit = catalog.Kits.Keys.OrderBy(k => k).FirstOrDefault(k => k == "gated") ?? catalog.Kits.Keys.First();
        foreach (var role in Enum.GetValues<PartRole>())
            for (int seed = 0; seed < 6; seed++)
            {
                var r = new PartRequest { Role = role, Key = key, Bars = seed % 2 == 0 ? 4 : 2, Density = seed % 5, Chords = chords };
                var part = RuleGenerator.Generate(r, new Random(seed));
                SoundInfo? sound = role == PartRole.Drums ? null : catalog.ForRole(role).OrderBy(s => s.Name).Skip(seed).FirstOrDefault() ?? catalog.ForRole(role).First();
                string code = role == PartRole.Drums ? TidalWriter.DrumPart(part, catalog, kit, 1) : TidalWriter.Pitched(part, sound!, 1);
                await runner.HushAsync();
                var result = await runner.EvalAsync(code);
                checks++;
                if (!result.Success)
                {
                    failures++;
                    log($"FAIL compile {role} seed {seed}: {code}\n{result.ErrorText}");
                    continue;
                }
                var events = await runner.DumpAsync(0, part.Bars);
                var ctx = TidalContext.Build(events, catalog, 0, part.Bars);
                var got = ctx.Parts.SelectMany(p => p.Part.Notes).Select(n => (Step: (int)Math.Round(n.Start * 16), n.Pitch)).ToHashSet();
                var want = part.Notes.Select(n => (Step: (int)Math.Round(n.Start * 16), Pitch: role == PartRole.Drums ? Substitute(catalog, kit, n.Pitch) : n.Pitch)).ToHashSet();
                if (role is PartRole.Bass or PartRole.Melody)
                    want = part.Notes.GroupBy(n => (int)Math.Round(n.Start * 16)).Select(g => (Step: g.Key, Pitch: g.First().Pitch)).ToHashSet();
                bool same = got.SetEquals(want);
                if (!same)
                {
                    failures++;
                    log($"FAIL notes {role} seed {seed}: missing {string.Join(",", want.Except(got).Take(8))} extra {string.Join(",", got.Except(want).Take(8))}\n{code}");
                }
                else log($"ok {role} seed {seed} ({want.Count} notes): {code.Replace("\n", " ")}");
            }
        log($"round trip: {checks - failures}/{checks} ok");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Loads a model, makes suggestions of every kind over a C minor backing, times them and checks the code in a
    /// silent Tidal.
    /// </summary>
    public static async Task<int> GenerateCheckAsync(string onnx, string samplesRoot, Action<string> log)
    {
        var catalog = SampleCatalog.Scan(samplesRoot);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var model = MuseModel.Load(onnx);
        log($"model loaded in {watch.ElapsedMilliseconds} ms on {(model.OnGpu ? "GPU (DirectML)" : "CPU")}");
        var suggester = new Suggester(catalog, new TasteModel(), model);
        var chords = new List<MChord> { new(0, ChordQuality.Minor), new(8, ChordQuality.Major), new(3, ChordQuality.Major), new(10, ChordQuality.Major) };
        var drums = RuleGenerator.Generate(new PartRequest { Role = PartRole.Drums, Bars = 4, Density = 2 }, new Random(1));
        var context = new TidalContext { From = 0, Bars = 4, Key = MKey.CMinor, Chords = chords, Parts = { ("1", drums) } };
        await using var runner = await StartRunnerAsync(log);
        int failures = 0;
        foreach (var kind in Enum.GetValues<SuggestKind>())
            foreach (int bars in new[] { 2, 4 })
            {
                watch.Restart();
                var items = suggester.Suggest(context, new SuggestOptions { Kind = kind, Bars = bars, Count = 4, Surprise = 0.4 }, new Random(bars));
                long ms = watch.ElapsedMilliseconds;
                log($"{kind} {bars} bars: {items.Count} suggestions in {ms} ms ({string.Join(", ", items.Select(i => i.Source).Distinct())})");
                foreach (var s in items.Take(2))
                {
                    await runner.HushAsync();
                    var r = await runner.EvalAsync(s.Code);
                    if (!r.Success) { failures++; log("FAIL " + r.ErrorText + "\n" + s.Code); }
                    else log("  " + s.Code.Replace("\n", "\n  "));
                }
            }
        return failures == 0 ? 0 : 1;
    }

    private static int Substitute(SampleCatalog catalog, string kit, int drum)
    {
        var s = catalog.DrumIn(kit, Drums.Names[drum]);
        return s?.Drum is { } d ? Drums.Index(d) : drum;
    }

    public static async Task<int> BuildDatasetAsync(string samplesRoot, string outDir, string[] inputs, Action<string> log)
    {
        var catalog = SampleCatalog.Scan(samplesRoot);
        var midi = new List<string>();
        var tidal = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                midi.AddRange(Directory.EnumerateFiles(input, "*.mid", SearchOption.AllDirectories));
                midi.AddRange(Directory.EnumerateFiles(input, "*.midi", SearchOption.AllDirectories));
                tidal.AddRange(Directory.EnumerateFiles(input, "*.tidal", SearchOption.AllDirectories));
            }
            else if (File.Exists(input))
                (input.EndsWith(".tidal", StringComparison.OrdinalIgnoreCase) ? tidal : midi).Add(input);
        }
        log($"inputs: {midi.Count} MIDI files, {tidal.Count} .tidal files");
        var random = new Random(1234);
        long songs = 0, skipped = 0;
        // MIDI → "pretrain", your .tidal files → "own" (the fine-tune data).
        if (midi.Count > 0)
        {
            var seen = new HashSet<ulong>();
            using var writer = new Dataset.Writer(Path.Combine(outDir, "pretrain"));
            var parsed = midi.AsParallel().AsOrdered().WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount - 1))
                .Select(f => MidiReader.Read(f));
            foreach (var song in parsed)
            {
                if (song is null) { skipped++; continue; }
                foreach (var seq in Dataset.Sequences(song, random))
                    if (seen.Add(Dataset.Hash(seq))) writer.Add(seq);
                if (++songs % 500 == 0) log($"{songs} MIDI songs, {writer.Count} sequences");
            }
            log($"pretrain: {songs} songs ({skipped} files skipped), {writer.Count} sequences");
        }
        if (tidal.Count > 0)
        {
            var seen = new HashSet<ulong>();
            using var writer = new Dataset.Writer(Path.Combine(outDir, "own"));
            await using var runner = await StartRunnerAsync(log);
            int own = 0;
            foreach (var f in tidal)
                foreach (var song in await runner.ImportFileAsync(f, catalog, log: log))
                {
                    foreach (var seq in Dataset.Sequences(song, random))
                        if (seen.Add(Dataset.Hash(seq))) writer.Add(seq);
                    own++;
                }
            log($"own: {own} snapshots from {tidal.Count} files, {writer.Count} sequences");
        }
        log($"done: {outDir}");
        return 0;
    }
}
