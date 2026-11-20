using System.Text;

namespace Cellular.Core;

/// <summary>One decoded SMS-DELIVER PDU (possibly one part of a concatenated message).</summary>
public sealed record SmsPart(
    string Sender, DateTimeOffset Timestamp, string Text, int MessageClass,
    int ConcatRef, int ConcatTotal, int ConcatSeq)
{
    public bool IsMultipart => ConcatTotal > 1;
    /// <summary>The PDU was cut short by the driver (text ends with "…").</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// Decoder for 3GPP TS 23.040 SMS-DELIVER PDUs as returned by the modem store
/// (<c>SmsBinaryMessage.GetData()</c>, format GsmDeliver).
/// Windows' own SmsTextMessage.FromBinaryMessage ignores the user-data length (so the RIL's zero padding
/// shows up as "@@@@") and doesn't reassemble concatenated messages — hence this decoder.
/// </summary>
public static class SmsPdu
{
    public static SmsPart Decode(byte[] pdu, bool hasSmsc = true)
    {
        int i = 0;
        if (hasSmsc) i += 1 + pdu[0];                          // SMSC info
        byte first = pdu[i++];
        if ((first & 0x03) != 0x00) throw new FormatException($"Not an SMS-DELIVER PDU (MTI {first & 3}).");
        bool udhi = (first & 0x40) != 0;

        int oaLen = pdu[i++];                                  // address length in semi-octets
        byte toa = pdu[i++];
        int oaBytes = (oaLen + 1) / 2;
        string sender = DecodeAddress(pdu.AsSpan(i, oaBytes), oaLen, toa);
        i += oaBytes;

        i++;                                                   // PID
        byte dcs = pdu[i++];
        var ts = DecodeTimestamp(pdu.AsSpan(i, 7)); i += 7;
        int udl = pdu[i++];

        var (alphabet, msgClass) = ParseDcs(dcs);
        var ud = pdu.AsSpan(i);

        int udhBytes = 0, cRef = 0, cTotal = 1, cSeq = 1;
        if (udhi && ud.Length > 0)
        {
            int udhl = ud[0];
            udhBytes = udhl + 1;
            int p = 1;
            while (p < udhBytes)
            {
                int iei = ud[p], iel = ud[p + 1];
                var data = ud.Slice(p + 2, iel);
                if (iei == 0x00 && iel == 3) { cRef = data[0]; cTotal = data[1]; cSeq = data[2]; }
                else if (iei == 0x08 && iel == 4) { cRef = (data[0] << 8) | data[1]; cTotal = data[2]; cSeq = data[3]; }
                p += 2 + iel;
            }
        }

        string text = alphabet switch
        {
            Alphabet.Gsm7 => DecodeGsm7(ud, udl, udhBytes),
            Alphabet.Ucs2 => Encoding.BigEndianUnicode.GetString(ud.Slice(udhBytes, Math.Max(0, Math.Min(udl, ud.Length) - udhBytes))),
            _ => Encoding.Latin1.GetString(ud.Slice(udhBytes, Math.Max(0, Math.Min(udl, ud.Length) - udhBytes))),
        };

        // The DW5820e driver's bulk store listing zero-fills PDUs after ~50 bytes of text. Detect that
        // (zero bytes where the declared user data should still be), cut the garbage and mark it.
        int expectedUdBytes = alphabet == Alphabet.Gsm7 ? (udl * 7 + 7) / 8 : udl;
        int lastNonZero = ud.Length - 1;
        while (lastNonZero >= 0 && ud[lastNonZero] == 0) lastNonZero--;
        bool truncated = lastNonZero + 1 < expectedUdBytes - 1 && expectedUdBytes - (lastNonZero + 1) >= 3;
        if (truncated)
        {
            text = text.TrimEnd('@', '\0');
            if (text.Length > 0) text = text[..^1];             // last char was built from a partial septet
            text = text.TrimEnd() + "…";
        }

        return new SmsPart(sender, ts, text, msgClass, cRef, cTotal, cSeq) { Truncated = truncated };
    }

    private enum Alphabet { Gsm7, EightBit, Ucs2 }

    private static (Alphabet, int) ParseDcs(byte dcs)
    {
        int cls = -1;
        Alphabet a = Alphabet.Gsm7;
        if ((dcs & 0xC0) == 0x00 || (dcs & 0xC0) == 0x40)     // general data coding (incl. auto-deletion group)
        {
            a = ((dcs >> 2) & 0x03) switch { 1 => Alphabet.EightBit, 2 => Alphabet.Ucs2, _ => Alphabet.Gsm7 };
            if ((dcs & 0x10) != 0) cls = dcs & 0x03;
        }
        else if ((dcs & 0xF0) == 0xF0)                         // data coding / message class
        {
            a = (dcs & 0x04) != 0 ? Alphabet.EightBit : Alphabet.Gsm7;
            cls = dcs & 0x03;
        }
        else if ((dcs & 0xF0) == 0xE0) a = Alphabet.Ucs2;      // MWI group, UCS2
        return (a, cls);
    }

    private static string DecodeAddress(ReadOnlySpan<byte> b, int semiOctets, byte toa)
    {
        int ton = (toa >> 4) & 0x07;
        if (ton == 0x05)                                        // alphanumeric (GSM 7-bit packed)
        {
            int septets = semiOctets * 4 / 7;
            return DecodeGsm7(b, septets, 0).TrimEnd('@', '\0', ' ');
        }
        var sb = new StringBuilder();
        foreach (var x in b)
        {
            int lo = x & 0x0F, hi = x >> 4;
            if (lo != 0x0F) sb.Append(BcdChar(lo));
            if (hi != 0x0F) sb.Append(BcdChar(hi));
        }
        var s = sb.ToString(0, Math.Min(sb.Length, semiOctets));
        return ton == 0x01 ? "+" + s : s;
    }

    private static char BcdChar(int n) => n switch { <= 9 => (char)('0' + n), 0x0A => '*', 0x0B => '#', _ => (char)('a' + n - 0x0C) };

    private static DateTimeOffset DecodeTimestamp(ReadOnlySpan<byte> b)
    {
        static int Swap(byte x) => (x & 0x0F) * 10 + (x >> 4);
        try
        {
            int tzRaw = ((b[6] & 0x07) * 10) + (b[6] >> 4);   // quarter hours; bit 3 of low nibble = sign
            bool neg = (b[6] & 0x08) != 0;
            var offset = TimeSpan.FromMinutes(tzRaw * 15 * (neg ? -1 : 1));
            return new DateTimeOffset(2000 + Swap(b[0]), Swap(b[1]), Swap(b[2]), Swap(b[3]), Swap(b[4]), Swap(b[5]), offset);
        }
        catch { return DateTimeOffset.Now; }
    }

    // GSM 03.38 default alphabet and extension table
    private const string Basic =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001BÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    private static char Ext(int c) => c switch
    {
        0x0A => '\f', 0x14 => '^', 0x28 => '{', 0x29 => '}', 0x2F => '\\', 0x3C => '[', 0x3D => '~',
        0x3E => ']', 0x40 => '|', 0x65 => '€', _ => ' ',
    };

    /// <param name="septets">UDL: total septets including the header.</param>
    /// <param name="udhBytes">Header length in octets (UDHL+1), or 0.</param>
    private static string DecodeGsm7(ReadOnlySpan<byte> data, int septets, int udhBytes)
    {
        int startBit = udhBytes * 8;
        if (udhBytes > 0) startBit += (7 - (udhBytes * 8) % 7) % 7;   // fill bits to septet boundary
        int skipSeptets = startBit / 7;
        var sb = new StringBuilder();
        bool escape = false;
        for (int s = skipSeptets; s < septets; s++)
        {
            int bit = s * 7, byteIdx = bit / 8, shift = bit % 8;
            if (byteIdx >= data.Length) break;
            int v = data[byteIdx] >> shift;
            if (shift > 1 && byteIdx + 1 < data.Length) v |= data[byteIdx + 1] << (8 - shift);
            v &= 0x7F;
            if (escape) { sb.Append(Ext(v)); escape = false; }
            else if (v == 0x1B) escape = true;
            else sb.Append(Basic[v]);
        }
        return sb.ToString();
    }
}

/// <summary>Joins the parts of concatenated SMS. Incomplete sets are returned as-is (parts in order, "…" for gaps).</summary>
public static class SmsAssembler
{
    public sealed record Assembled(string Sender, DateTimeOffset Timestamp, string Text, int MessageClass, IReadOnlyList<uint> StoreIds, bool Complete);

    public static List<Assembled> Assemble(IEnumerable<(uint StoreId, SmsPart Part)> parts)
    {
        var result = new List<Assembled>();
        var groups = parts.GroupBy(p => p.Part.IsMultipart
            ? $"{p.Part.Sender}|{p.Part.ConcatRef}|{p.Part.ConcatTotal}"
            : $"single|{p.StoreId}");
        foreach (var g in groups)
        {
            // the same ref can be reused later by the network: split sets that are far apart in time
            foreach (var set in SplitByTime(g.OrderBy(x => x.Part.Timestamp).ToList()))
            {
                var ordered = set.GroupBy(x => x.Part.ConcatSeq).Select(x => x.First()).OrderBy(x => x.Part.ConcatSeq).ToList();
                var first = ordered[0].Part;
                int total = first.ConcatTotal;
                var sb = new StringBuilder();
                for (int seq = 1; seq <= total; seq++)
                {
                    var p = ordered.FirstOrDefault(x => x.Part.ConcatSeq == seq);
                    sb.Append(p.Part is null ? "…" : p.Part.Text);
                }
                result.Add(new Assembled(first.Sender, ordered.Min(x => x.Part.Timestamp), sb.ToString(),
                    first.MessageClass, ordered.Select(x => x.StoreId).ToList(), ordered.Count >= total));
            }
        }
        return result.OrderBy(r => r.Timestamp).ToList();
    }

    private static IEnumerable<List<(uint StoreId, SmsPart Part)>> SplitByTime(List<(uint StoreId, SmsPart Part)> items)
    {
        var cur = new List<(uint, SmsPart)>();
        foreach (var it in items)
        {
            if (cur.Count > 0 && (it.Part.Timestamp - cur[^1].Item2.Timestamp).Duration() > TimeSpan.FromMinutes(10))
            {
                yield return cur;
                cur = [];
            }
            cur.Add(it);
        }
        if (cur.Count > 0) yield return cur;
    }
}
