using System.Numerics;

namespace TidalStudio.Core.Audio;

/// <summary>In-place radix-2 FFT plus helpers to turn audio into a magnitude spectrum in dB.</summary>
public static class Fft
{
    public static void Transform(Span<Complex> data)
    {
        int n = data.Length;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two", nameof(data));

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = data[i + k];
                    var v = data[i + k + len / 2] * w;
                    data[i + k] = u + v;
                    data[i + k + len / 2] = u - v;
                    w *= wLen;
                }
            }
        }
    }

    /// <summary>
    /// Hann-windowed magnitude spectrum in dBFS (0 dB = full-scale sine). Output length is samples.Length / 2.
    /// </summary>
    public static void MagnitudeDb(ReadOnlySpan<float> samples, Span<float> dbOut, float floorDb = -120f)
    {
        int n = samples.Length;
        var buffer = new Complex[n];
        double windowSum = 0;
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            windowSum += w;
            buffer[i] = new Complex(samples[i] * w, 0);
        }
        Transform(buffer);

        int bins = Math.Min(dbOut.Length, n / 2);
        double scale = 2.0 / windowSum; // amplitude of a full-scale sine -> 1
        for (int k = 0; k < bins; k++)
        {
            double mag = buffer[k].Magnitude * scale;
            double db = mag > 0 ? 20 * Math.Log10(mag) : floorDb;
            dbOut[k] = (float)Math.Max(db, floorDb);
        }
    }

    public static double BinFrequency(int bin, int fftSize, int sampleRate) => (double)bin * sampleRate / fftSize;
}
