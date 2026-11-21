using NAudio.CoreAudioApi;
using NAudio.Wave;
using TidalStudio.Core.Synth;

#pragma warning disable CS0618 // WasapiOut: see PreviewPlayer
#pragma warning disable CA1416 // only used on Windows (see Play)

namespace TidalStudio.App.Synth;

/// <summary>A window with a looping sequence that follows the shared tempo (sequencers, Tidepool Acid).</summary>
public interface ITempoFollower
{
    /// <summary>Shown when this window changes the shared tempo ("Tidepool Drums").</summary>
    string TempoName { get; }

    /// <summary>Another window changed the shared tempo: use it (and re-render when playing).</summary>
    void FollowTempo(double bpm, string changedBy);
}

/// <summary>
/// The one clock every looping sequencer plays on: their loops are mixed into one output by a <see cref="LoopMixer"/>,
/// so they stay in time with each other, and they share one tempo (like Tidal's single cps): changing it in one window
/// changes it in all open ones.
/// </summary>
public sealed class SequencerTransport
{
    public static SequencerTransport Shared { get; } = new();

    private const int SampleRate = 48000;

    private readonly LoopMixer _mixer = new(SampleRate);
    private readonly List<ITempoFollower> _followers = new();
    private WasapiOut? _output;

    /// <summary>The shared tempo.</summary>
    public double Bpm => _mixer.Bpm;

    /// <summary>
    /// A window opens: returns the tempo it should use — the shared one when other windows are open, else its own
    /// (which becomes the shared one).
    /// </summary>
    public double Join(ITempoFollower follower, double ownBpm)
    {
        if (_followers.Count == 0) _mixer.Bpm = ownBpm;
        if (!_followers.Contains(follower)) _followers.Add(follower);
        return _mixer.Bpm;
    }

    public void Leave(ITempoFollower follower)
    {
        Stop(follower);
        _followers.Remove(follower);
    }

    /// <summary>The other open windows, for "in time with …".</summary>
    public IEnumerable<ITempoFollower> Others(ITempoFollower me) => _followers.Where(f => f != me);

    /// <summary>Sets the shared tempo and tells every other open window.</summary>
    public void SetBpm(double bpm, ITempoFollower source)
    {
        if (Math.Abs(bpm - _mixer.Bpm) < 1e-9) return;
        _mixer.Bpm = bpm;
        foreach (var f in _followers.Where(f => f != source).ToList()) f.FollowTempo(_mixer.Bpm, source.TempoName);
    }

    /// <summary>
    /// Plays (or replaces) the loop of <paramref name="owner"/>, <paramref name="steps"/> sixteenths long, in time with
    /// the loops already playing. Returns an error message when the sound device can't be used.
    /// </summary>
    public string? Play(object owner, RenderedNote loop, int steps)
    {
        if (!OperatingSystem.IsWindows())
            return "Preview playback is only available on Windows; saved samples still work everywhere.";
        try
        {
            if (_output is null)
            {
                _output = new WasapiOut(AudioClientShareMode.Shared, 60);
                _output.Init(new Source(_mixer));
                _output.Play();
            }
            _mixer.Play(owner, loop, steps);
            return null;
        }
        catch (Exception e)
        {
            CloseOutput();
            return $"Could not play the preview: {e.Message}";
        }
    }

    public void Stop(object owner)
    {
        _mixer.Stop(owner);
        // Release the device once nothing plays any more (after the fade-out).
        if (_output is not null)
            Avalonia.Threading.DispatcherTimer.RunOnce(() => { if (!_mixer.IsRunning) CloseOutput(); }, TimeSpan.FromMilliseconds(300));
    }

    public bool IsPlaying(object owner) => _mixer.IsPlaying(owner);

    /// <summary>Where the owner's loop is being heard now, in steps (0..its length); null when it doesn't play.</summary>
    public double? StepIn(object owner) => _mixer.StepIn(owner, PreviewPlayer.LatencySeconds);

    private void CloseOutput()
    {
        try { _output?.Stop(); } catch (Exception) { }
        _output?.Dispose();
        _output = null;
    }

    private sealed class Source(LoopMixer mixer) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(mixer.SampleRate, 2);

        public int Read(Span<byte> buffer)
        {
            int usable = buffer.Length - buffer.Length % 8;
            mixer.Mix(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer[..usable]));
            buffer[usable..].Clear();
            return buffer.Length;
        }
    }
}
