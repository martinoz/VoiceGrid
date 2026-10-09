using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceGrid.Services;

public enum Intent { None, Yes, No }

/// <summary>
/// Turns what Whisper returned into a number. Handles digits ("12,5", "1 024", "-5")
/// and Czech words ("dvanáct celá pět", "pětadvacet", "dvě stě třicet", "mínus tři", "dva pět tři").
/// </summary>
public static class CzechNumberParser
{
    // All keys are lower-case without diacritics (see Normalize).
    private static readonly Dictionary<string, int> Units = new()
    {
        ["nula"] = 0, ["jedna"] = 1, ["jeden"] = 1, ["jedno"] = 1, ["jednu"] = 1,
        ["dva"] = 2, ["dve"] = 2, ["tri"] = 3, ["ctyri"] = 4, ["pet"] = 5, ["sest"] = 6,
        ["sedm"] = 7, ["osm"] = 8, ["devet"] = 9, ["deset"] = 10, ["jedenact"] = 11,
        ["dvanact"] = 12, ["trinact"] = 13, ["ctrnact"] = 14, ["patnact"] = 15,
        ["sestnact"] = 16, ["sedmnact"] = 17, ["osmnact"] = 18, ["devatenact"] = 19
    };

    private static readonly Dictionary<string, int> Tens = new()
    {
        ["dvacet"] = 20, ["tricet"] = 30, ["ctyricet"] = 40, ["padesat"] = 50,
        ["sedesat"] = 60, ["sedmdesat"] = 70, ["osmdesat"] = 80, ["devadesat"] = 90
    };

    // "petadvacet" = 25, "jednadvacet" = 21, "dvaadvacet" = 22 ...
    private static readonly Dictionary<string, int> Compound = BuildCompound();

    private static readonly HashSet<string> DecimalWords = new() { "cela", "cele", "celych", "carka", "tecka", "bod" };
    private static readonly HashSet<string> YesWords = new() { "ano", "jo", "jojo", "ok", "okej", "potvrdit", "potvrzuji", "spravne", "sedi", "dobre", "presne" };
    private static readonly HashSet<string> NoWords = new() { "ne", "nene", "spatne", "znovu", "opakovat", "zrusit", "stornovat", "nesedi" };

    private static Dictionary<string, int> BuildCompound()
    {
        var d = new Dictionary<string, int>();
        foreach (var (uk, uv) in Units.Where(k => k.Value is >= 1 and <= 9))
            foreach (var (tk, tv) in Tens)
            {
                d[uk + "a" + tk] = uv + tv;
                if (uk == "jedna") d[uk + tk] = uv + tv;
            }
        return d;
    }

    public static string Normalize(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC)
            .Replace('\u2212', '-').Replace('\u00A0', ' ').Replace('\u202F', ' ');
    }

    public static Intent GetIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Intent.None;
        var toks = Regex.Matches(Normalize(text), "[a-z]+").Select(m => m.Value).ToList();
        if (toks.Any(NoWords.Contains)) return Intent.No;     // "no" wins over "yes" - safer
        if (toks.Any(YesWords.Contains)) return Intent.Yes;
        return Intent.None;
    }

    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var norm = Normalize(text);
        bool neg = Regex.IsMatch(norm, @"\b(minus|zaporn\w*)\b") || Regex.IsMatch(norm, @"(^|\s)-\s*\d");

        // ---- digits path -------------------------------------------------
        if (norm.Any(char.IsDigit))
        {
            var n = Regex.Replace(norm, @"\b(cela|cele|celych|carka|tecka|bod)\b", ",");
            n = Regex.Replace(n, @"\s*,\s*", ",");
            n = Regex.Replace(n, @"(?<=\d)\s(?=\d{3}\b)", "");   // "1 024" -> "1024"
            var m = Regex.Match(n, @"\d+(?:[.,]\d+)?");
            if (!m.Success) return false;
            value = double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (neg) value = -value;
            return true;
        }

        // ---- words path --------------------------------------------------
        var toks = Regex.Matches(norm, "[a-z]+").Select(m => m.Value).ToList();
        int sep = toks.FindIndex(DecimalWords.Contains);
        var intToks = sep >= 0 ? toks.Take(sep).ToList() : toks;
        var fracToks = sep >= 0 ? toks.Skip(sep + 1).ToList() : new List<string>();

        string intPart;
        if (TryWordsToInt(intToks, out var iv)) intPart = iv.ToString(CultureInfo.InvariantCulture);
        else if (sep >= 0) intPart = "0";                       // "čárka pět" -> 0,5
        else return false;

        string fracPart = "";
        if (sep >= 0 && TryWordsToInt(fracToks, out var fv, keepLeadingZeros: true, out var fdigits))
            fracPart = fdigits ?? fv.ToString(CultureInfo.InvariantCulture);

        value = double.Parse(fracPart.Length > 0 ? $"{intPart}.{fracPart}" : intPart, CultureInfo.InvariantCulture);
        if (neg) value = -value;
        return true;
    }

    private static bool IsNumToken(string t) =>
        Units.ContainsKey(t) || Tens.ContainsKey(t) || Compound.ContainsKey(t) ||
        t is "sto" or "ste" or "sta" or "set" || t.StartsWith("tisic") || TryHundredCompound(t, out _);

    private static bool TryHundredCompound(string t, out int hundreds)
    {
        hundreds = 0;
        foreach (var suf in new[] { "ste", "sta", "sto", "set" })
            if (t.Length > suf.Length && t.EndsWith(suf) &&
                Units.TryGetValue(t[..^suf.Length], out var u) && u is >= 1 and <= 9)
            { hundreds = u * 100; return true; }
        return false;
    }

    private static bool TryWordsToInt(List<string> toks, out long result)
        => TryWordsToInt(toks, out result, false, out _);

    private static bool TryWordsToInt(List<string> toks, out long result, bool keepLeadingZeros, out string? digits)
    {
        result = 0; digits = null;
        var nums = toks.Where(IsNumToken).ToList();
        if (nums.Count == 0) return false;

        // digit-by-digit dictation: "dva pět tři" -> 253, "nula pět" -> 05
        if ((nums.Count >= 2 || keepLeadingZeros) &&
            nums.All(t => Units.TryGetValue(t, out var u) && u <= 9) && nums.Count <= 15)
        {
            digits = string.Concat(nums.Select(t => Units[t]));
            result = long.Parse(digits, CultureInfo.InvariantCulture);
            return true;
        }

        long total = 0, cur = 0;
        foreach (var t in nums)
        {
            if (Units.TryGetValue(t, out var u)) cur += u;
            else if (Tens.TryGetValue(t, out var te)) cur += te;
            else if (Compound.TryGetValue(t, out var cv)) cur += cv;
            else if (t == "sto") cur += 100;
            else if (t is "ste" or "sta" or "set") cur = cur is >= 1 and <= 9 ? cur * 100 : cur + 100;
            else if (TryHundredCompound(t, out var h)) cur += h;
            else if (t.StartsWith("tisic")) { total += (cur == 0 ? 1 : cur) * 1000; cur = 0; }
        }
        result = total + cur;
        return true;
    }
}

/// <summary>Number -> Czech words, used for the read-back (decimals are read digit by digit).</summary>
public static class CzechNumberWords
{
    private static readonly string[] U =
    {
        "nula", "jedna", "dva", "tři", "čtyři", "pět", "šest", "sedm", "osm", "devět", "deset",
        "jedenáct", "dvanáct", "třináct", "čtrnáct", "patnáct", "šestnáct", "sedmnáct", "osmnáct", "devatenáct"
    };
    private static readonly string[] T =
        { "", "", "dvacet", "třicet", "čtyřicet", "padesát", "šedesát", "sedmdesát", "osmdesát", "devadesát" };

    public static string ToSpoken(double v)
    {
        bool neg = v < 0; v = Math.Abs(v);
        var s = Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        var parts = s.Split('.');
        long ip = long.Parse(parts[0], CultureInfo.InvariantCulture);
        if (ip >= 1_000_000) return (neg ? "mínus " : "") + s.Replace('.', ',');

        var words = Int(ip);
        if (parts.Length > 1) words += " celá " + string.Join(' ', parts[1].Select(c => U[c - '0']));
        return (neg ? "mínus " : "") + words;
    }

    private static string Int(long n)
    {
        if (n < 20) return U[n];
        if (n < 100) return T[n / 10] + (n % 10 > 0 ? " " + U[n % 10] : "");
        if (n < 1000)
        {
            long h = n / 100;
            var hs = h == 1 ? "sto" : h == 2 ? "dvě stě" : h < 5 ? U[h] + " sta" : U[h] + " set";
            return hs + (n % 100 > 0 ? " " + Int(n % 100) : "");
        }
        long th = n / 1000;
        var ts = th == 1 ? "tisíc" : th is >= 2 and <= 4 ? Int(th) + " tisíce" : Int(th) + " tisíc";
        return ts + (n % 1000 > 0 ? " " + Int(n % 1000) : "");
    }
}
