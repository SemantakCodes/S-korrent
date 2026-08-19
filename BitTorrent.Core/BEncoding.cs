


using System.Buffers;
using System.Text;

namespace BitTorrent.Core;


public static class BEncoding
{
    
    
    

    
    
    
    
    
    
    
    public static BEncodedValue Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var (value, _) = DecodeValue(data, 0);
        return value;
    }

    
    
    
    
    
    
    public static BEncodedValue Decode(byte[] data, int offset)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (offset < 0 || offset >= data.Length)
            throw new FormatException("Decode offset is out of range.");
        var (value, _) = DecodeValue(data, offset);
        return value;
    }

    
    
    
    
    public static BEncodedValue Decode(string text) =>
        Decode(Encoding.UTF8.GetBytes(text));

    
    
    
    
    
    
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

    
    
    
    
    
    
    
    private static (BEncodedValue Value, int NextOffset) DecodeString(byte[] buffer, int offset)
    {
        int colonIndex = Array.IndexOf(buffer, (byte)':', offset);
        if (colonIndex < 0)
            throw new FormatException("Missing ':' separator in byte string.");

        
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

    
    
    
    
    
    private static (BEncodedValue Value, int NextOffset) DecodeInteger(byte[] buffer, int offset)
    {
        int endIndex = Array.IndexOf(buffer, (byte)'e', offset + 1);
        if (endIndex < 0)
            throw new FormatException("Missing 'e' terminator on integer.");

        
        
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

    
    
    
    
    private static (BEncodedValue Value, int NextOffset) DecodeList(byte[] buffer, int offset)
    {
        var items = new List<BEncodedValue>();
        int cursor = offset + 1;            
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

    
    
    
    
    
    
    
    private static (BEncodedValue Value, int NextOffset) DecodeDictionary(byte[] buffer, int offset)
    {
        var entries = new Dictionary<BEncodedString, BEncodedValue>();
        int cursor = offset + 1;            
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

    
    
    

    
    
    
    
    
    public static byte[] Encode(BEncodedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        
        
        
        var initialCapacity = EstimateEncodedSize(value);
        var stream = new MemoryStream(initialCapacity);
        EncodeInto(value, stream);
        return stream.ToArray();
    }

    
    
    
    
    public static string EncodeToString(BEncodedValue value) =>
        Encoding.UTF8.GetString(Encode(value));

    
    
    
    
    
    
    private static int EstimateEncodedSize(BEncodedValue value) => value switch
    {
        BEncodedString str => str.Value.Length + 8,
        BEncodedInteger    => 24,
        BEncodedList list  => 8 + list.Value.Sum(EstimateEncodedSize),
        BEncodedDictionary dict =>
            8 + dict.Value.Sum(kv => EstimateEncodedSize(kv.Key) + EstimateEncodedSize(kv.Value)),
        _ => 0,
    };

    
    
    
    
    private static void EncodeInto(BEncodedValue value, Stream output)
    {
        switch (value)
        {
            case BEncodedString str:
                {
                    
                    
                    Span<byte> digits = stackalloc byte[20];
                    int n = WriteDecimal(str.Value.Length, digits);
                    output.Write(digits.Slice(0, n));
                    output.WriteByte((byte)':');
                    output.Write(str.Value, 0, str.Value.Length);
                    break;
                }
            case BEncodedInteger num:
                {
                    
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

    
    
    

    
    
    private static int WriteDecimal(long value, Span<byte> dst)
    {
        if (value == 0) { dst[0] = (byte)'0'; return 1; }

        bool negative = value < 0;
        
        ulong abs = negative ? (ulong)(~value) + 1UL : (ulong)value;
        int len = 0;
        Span<byte> buf = stackalloc byte[20]; 
        while (abs != 0)
        {
            buf[len++] = (byte)('0' + (int)(abs % 10));
            abs /= 10;
        }
        
        int o = 0;
        if (negative) dst[o++] = (byte)'-';
        for (int i = len - 1; i >= 0; i--) dst[o++] = buf[i];
        return o;
    }
}



public interface BEncodedValue { }


public sealed class BEncodedString(byte[] value) : BEncodedValue
{
    public byte[] Value { get; } = value ?? throw new ArgumentNullException(nameof(value));

    
    public string AsText() => Encoding.UTF8.GetString(Value);

    
    
    
    
    
    public override int GetHashCode() =>
        ByteArrayComparer.Instance.GetHashCode(Value);

    public override bool Equals(object? obj) =>
        obj is BEncodedString other && ByteArrayComparer.Instance.Compare(Value, other.Value) == 0;
}


public sealed class BEncodedInteger(long value) : BEncodedValue
{
    public long Value { get; } = value;
}


public sealed class BEncodedList(IReadOnlyList<BEncodedValue> value) : BEncodedValue
{
    public IReadOnlyList<BEncodedValue> Value { get; } = value ?? Array.Empty<BEncodedValue>();
}


public sealed class BEncodedDictionary(IDictionary<BEncodedString, BEncodedValue> value) : BEncodedValue
{
    public IDictionary<BEncodedString, BEncodedValue> Value { get; } = value;

    
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

