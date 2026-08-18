// =====================================================================================
// PercentEncoding.cs
// =====================================================================================
// BitTorrent's tracker protocol requires raw 20-byte SHA-1 hashes (info_hash) and
// 20-byte peer IDs to be percent-encoded for embedding inside HTTP query strings.
//
// Tracker convention dictates RFC 3986 unreserved characters pass through and the
// rest be encoded as %XX where the hex digits are LOWERCASE. Some trackers reject
// uppercase encodings (opentracker, ocelot). We implement that behavior here.
//
// Keeping this in a dedicated file ensures the encoder is identical everywhere
// it's used: Torrent.UrlEncodeInfoHash, TrackerClient.AnnounceAsync, and any
// future peer-ID encoders (extension protocol BEP 9).
// =====================================================================================

using System.Text;

namespace BitTorrent.Core;

public static class PercentEncoding
{
    /// <summary>
    /// RFC 3986 percent-encoder emitting lowercase hex.
    /// </summary>
    /// <param name="bytes">Bytes to encode. Not modified.</param>
    /// <returns>A string of 1-3 characters per input byte.</returns>
    public static string Encode(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;

        // Worst case is 3 chars per byte: '%XX'. Allocate exactly the right size
        // up-front so we never resize during the append loop.
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
