using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DonutHypixelPlayerComparer.Services;

internal sealed class NbtReader
{
    /// <summary>
    /// Compounds and lists recurse, and a StackOverflowException cannot be caught, so malformed or
    /// hostile item bytes have to be rejected before they can take the process down with them.
    /// </summary>
    private const int MaxDepth = 96;

    /// <summary>Collection lengths are attacker-controlled 4-byte fields, so pre-sizing is capped.</summary>
    private const int MaxPreallocated = 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[8];
    private int _depth;

    private NbtReader(Stream stream) => _stream = stream;

    public static Dictionary<string, object?> ReadRoot(byte[] bytes)
    {
        using var memory = new MemoryStream(bytes, false);
        Stream source = memory;
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            source = new GZipStream(memory, CompressionMode.Decompress, false);
        using (source)
        {
            var reader = new NbtReader(source);
            var type = (TagType)reader.ReadByte();
            if (type != TagType.Compound) throw new InvalidDataException("NBT root is not a compound.");
            reader.ReadString();
            return (Dictionary<string, object?>)reader.ReadPayload(type)!;
        }
    }

    private object? ReadPayload(TagType type) => type switch
    {
        TagType.End => null,
        TagType.Byte => unchecked((sbyte)ReadByte()),
        TagType.Short => ReadInt16(),
        TagType.Int => ReadInt32(),
        TagType.Long => ReadInt64(),
        TagType.Float => BitConverter.Int32BitsToSingle(ReadInt32()),
        TagType.Double => BitConverter.Int64BitsToDouble(ReadInt64()),
        TagType.ByteArray => ReadByteArray(),
        TagType.String => ReadString(),
        TagType.List => ReadList(),
        TagType.Compound => ReadCompound(),
        TagType.IntArray => ReadIntArray(),
        TagType.LongArray => ReadLongArray(),
        _ => throw new InvalidDataException($"Unknown NBT tag type {(byte)type}.")
    };

    private Dictionary<string, object?> ReadCompound()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        using var depth = Descend();
        while (true)
        {
            var type = (TagType)ReadByte();
            if (type == TagType.End) return result;
            result[ReadString()] = ReadPayload(type);
        }
    }

    private List<object?> ReadList()
    {
        var type = (TagType)ReadByte();
        var length = ReadLength();
        // A TAG_End element reads no bytes, so a non-empty list of them would spin without ever
        // hitting the end of the stream.
        if (type == TagType.End && length > 0) throw new InvalidDataException("NBT list of TAG_End is not valid.");
        using var depth = Descend();
        var result = new List<object?>(Math.Min(length, MaxPreallocated));
        for (var index = 0; index < length; index++) result.Add(ReadPayload(type));
        return result;
    }

    private DepthScope Descend()
    {
        if (++_depth > MaxDepth) throw new InvalidDataException("NBT nesting is too deep.");
        return new DepthScope(this);
    }

    private readonly struct DepthScope(NbtReader reader) : IDisposable
    {
        public void Dispose() => reader._depth--;
    }

    // Declared lengths are never trusted for the initial allocation: the arrays grow as bytes are
    // actually read, so a truncated payload fails on the stream rather than on a huge allocation.
    private byte[] ReadByteArray()
    {
        var length = ReadLength();
        var value = new byte[Math.Min(length, MaxPreallocated)];
        var read = 0;
        while (read < length)
        {
            if (read == value.Length) Array.Resize(ref value, (int)Math.Min(length, value.Length * 2L));
            var count = _stream.Read(value, read, value.Length - read);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
        if (value.Length != length) Array.Resize(ref value, length);
        return value;
    }

    private int[] ReadIntArray()
    {
        var length = ReadLength();
        var value = new List<int>(Math.Min(length, MaxPreallocated));
        for (var index = 0; index < length; index++) value.Add(ReadInt32());
        return value.ToArray();
    }

    private long[] ReadLongArray()
    {
        var length = ReadLength();
        var value = new List<long>(Math.Min(length, MaxPreallocated));
        for (var index = 0; index < length; index++) value.Add(ReadInt64());
        return value.ToArray();
    }

    private int ReadLength()
    {
        var length = ReadInt32();
        if (length is < 0 or > 10_000_000) throw new InvalidDataException("Invalid NBT collection length.");
        return length;
    }

    private string ReadString()
    {
        ReadExactly(_buffer.AsSpan(0, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(_buffer);
        if (length == 0) return string.Empty;
        var bytes = new byte[length];
        ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private short ReadInt16()
    {
        ReadExactly(_buffer.AsSpan(0, 2));
        return BinaryPrimitives.ReadInt16BigEndian(_buffer);
    }

    private int ReadInt32()
    {
        ReadExactly(_buffer.AsSpan(0, 4));
        return BinaryPrimitives.ReadInt32BigEndian(_buffer);
    }

    private long ReadInt64()
    {
        ReadExactly(_buffer);
        return BinaryPrimitives.ReadInt64BigEndian(_buffer);
    }

    private int ReadByte()
    {
        var value = _stream.ReadByte();
        if (value < 0) throw new EndOfStreamException();
        return value;
    }

    private void ReadExactly(Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = _stream.Read(buffer[read..]);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }

    private enum TagType : byte
    {
        End, Byte, Short, Int, Long, Float, Double, ByteArray, String, List, Compound, IntArray, LongArray
    }
}
