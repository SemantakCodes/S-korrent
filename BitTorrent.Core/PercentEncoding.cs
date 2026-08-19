


using System.Text;

namespace BitTorrent.Core;

public static class PercentEncoding
{
    
    
    
    
    
    public static string Encode(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;

        
        
        var sb = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if ((b >= (byte)'A' && b <= (byte)'Z') ||
                (b >= (byte)'a' && b <= (byte)'z') ||
                (b >= (byte)'0' && b <= (byte)'9') ||
                b == (byte)'.' || b == (byte)'-' || b == (byte)'_' || b == (byte)'~')
            {
                sb.Append((char)b);
            }
            else
            {
                sb.Append('%');
                sb.Append(HexChar[(b >> 4) & 0xF]);
                sb.Append(HexChar[b & 0xF]);
            }
        }
        return sb.ToString();
    }

    private static readonly char[] HexChar = "0123456789abcdef".ToCharArray();
}

