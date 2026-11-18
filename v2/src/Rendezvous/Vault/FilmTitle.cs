using System.Text.RegularExpressions;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// A presentable title and year from a film's file name.
/// "The.Princess.Bride.1987.REMASTERED.1080p.BluRay.H264.AAC-RARBG" becomes
/// ("The Princess Bride", 1987); "Captain America The Winter Soldier (2014)"
/// becomes ("Captain America The Winter Soldier", 2014); a name with nothing
/// to tidy comes back as it is.
/// </summary>
/// <remarks>
/// Release names follow one loose convention: title, then year, then the
/// release's technical tags. So the title is everything before the first
/// plausible year, or, with no year, everything before the first technical tag.
/// A year at the very start is kept as part of the title ("2001 A Space
/// Odyssey", "1917").
/// </remarks>
public static partial class FilmTitle
{
    public readonly record struct Result(string Title, int? Year);

    [GeneratedRegex(@"[\(\[]?\b(19[2-9]\d|20[0-4]\d)\b[\)\]]?")]
    private static partial Regex YearRx();

    [GeneratedRegex(@"\b(2160p|1080p|1080i|720p|576p|480p|4k|uhd|hdr|hdr10|dv|bluray|blu-ray|bdrip|brrip|web-?dl|webrip|web|hdrip|dvdrip|dvd|hdtv|remux|x264|x265|h\.?264|h\.?265|hevc|avc|xvid|aac|ac3|dts|ddp?5\.1|atmos|remastered|extended|unrated|uncut|proper|repack|imax|directors\.?cut|amzn|nf|dsnp|hmax)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TagRx();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    public static Result Parse(string name)
    {
        var s = name ?? "";
        // Dots and underscores stand in for spaces in release names. Only when
        // the name has no real spaces, so "Mr. Smith" keeps its full stop.
        if (!s.Contains(' ')) s = s.Replace('.', ' ').Replace('_', ' ');
        s = s.Trim();

        // Technical tags end the title whatever else happens.
        var tag = TagRx().Match(s);
        var end = tag.Success && tag.Index > 0 ? tag.Index : s.Length;

        // The release year is the LAST year before the tags, so a year that is
        // part of the title survives: "Blade Runner 2049 2017 2160p". A year
        // that IS the start of the name belongs to the title too ("1917").
        int? year = null;
        var cut = end;
        foreach (Match m in YearRx().Matches(s[..end]))
        {
            var y = int.Parse(m.Groups[1].Value);
            // Not a release year if it is still in the future ("Blade Runner 2049").
            if (m.Index == 0 || y > DateTime.UtcNow.Year + 1) continue;
            year = y;
            cut = m.Index;
        }

        var title = Spaces().Replace(s[..cut], " ").Trim(' ', '-', '(', '[', ',');
        if (title.Length == 0) return new Result(name ?? "", year);
        return new Result(title, year);
    }
}
