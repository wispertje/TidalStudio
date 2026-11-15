using TidalStudio.Core.Audio;

namespace TidalStudio.Tests;

public class AudioTests
{
    [Fact]
    public void Full_scale_sine_peaks_near_0_dB_at_its_frequency()
    {
        const int n = 4096, rate = 48000;
        double freq = Fft.BinFrequency(256, n, rate); // exactly on a bin
        var samples = new float[n];
        for (int i = 0; i < n; i++) samples[i] = (float)Math.Sin(2 * Math.PI * freq * i / rate);

        var db = new float[n / 2];
        Fft.MagnitudeDb(samples, db);
        int peak = Array.IndexOf(db, db.Max());
        Assert.Equal(256, peak);
        Assert.InRange(db[peak], -1.0, 0.5);
        Assert.True(db[1000] < -60);
    }

    [Fact]
    public void Ring_buffer_returns_latest_samples_oldest_first()
    {
        var rb = new AudioRingBuffer(8);
        rb.Write(new float[] { 1, 2, 3 });
        var dst = new float[5];
        rb.ReadLatest(dst);
        Assert.Equal(new float[] { 0, 0, 1, 2, 3 }, dst);

        rb.Write(new float[] { 4, 5, 6, 7, 8, 9, 10 });
        rb.ReadLatest(dst);
        Assert.Equal(new float[] { 6, 7, 8, 9, 10 }, dst);
    }
}
