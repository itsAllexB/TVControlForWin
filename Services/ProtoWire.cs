// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace TVControlForWin.Services;

// The Android TV Remote protocol is protobuf over TLS. It needs a dozen tiny messages, so
// rather than pull in a protobuf toolchain these two classes speak the wire format directly:
// a varint tag (field number << 3 | wire type) followed by a varint or a length-prefixed blob.

/// <summary>Builds one protobuf message field by field.</summary>
internal sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    public ProtoWriter Varint(int field, ulong value)
    {
        WriteRaw((ulong)field << 3);
        WriteRaw(value);
        return this;
    }

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        WriteRaw((ulong)field << 3 | 2);
        WriteRaw((ulong)value.Length);
        _ms.Write(value);
        return this;
    }

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoWriter Message(int field, ProtoWriter inner) => Bytes(field, inner.ToArray());

    public byte[] ToArray() => _ms.ToArray();

    private void WriteRaw(ulong value)
    {
        while (value >= 0x80)
        {
            _ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        _ms.WriteByte((byte)value);
    }
}

/// <summary>A parsed protobuf message: just enough to look fields up by number.</summary>
internal sealed class ProtoMessage
{
    private readonly List<(int Field, ulong Varint, byte[]? Bytes)> _fields = new();

    /// <summary>Parses a message. Throws <see cref="InvalidDataException"/> on malformed input.</summary>
    public static ProtoMessage Parse(ReadOnlySpan<byte> data)
    {
        var msg = new ProtoMessage();
        var pos = 0;
        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var field = (int)(tag >> 3);
            switch ((int)(tag & 7))
            {
                case 0:
                    msg._fields.Add((field, ReadVarint(data, ref pos), null));
                    break;
                case 1:
                    pos = Skip(data, pos, 8);
                    break;
                case 2:
                    var length = ReadVarint(data, ref pos);
                    if (length > (ulong)(data.Length - pos)) throw new InvalidDataException("Truncated protobuf field.");
                    msg._fields.Add((field, 0, data.Slice(pos, (int)length).ToArray()));
                    pos += (int)length;
                    break;
                case 5:
                    pos = Skip(data, pos, 4);
                    break;
                default:
                    throw new InvalidDataException("Unsupported protobuf wire type.");
            }
        }
        return msg;
    }

    public bool Has(int field) => _fields.Any(f => f.Field == field);

    /// <summary>The varint value of a field, or null when it's absent (proto3 omits zero/false).</summary>
    public ulong? Varint(int field)
    {
        foreach (var f in _fields)
            if (f.Field == field && f.Bytes is null) return f.Varint;
        return null;
    }

    /// <summary>The nested message in a field, or null when it's absent or not parseable as one.</summary>
    public ProtoMessage? Message(int field)
    {
        foreach (var f in _fields)
        {
            if (f.Field != field || f.Bytes is null) continue;
            try { return Parse(f.Bytes); }
            catch (InvalidDataException) { return null; }
        }
        return null;
    }

    public string? String(int field)
    {
        foreach (var f in _fields)
            if (f.Field == field && f.Bytes is not null) return Encoding.UTF8.GetString(f.Bytes);
        return null;
    }

    private static int Skip(ReadOnlySpan<byte> data, int pos, int count)
    {
        if (count > data.Length - pos) throw new InvalidDataException("Truncated protobuf field.");
        return pos + count;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= data.Length) throw new InvalidDataException("Truncated protobuf varint.");
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Protobuf varint too long.");
    }
}

/// <summary>Length-delimited framing: each message on the stream is preceded by its varint length.</summary>
internal static class ProtoFraming
{
    private const int MaxMessageBytes = 1 << 20;

    public static async Task WriteAsync(Stream stream, byte[] message, CancellationToken ct)
    {
        // Prefix and body go out in one write so a frame is never split across TLS records
        // by a concurrent writer.
        var frame = new byte[message.Length + 5];
        var n = 0;
        var length = (uint)message.Length;
        while (length >= 0x80)
        {
            frame[n++] = (byte)(length | 0x80);
            length >>= 7;
        }
        frame[n++] = (byte)length;
        message.CopyTo(frame, n);
        await stream.WriteAsync(frame.AsMemory(0, n + message.Length), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one frame. Throws <see cref="EndOfStreamException"/> when the peer closes.</summary>
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        var length = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (shift > 28) throw new InvalidDataException("Frame length varint too long.");
            await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            length |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
        }
        if (length < 0 || length > MaxMessageBytes) throw new InvalidDataException("Frame too large.");

        var message = new byte[length];
        await stream.ReadExactlyAsync(message, ct).ConfigureAwait(false);
        return message;
    }
}
