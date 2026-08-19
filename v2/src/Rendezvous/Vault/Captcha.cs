using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// Self-hosted CAPTCHA: a distorted-text PNG rendered with no image library and
/// no third-party service.
///
/// System.Drawing does not work on Linux, and pulling in SkiaSharp for one
/// endpoint would be the largest dependency in the server. So this draws its
/// own: a built-in 5x7 pixel font scaled up, per-character rotation, a sine
/// wave through the whole image, and noise — then a hand-rolled PNG encoder
/// (the format is just zlib plus checksums, both of which the BCL nearly
/// provides).
///
/// Honesty note: this stops scripted drive-by registrations, not a determined
/// attacker with an OCR model. The owner-approval gate is what actually
/// protects the library; this keeps junk out of the approval queue.
/// </summary>
public class CaptchaStore
{
    /// <summary>Same alphabet as room codes: no ambiguous characters to squint at.</summary>
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int Length = 5;
    private const int TtlMinutes = 10;

    /// <summary>
    /// Cap on outstanding challenges. Every refused-over-cap request costs an
    /// attacker a round trip and costs us nothing; without the cap, spamming
    /// GET /captcha grows the dictionary without bound.
    /// </summary>
    private const int MaxOutstanding = 2000;

    private sealed record Challenge(string Answer, long ExpiresAt);
    private readonly ConcurrentDictionary<string, Challenge> _open = new();

    public (string Id, byte[] Png)? Issue()
    {
        Sweep();
        if (_open.Count >= MaxOutstanding) return null;

        var answer = new string(Enumerable.Range(0, Length)
            .Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _open[id] = new Challenge(answer,
            DateTimeOffset.UtcNow.AddMinutes(TtlMinutes).ToUnixTimeMilliseconds());
        return (id, CaptchaImage.Render(answer));
    }

    /// <summary>
    /// Single use: the challenge is removed before the answer is checked, so a
    /// wrong guess burns it and the next attempt needs a fresh image.
    /// </summary>
    public bool Consume(string? id, string? answer)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(answer)) return false;
        if (!_open.TryRemove(id, out var c)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > c.ExpiresAt) return false;
        return string.Equals(c.Answer, answer.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Answer lookup for the VAULT_CAPTCHA_REVEAL test hook only.</summary>
    public string? Peek(string id) => _open.TryGetValue(id, out var c) ? c.Answer : null;

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (key, c) in _open)
            if (c.ExpiresAt < now) _open.TryRemove(key, out _);
    }
}

/// <summary>Draws the challenge text and encodes it as a PNG.</summary>
public static class CaptchaImage
{
    private const int W = 260, H = 80;

    public static byte[] Render(string text)
    {
        // Coverage buffer first, colour later: the wave distortion is a row
        // resample, which is simplest on a single channel.
        var ink = new byte[W * H];
        var rng = Random.Shared;

        var cell = W / (text.Length + 1);
        for (var i = 0; i < text.Length; i++)
        {
            var glyph = Font5x7.Glyph(text[i]);
            if (glyph is null) continue;

            var scale = 6.0 + rng.NextDouble() * 1.6;
            var angle = (rng.NextDouble() - 0.5) * 0.75;          // ±21°
            var cx = cell * (i + 1) + rng.Next(-4, 5);
            var cy = H / 2 + rng.Next(-6, 7);
            var (sin, cos) = Math.SinCos(angle);

            // Inverse mapping: walk the destination pixels, rotate each back
            // into glyph space, and test the bit. Forward-painting scaled,
            // rotated blocks leaves moiré holes; this cannot.
            var half = (int)(5 * scale);
            for (var dy = -half; dy <= half; dy++)
            for (var dx = -half; dx <= half; dx++)
            {
                var gx = (dx * cos + dy * sin) / scale + 2.5;
                var gy = (-dx * sin + dy * cos) / scale + 3.5;
                if (gx is < 0 or >= 5 || gy is < 0 or >= 7) continue;
                if (!glyph[(int)gy * 5 + (int)gx]) continue;
                var px = cx + dx;
                var py = cy + dy;
                if (px >= 0 && px < W && py >= 0 && py < H) ink[py * W + px] = 255;
            }
        }

        // Sine wave through everything, so characters can't be segmented by
        // clean horizontal runs.
        var waved = new byte[W * H];
        var amp = 4.0 + rng.NextDouble() * 3;
        var freq = 0.035 + rng.NextDouble() * 0.02;
        var phase = rng.NextDouble() * Math.PI * 2;
        for (var x = 0; x < W; x++)
        {
            var shift = (int)(amp * Math.Sin(x * freq + phase));
            for (var y = 0; y < H; y++)
            {
                var sy = y + shift;
                if (sy >= 0 && sy < H) waved[y * W + x] = ink[sy * W + x];
            }
        }

        // Two curved strokes and speckle noise over the top.
        for (var line = 0; line < 2; line++)
        {
            var lp = rng.NextDouble() * Math.PI * 2;
            var lf = 0.02 + rng.NextDouble() * 0.03;
            var ly = 15 + rng.Next(H - 30);
            for (var x = 0; x < W; x++)
            {
                var y = ly + (int)(8 * Math.Sin(x * lf + lp));
                if (y >= 0 && y < H - 1) { waved[y * W + x] = 200; waved[(y + 1) * W + x] = 200; }
            }
        }
        for (var i = 0; i < 220; i++)
            waved[rng.Next(H) * W + rng.Next(W)] = (byte)rng.Next(120, 255);

        // Colourise: warm paper background, dark ink with slight variation.
        var rgb = new byte[W * H * 3];
        for (var i = 0; i < W * H; i++)
        {
            var v = waved[i];
            if (v == 0)
            {
                rgb[i * 3 + 0] = 244; rgb[i * 3 + 1] = 238; rgb[i * 3 + 2] = 228;
            }
            else
            {
                var shade = (byte)(255 - v);
                rgb[i * 3 + 0] = (byte)(40 + shade / 3);
                rgb[i * 3 + 1] = (byte)(26 + shade / 4);
                rgb[i * 3 + 2] = (byte)(20 + shade / 4);
            }
        }
        return Png.Encode(W, H, rgb);
    }
}

/// <summary>
/// Minimal PNG writer: signature, IHDR, one zlib-compressed IDAT with filter 0
/// on every scanline, IEND. ZLibStream supplies the compression; only the CRC
/// is hand-rolled.
/// </summary>
internal static class Png
{
    public static byte[] Encode(int w, int h, byte[] rgb)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        WriteBe(ihdr, 0, w);
        WriteBe(ihdr, 4, h);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // colour type: truecolour
        Chunk(output, "IHDR", ihdr);

        // Raw stream: each scanline prefixed with filter byte 0 (None).
        var raw = new byte[h * (1 + w * 3)];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(rgb, y * w * 3, raw, y * (1 + w * 3) + 1, w * 3);

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        Chunk(output, "IDAT", compressed.ToArray());

        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var header = new byte[4];
        WriteBe(header, 0, data.Length);
        s.Write(header);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBe(crcBytes, 0, (int)crc);
        s.Write(crcBytes);
    }

    private static void WriteBe(byte[] buf, int at, int value)
    {
        buf[at] = (byte)(value >> 24);
        buf[at + 1] = (byte)(value >> 16);
        buf[at + 2] = (byte)(value >> 8);
        buf[at + 3] = (byte)value;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        var crc = 0xFFFFFFFF;
        foreach (var x in a) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in b) crc = Table[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}

/// <summary>
/// The glyphs the captcha can draw: the room-code alphabet in a 5x7 pixel font.
/// Ambiguous characters (I, L, O, 0, 1) are absent by construction.
/// </summary>
internal static class Font5x7
{
    public static bool[]? Glyph(char c) =>
        Glyphs.TryGetValue(char.ToUpperInvariant(c), out var rows) ? Decode(rows) : null;

    private static bool[] Decode(string[] rows)
    {
        var bits = new bool[35];
        for (var y = 0; y < 7; y++)
        for (var x = 0; x < 5; x++)
            bits[y * 5 + x] = rows[y][x] == 'X';
        return bits;
    }

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['A'] = new[] { ".XXX.", "X...X", "X...X", "XXXXX", "X...X", "X...X", "X...X" },
        ['B'] = new[] { "XXXX.", "X...X", "X...X", "XXXX.", "X...X", "X...X", "XXXX." },
        ['C'] = new[] { ".XXXX", "X....", "X....", "X....", "X....", "X....", ".XXXX" },
        ['D'] = new[] { "XXXX.", "X...X", "X...X", "X...X", "X...X", "X...X", "XXXX." },
        ['E'] = new[] { "XXXXX", "X....", "X....", "XXXX.", "X....", "X....", "XXXXX" },
        ['F'] = new[] { "XXXXX", "X....", "X....", "XXXX.", "X....", "X....", "X...." },
        ['G'] = new[] { ".XXXX", "X....", "X....", "X..XX", "X...X", "X...X", ".XXXX" },
        ['H'] = new[] { "X...X", "X...X", "X...X", "XXXXX", "X...X", "X...X", "X...X" },
        ['J'] = new[] { "..XXX", "...X.", "...X.", "...X.", "...X.", "X..X.", ".XX.." },
        ['K'] = new[] { "X...X", "X..X.", "X.X..", "XX...", "X.X..", "X..X.", "X...X" },
        ['M'] = new[] { "X...X", "XX.XX", "X.X.X", "X.X.X", "X...X", "X...X", "X...X" },
        ['N'] = new[] { "X...X", "XX..X", "X.X.X", "X..XX", "X...X", "X...X", "X...X" },
        ['P'] = new[] { "XXXX.", "X...X", "X...X", "XXXX.", "X....", "X....", "X...." },
        ['Q'] = new[] { ".XXX.", "X...X", "X...X", "X...X", "X.X.X", "X..X.", ".XX.X" },
        ['R'] = new[] { "XXXX.", "X...X", "X...X", "XXXX.", "X.X..", "X..X.", "X...X" },
        ['S'] = new[] { ".XXXX", "X....", "X....", ".XXX.", "....X", "....X", "XXXX." },
        ['T'] = new[] { "XXXXX", "..X..", "..X..", "..X..", "..X..", "..X..", "..X.." },
        ['U'] = new[] { "X...X", "X...X", "X...X", "X...X", "X...X", "X...X", ".XXX." },
        ['V'] = new[] { "X...X", "X...X", "X...X", "X...X", "X...X", ".X.X.", "..X.." },
        ['W'] = new[] { "X...X", "X...X", "X...X", "X.X.X", "X.X.X", "XX.XX", "X...X" },
        ['X'] = new[] { "X...X", "X...X", ".X.X.", "..X..", ".X.X.", "X...X", "X...X" },
        ['Y'] = new[] { "X...X", "X...X", ".X.X.", "..X..", "..X..", "..X..", "..X.." },
        ['Z'] = new[] { "XXXXX", "....X", "...X.", "..X..", ".X...", "X....", "XXXXX" },
        ['2'] = new[] { ".XXX.", "X...X", "....X", "...X.", "..X..", ".X...", "XXXXX" },
        ['3'] = new[] { "XXXX.", "....X", "....X", ".XXX.", "....X", "....X", "XXXX." },
        ['4'] = new[] { "...X.", "..XX.", ".X.X.", "X..X.", "XXXXX", "...X.", "...X." },
        ['5'] = new[] { "XXXXX", "X....", "XXXX.", "....X", "....X", "X...X", ".XXX." },
        ['6'] = new[] { ".XXX.", "X....", "X....", "XXXX.", "X...X", "X...X", ".XXX." },
        ['7'] = new[] { "XXXXX", "....X", "...X.", "..X..", "..X..", "..X..", "..X.." },
        ['8'] = new[] { ".XXX.", "X...X", "X...X", ".XXX.", "X...X", "X...X", ".XXX." },
        ['9'] = new[] { ".XXX.", "X...X", "X...X", ".XXXX", "....X", "....X", ".XXX." },
    };
}
