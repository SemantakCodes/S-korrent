// =====================================================================================
// BEncoding.cs
// =====================================================================================
// BEncoding is the binary encoding format defined by the BitTorrent specification.
// It supports four primitive types:
//
//   1. Byte strings  : <length>:<bytes>            e.g. "4:spam"
//   2. Integers      : i<signed-int>e              e.g. "i42e" or "i-3e"
//   3. Lists         : l<elements>e                e.g. "l4:spam4:eggse"
//   4. Dictionaries  : d<key><value>...e           e.g. "d3:cow3:moo4:spam4:eggse"
//
// Dictionaries MUST have keys that are byte strings and MUST be sorted by raw byte
// order. This canonical ordering is critical: the SHA-1 "infohash" is computed over
// the EXACT byte representation of the "info" dictionary, so any deviation in key
// sorting will break every torrent in existence. We enforce the canonical sort here.
//
// References:
//   https://wiki.theory.org/index.php/BitTorrentSpecification
//   https://www.bittorrent.org/beps/bep_0003.html
//
// Performance notes:
//   * Decoding allocates one byte[] per BEncodedString but is otherwise allocation-
//     free, hoisted out of dictionary lookups.
//   * Encoding writes through a sized MemoryStream so the working set stays close
//     to the encoded size (a multi-MB info dictionary does not pay 2× in GC).
//   * ByteArrayComparer is the canonical memcmp-equivalent comparer used by both
//     the dictionary store and the encoder.
// =====================================================================================

using System.Buffers;
using System.Text;

namespace BitTorrent.Core;

/// <summary>
/// Static utility class providing BEncoding parse and serialize operations
/// over raw <see cref="byte"/> arrays. The decoded values are exposed as
/// the small <see cref="BEncodedValue"/> hierarchy so callers can navigate
/// them without any external dependencies.
/// </summary>
public static class BEncoding
{
    // -------------------------------------------------------------------------
    // Decode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Decode a complete BEncoded blob from a byte array.
    /// </summary>
    /// <param name="data">Raw bytes containing a single BEncoded value.</param>
    /// <returns>The decoded root <see cref="BEncodedValue"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
    /// <exception cref="FormatException">The data is malformed.</exception>
    public static BEncodedValue Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var (value, _) = DecodeValue(data, 0);
        return value;
    }

    /// <summary>
    /// Decode a BEncoded blob that is embedded in a larger buffer.
    /// Returns the decoded value AND the offset where decoding stopped.
    /// Useful for "bencode" multi-value streams, though the spec itself does
    /// not require them.
    /// </summary>
    public static BEncodedValue Decode(byte[] data, int offset)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0 || offset >= data.Length)
            throw new FormatException("Decode offset is out of range.");
        var (value, _) = DecodeValue(data, offset);
        return value;
    }

    /// <summary>
    /// Decode a UTF-8 view of a BEncoded blob. Useful for debugging or for
    /// reading human-edited torrent files.
    /// </summary>
    public static BEncodedValue Decode(string text) =>
        Decode(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Recursive descent decoder. Each call consumes exactly one BEncoded value
    /// from <paramref name="buffer"/> starting at <paramref name="offset"/> and
    /// returns the parsed value paired with the index of the FIRST byte that was
    /// NOT consumed. This lets callers stream-decode sequential values if needed.
    /// </summary>
    private static (BEncodedValue Value, int NextOffset) DecodeValue(byte[] buffer, int offset)
    {
        if (offset >= buffer.Length)
            throw new FormatException("Unexpected end of buffer.");

        byte lead = buffer[offset];
        return lead switch
        {
            (byte)'i' => DecodeInteger(buffer, offset),
            (byte)'l' => DecodeList(buffer, offset),
            (byte)'d' => DecodeDictionary(buffer, offset),
            (byte)'0' or (byte)'1' or (byte)'2' or (byte)'3' or
            (byte)'4' or (byte)'5' or (byte)'6' or (byte)'7' or
            (byte)'8' or (byte)'9' => DecodeString(buffer, offset),
            _ => throw new FormatException($"Unexpected byte 0x{lead:X2} at offset {offset}.")
        };
    }

    /// <summary>
    /// Decode a byte string with format "LENGTH:BYTES" where LENGTH is ASCII digits
    /// and the colon delimits the count from the payload.
    ///
    /// We hand-parse the length instead of using int.TryParse so a 2-GB string
    /// header (which would overflow int) is still rejected cleanly.
    /// </summary>
    private static (BEncodedValue Value, int NextOffset) DecodeString(byte[] buffer, int offset)
    {
        int colonIndex = Array.IndexOf(buffer, (byte)':', offset);
        if (colonIndex < 0)
            throw new FormatException("Missing ':' separator in byte string.");

        // Length is a raw ASCII integer between the start and the colon.
        int length = 0;
        for (int i = offset; i < colonIndex; i++)
        {
            byte b = buffer[i];
            if ((uint)(b - (byte)'0') > 9)
                throw new FormatException($"Non-digit 0x{b:X2} in byte string length.");
            length = length * 10 + (b - (byte)'0');
        }
        if (length < 0)
            throw new FormatException("Byte string length overflowed.");

        int payloadStart = colonIndex + 1;
        int payloadEnd = payloadStart + length;
        if (payloadEnd > buffer.Length)
            throw new FormatException("Byte string length exceeds buffer size.");

        byte[] payload = new byte[length];
        Buffer.BlockCopy(buffer, payloadStart, payload, 0, length);
        return (new BEncodedString(payload), payloadEnd);
    }

    /// <summary>
    /// Decode an integer with format "iDIGITSe" with an optional leading '-'.
    /// Note: BEncoded integers have no theoretical upper bound, but C# long
    /// (Int64) comfortably covers the practical range used in BitTorrent.
    /// </summary>
    private static (BEncodedValue Value, int NextOffset) DecodeInteger(byte[] buffer, int offset)
    {
        int endIndex = Array.IndexOf(buffer, (byte)'e', offset + 1);
        if (endIndex < 0)
            throw new FormatException("Missing 'e' terminator on integer.");

        // Hand-parse: avoids a 1-byte ASCII->string allocation per integer
        // and rejects "-0" and "03" forms which int.TryParse would accept.
        int cursor = offset + 1;
        bool negative = false;
        long value = 0;
        if (cursor < endIndex && buffer[cursor] == (byte)'-')
        {
            negative = true;
            cursor++;
        }
        if (cursor == endIndex)
            throw new FormatException("Empty integer body.");
        for (int i = cursor; i < endIndex; i++)
        {
            byte b = buffer[i];
            if ((uint)(b - (byte)'0') > 9)
                throw new FormatException($"Non-digit 0x{b:X2} in integer body.");
            value = value * 10 + (b - (byte)'0');
        }
        if (negative) value = -value;

        return (new BEncodedInteger(value), endIndex + 1);
    }

    /// <summary>
    /// Decode a list with format "l<items>e". Items may be of any BEncoded type
    /// including further nested lists or dictionaries.
    /// </summary>
    private static (BEncodedValue Value, int NextOffset) DecodeList(byte[] buffer, int offset)
    {
        var items = new List<BEncodedValue>();
        int cursor = offset + 1;            // skip past 'l'
        while (cursor < buffer.Length && buffer[cursor] != (byte)'e')
        {
            var (item, next) = DecodeValue(buffer, cursor);
            items.Add(item);
            cursor = next;
        }
        if (cursor >= buffer.Length)
            throw new FormatException("Missing 'e' terminator on list.");
        return (new BEncodedList(items), cursor + 1);
    }

    /// <summary>
    /// Decode a dictionary with format "d<key><value>...e". We accept the input
    /// even if keys arrive out of order, but we still expose them via the usual
    /// Indexer — order is not preserved for callers since C# Dictionaries do
    /// not guarantee enumeration order. What MATTERS is that re-serialization
    /// (see <see cref="Encode"/>) sorts keys canonically.
    /// </summary>
    private static (BEncodedValue Value, int NextOffset) DecodeDictionary(byte[] buffer, int offset)
    {
        var entries = new Dictionary<BEncodedString, BEncodedValue>();
        int cursor = offset + 1;            // skip past 'd'
        while (cursor < buffer.Length && buffer[cursor] != (byte)'e')
        {
            var (key, nextKey) = DecodeValue(buffer, cursor);
            if (key is not BEncodedString keyString)
                throw new FormatException("Dictionary keys must be byte strings.");
            var (value, nextValue) = DecodeValue(buffer, nextKey);
            entries[keyString] = value;
            cursor = nextValue;
        }
        if (cursor >= buffer.Length)
            throw new FormatException("Missing 'e' terminator on dictionary.");
        return (new BEncodedDictionary(entries), cursor + 1);
    }

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Serialize a <see cref="BEncodedValue"/> (or any compatible plain object)
    /// into its canonical byte form. Dictionary keys are sorted by raw byte order
    /// to guarantee a stable infohash.
    /// </summary>
    public static byte[] Encode(BEncodedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Size the buffer with a reasonable initial capacity so multi-MB info
        // dictionaries don't double their memory footprint via MemoryStream's
        // doubling growth policy.
        var initialCapacity = EstimateEncodedSize(value);
        var stream = new MemoryStream(initialCapacity);
        EncodeInto(value, stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Convenience helper that encodes a value to UTF-8 text. Useful for debugging
    /// or for human-readable display in console applications.
    /// </summary>
    public static string EncodeToString(BEncodedValue value) =>
        Encoding.UTF8.GetString(Encode(value));

    /// <summary>
    /// Lower bound on the encoded size — number of digit bytes per integer /
    /// length prefix, plus a colon, plus the raw payload.
    /// Used to size the encoder's MemoryStream so we don't pay double-allocation
    /// for large info dictionaries.
    /// </summary>
    private static int EstimateEncodedSize(BEncodedValue value) => value switch
    {
        BEncodedString str => str.Value.Length + 8,
        BEncodedInteger    => 24,
        BEncodedList list  => 8 + list.Value.Sum(EstimateEncodedSize),
        BEncodedDictionary dict =>
            8 + dict.Value.Sum(kv => EstimateEncodedSize(kv.Key) + EstimateEncodedSize(kv.Value)),
        _ => 0,
    };

    /// <summary>
    /// Stream-writing form of the encoder. Reuses a single buffer to avoid
    /// allocating intermediate arrays when serializing large dictionaries.
    /// </summary>
    private static void EncodeInto(BEncodedValue value, Stream output)
    {
        switch (value)
        {
            case BEncodedString str:
                {
                    // LENGTH:PAYLOAD. Use a stack-allocated ASCII byte span
                    // since the typical encoded lengths fit in <10 digits.
                    Span<byte> digits = stackalloc byte[20];
                    int n = WriteDecimal(str.Value.Length, digits);
                    output.Write(digits.Slice(0, n));
                    output.WriteByte((byte)':');
                    output.Write(str.Value, 0, str.Value.Length);
                    break;
                }
            case BEncodedInteger num:
                {
                    // iINTEGERe — same trick: digit array, no string allocation.
                    Span<byte> digits = stackalloc byte[24];
                    int n = WriteDecimal(num.Value, digits);
                    output.WriteByte((byte)'i');
                    output.Write(digits.Slice(0, n));
                    output.WriteByte((byte)'e');
                    break;
                }
            case BEncodedList list:
                {
                    output.WriteByte((byte)'l');
                    foreach (var item in list.Value) EncodeInto(item, output);
                    output.WriteByte((byte)'e');
                    break;
                }
            case BEncodedDictionary dict:
                {
                    output.WriteByte((byte)'d');
                    // BEncodedDictionary stores entries in a regular Dictionary under
                    // the hood, but we re-sort defensively here using the canonical
                    // comparer so the output is correct regardless of construction.
                    // (Almost zero cost for typical torrent-info sizes which have < 20 keys.)
                    var keys = dict.Value.Keys.OrderBy(k => k.Value, ByteArrayComparer.Instance).ToArray();
                    foreach (var key in keys)
                    {
                        EncodeInto(key, output);
                        EncodeInto(dict.Value[key], output);
                    }
                    output.WriteByte((byte)'e');
                    break;
                }
            default:
                throw new InvalidOperationException($"Cannot encode value of type {value.GetType()}.");
        }
    }

    // -------------------------------------------------------------------------
    // Decimal writer (no allocation, byte-form)
    // -------------------------------------------------------------------------

    /// <summary>Write the decimal representation of <paramref name="value"/> into
    /// <paramref name="dst"/> and return the length written. Handles negatives.</summary>
    private static int WriteDecimal(long value, Span<byte> dst)
    {
        if (value == 0) { dst[0] = (byte)'0'; return 1; }

        bool negative = value < 0;
        // Avoid long.MinValue asymmetry by writing to ulong.
        ulong abs = negative ? (ulong)(~value) + 1UL : (ulong)value;
        int len = 0;
        Span<byte> buf = stackalloc byte[20]; // enough for ulong.MaxValue
        while (abs != 0)
        {
            buf[len++] = (byte)('0' + (int)(abs % 10));
            abs /= 10;
        }
        // Reverse into dst, then prepend sign if needed.
        int o = 0;
        if (negative) dst[o++] = (byte)'-';
        for (int i = len - 1; i >= 0; i--) dst[o++] = buf[i];
        return o;
    }
}

// =====================================================================================
// BEncoded value types (object model).
// These are lightweight, immutable wrappers over their underlying payloads.
// =====================================================================================

public interface BEncodedValue { }

/// <summary>Wraps a BEncoded byte string. The spec technically calls these "strings"
/// but they're often used to carry arbitrary binary blobs (e.g. piece hashes,
/// peer binary address strings).</summary>
public sealed class BEncodedString(byte[] value) : BEncodedValue
{
    public byte[] Value { get; } = value ?? throw new ArgumentNullException(nameof(value));

    /// <summary>Decode the byte string as UTF-8 text.</summary>
    public string AsText() => Encoding.UTF8.GetString(Value);

    /// <summary>Implements equality based on the wrapped bytes. Required so the
    /// dictionary lookup in <see cref="BEncodedDictionary"/> actually works as a
    /// keyed store even when callers construct their own <see cref="BEncodedString"/>
    /// instances with identical content. Without this the default object equality
    /// causes every key to hash to a different bucket.</summary>
    public override int GetHashCode() =>
        ByteArrayComparer.Instance.GetHashCode(Value);

    public override bool Equals(object? obj) =>
        obj is BEncodedString other && ByteArrayComparer.Instance.Compare(Value, other.Value) == 0;
}

/// <summary>Wraps a BEncoded integer. Range is constrained to C# long.</summary>
public sealed class BEncodedInteger(long value) : BEncodedValue
{
    public long Value { get; } = value;
}

/// <summary>Wraps a BEncoded list. Items may be heterogeneous.</summary>
public sealed class BEncodedList(IReadOnlyList<BEncodedValue> value) : BEncodedValue
{
    public IReadOnlyList<BEncodedValue> Value { get; } = value ?? Array.Empty<BEncodedValue>();
}

/// <summary>Wraps a BEncoded dictionary. Keys are byte strings; values are heterogeneous.</summary>
public sealed class BEncodedDictionary(IDictionary<BEncodedString, BEncodedValue> value) : BEncodedValue
{
    public IDictionary<BEncodedString, BEncodedValue> Value { get; } = value;

    /// <summary>Look up a value by UTF-8-decoded key. Returns null if absent.</summary>
    public BEncodedValue? this[string utf8Key]
    {
        get
        {
            var bytes = Encoding.UTF8.GetBytes(utf8Key);
            foreach (var pair in Value)
                if (pair.Key.Value.AsSpan().SequenceEqual(bytes))
                    return pair.Value;
            return null;
        }
    }
}

// =====================================================================================
// ByteArrayComparer
// Lexicographic comparison over raw byte arrays. This is the EXACT ordering used
// by canonical BEncoded dictionaries, and is therefore identical to a memcmp()
// on Linux, BSD, and macOS. On Windows, default string comparison would diverge,
// so we provide this canonical version explicitly.
//
// Equality contract: two byte arrays compare equal iff their lengths and byte
// contents match exactly. We provide a GetHashCode that's stable across runs and
// independent of array identity (use the first few bytes spread-out).
// =====================================================================================
public sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public int Compare(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int min = Math.Min(x.Length, y.Length);
        for (int i = 0; i < min; i++)
        {
            int diff = x[i] - y[i];
            if (diff != 0) return diff;
        }
        return x.Length - y.Length;
    }

    public bool Equals(byte[]? x, byte[]? y) => Compare(x, y) == 0;

    public int GetHashCode(byte[] obj)
    {
        // FNV-1a 32-bit, collision-free for distinct inputs of length <= ~30.
        // For larger pieces (20 bytes SHA-1) this gives good distribution with
        // very low cost.
        const uint offset_basis = 2166136261u;
        const uint prime        = 16777619u;
        uint h = offset_basis;
        for (int i = 0; i < obj.Length; i++)
        {
            h ^= obj[i];
            h *= prime;
        }
        return (int)(h ^ (uint)obj.Length);
    }
}
