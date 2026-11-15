using System.Buffers.Binary;
using System.Text;

namespace TidalStudio.Core.Osc;

/// <summary>Minimal OSC 1.0 encoder/decoder (enough for Tidal / SuperDirt traffic).</summary>
public static class OscCodec
{
    public static OscPacket Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == (byte)'#' && ReadString(data, 0, out _) == "#bundle")
            return DecodeBundle(data);
        return DecodeMessage(data);
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out OscPacket? packet)
    {
        try
        {
            packet = Decode(data);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            packet = null;
            return false;
        }
    }

    private static OscBundle DecodeBundle(ReadOnlySpan<byte> data)
    {
        // "#bundle\0" (8 bytes) + 8-byte time tag + elements (int32 size + content)
        if (data.Length < 16) throw new FormatException("Bundle too short");
        ulong timeTag = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(8, 8));
        var packets = new List<OscPacket>();
        int pos = 16;
        while (pos + 4 <= data.Length)
        {
            int size = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
            pos += 4;
            if (size < 0 || pos + size > data.Length) throw new FormatException("Bad bundle element size");
            packets.Add(Decode(data.Slice(pos, size)));
            pos += size;
        }
        return new OscBundle(timeTag, packets);
    }

    private static OscMessage DecodeMessage(ReadOnlySpan<byte> data)
    {
        string address = ReadString(data, 0, out int pos);
        if (!address.StartsWith('/')) throw new FormatException("OSC address must start with '/'");
        if (pos >= data.Length) return new OscMessage(address, Array.Empty<object?>());

        string tags = ReadString(data, pos, out pos);
        if (!tags.StartsWith(',')) throw new FormatException("Missing type tag string");

        var args = new List<object?>(tags.Length - 1);
        for (int i = 1; i < tags.Length; i++)
        {
            switch (tags[i])
            {
                case 'i': args.Add(BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4))); pos += 4; break;
                case 'f': args.Add(BinaryPrimitives.ReadSingleBigEndian(data.Slice(pos, 4))); pos += 4; break;
                case 'h': args.Add(BinaryPrimitives.ReadInt64BigEndian(data.Slice(pos, 8))); pos += 8; break;
                case 'd': args.Add(BinaryPrimitives.ReadDoubleBigEndian(data.Slice(pos, 8))); pos += 8; break;
                case 't': args.Add(BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos, 8))); pos += 8; break;
                case 's':
                case 'S': args.Add(ReadString(data, pos, out pos)); break;
                case 'c': args.Add((char)BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4))); pos += 4; break;
                case 'b':
                {
                    int len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
                    pos += 4;
                    args.Add(data.Slice(pos, len).ToArray());
                    pos += Pad4(len);
                    break;
                }
                case 'T': args.Add(true); break;
                case 'F': args.Add(false); break;
                case 'N': args.Add(null); break;
                case 'I': args.Add(double.PositiveInfinity); break;
                default: throw new FormatException($"Unsupported OSC type tag '{tags[i]}'");
            }
        }
        return new OscMessage(address, args);
    }

    private static string ReadString(ReadOnlySpan<byte> data, int start, out int next)
    {
        int end = data.Slice(start).IndexOf((byte)0);
        if (end < 0) throw new FormatException("Unterminated OSC string");
        string s = Encoding.UTF8.GetString(data.Slice(start, end));
        next = start + Pad4(end + 1);
        return s;
    }

    private static int Pad4(int n) => (n + 3) & ~3;

    // ---------------- Encoding (used by tests and for sending to SuperDirt/ghci tools) ----------------

    public static byte[] Encode(OscPacket packet)
    {
        using var ms = new MemoryStream();
        Write(ms, packet);
        return ms.ToArray();
    }

    private static void Write(Stream s, OscPacket packet)
    {
        switch (packet)
        {
            case OscMessage m: WriteMessage(s, m); break;
            case OscBundle b:
                WriteString(s, "#bundle");
                WriteBE(s, b.TimeTag);
                foreach (var p in b.Packets)
                {
                    byte[] inner = Encode(p);
                    WriteBE(s, inner.Length);
                    s.Write(inner);
                }
                break;
        }
    }

    private static void WriteMessage(Stream s, OscMessage m)
    {
        WriteString(s, m.Address);
        var tags = new StringBuilder(",");
        foreach (var a in m.Arguments)
        {
            tags.Append(a switch
            {
                int => 'i',
                float => 'f',
                double => 'd',
                long => 'h',
                string => 's',
                byte[] => 'b',
                true => 'T',
                false => 'F',
                null => 'N',
                _ => throw new ArgumentException($"Unsupported OSC argument type {a.GetType()}")
            });
        }
        WriteString(s, tags.ToString());
        foreach (var a in m.Arguments)
        {
            switch (a)
            {
                case int i: WriteBE(s, i); break;
                case float f: { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, f); s.Write(b); break; }
                case double d: { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, d); s.Write(b); break; }
                case long l: { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, l); s.Write(b); break; }
                case string str: WriteString(s, str); break;
                case byte[] blob:
                    WriteBE(s, blob.Length);
                    s.Write(blob);
                    for (int i = blob.Length; i < Pad4(blob.Length); i++) s.WriteByte(0);
                    break;
            }
        }
    }

    private static void WriteString(Stream s, string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        s.Write(bytes);
        int total = Pad4(bytes.Length + 1);
        for (int i = bytes.Length; i < total; i++) s.WriteByte(0);
    }

    private static void WriteBE(Stream s, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }
    private static void WriteBE(Stream s, ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); s.Write(b); }
}
