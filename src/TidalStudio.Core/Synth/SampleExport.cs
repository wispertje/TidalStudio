using System.Text;
using System.Text.RegularExpressions;

namespace TidalStudio.Core.Synth;

/// <summary>Writes rendered notes as WAV files into a SuperDirt sample folder (one folder = one sound name).</summary>
public static partial class SampleExport
{
    [GeneratedRegex("^[a-z][a-z0-9_-]*$")]
    private static partial Regex ValidName();

    /// <summary>
    /// A sound name works in Tidal when it is lowercase letters, digits, '-' or '_', starting with a letter
    /// (so it can't be read as a number, and has no spaces or characters the mini-notation uses).
    /// </summary>
    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    /// <summary>
    /// File name for the i-th sample. SuperDirt sorts files by name, so the index prefix keeps the order of the
    /// notes: s "name" # n 0 is the first note, n 1 the second, …
    /// </summary>
    public static string FileName(string sound, int index, int midi) => $"{sound}_{index:00}_{SynthPatch.NoteName(midi)}.wav";

    /// <summary>
    /// Writes every note to <paramref name="soundFolder"/>, replacing the .wav files already there. Returns the paths.
    /// </summary>
    public static List<string> Write(string soundFolder, string sound, IReadOnlyList<RenderedNote> notes)
    {
        Directory.CreateDirectory(soundFolder);
        foreach (var old in Directory.EnumerateFiles(soundFolder, "*.wav")) File.Delete(old);
        var paths = new List<string>();
        for (int i = 0; i < notes.Count; i++)
        {
            string path = Path.Combine(soundFolder, FileName(sound, i, notes[i].Midi));
            WriteWav(path, notes[i]);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>24-bit stereo PCM WAV.</summary>
    public static void WriteWav(string path, RenderedNote note)
    {
        using var stream = File.Create(path);
        WriteWav(stream, note);
    }

    public static void WriteWav(Stream stream, RenderedNote note)
    {
        const int channels = 2, bytesPerSample = 3;
        int frames = note.Left.Length;
        int dataBytes = frames * channels * bytesPerSample;
        using var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)channels);
        w.Write(note.SampleRate);
        w.Write(note.SampleRate * channels * bytesPerSample);
        w.Write((short)(channels * bytesPerSample));
        w.Write((short)(bytesPerSample * 8));
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        var buffer = new byte[dataBytes];
        int o = 0;
        for (int i = 0; i < frames; i++)
        {
            Put(note.Left[i]);
            Put(note.Right[i]);
        }
        w.Write(buffer);

        void Put(float sample)
        {
            int v = (int)Math.Round(Math.Clamp(sample, -1f, 1f) * 8388607);
            buffer[o++] = (byte)v;
            buffer[o++] = (byte)(v >> 8);
            buffer[o++] = (byte)(v >> 16);
        }
    }
}
