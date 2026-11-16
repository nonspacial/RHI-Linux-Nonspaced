using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RHI.Core.Services;

/// <summary>
/// #299 (upstream 2.9.3 <c>FilterViewModel</c> game search, "like Steam's"): the name matching and ordering rules the
/// library search uses. Pure; names are worked out once each and cached.
/// <list type="bullet">
/// <item>punctuation and spaces ignored: "fear" finds F.E.A.R., "half life" finds Half-Life 2;</item>
/// <item>Roman numerals as numbers: "gta 3" / "grand theft auto 3" find Grand Theft Auto III;</item>
/// <item>initials: "gta", "ff7", "hl2"; three or more characters also match part of the initials ("mw3");</item>
/// <item>any word order: "batman lego" finds LEGO Batman (each typed word must match the name somewhere);</item>
/// <item>typos: a typed word of 5+ letters may be 2 letters off a word of the name, 4 letters 1 off, shorter none;</item>
/// <item>tags (store, engine, graphics API, author…) match whole words only (<see cref="TagMatches"/>).</item>
/// </list>
/// </summary>
public static class GameSearch
{
    /// <summary>Rank of a game found through something other than its name (<see cref="NameRank"/>).</summary>
    public const int NotTheName = 6;

    /// <summary>A name's search forms, worked out once per name.</summary>
    internal sealed record SearchForm(string Plain, string Numbered, string Initials, string RawInitials, IReadOnlyList<string> Words);

    private static readonly ConcurrentDictionary<string, SearchForm> Forms = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string[]> TagWords = new(StringComparer.Ordinal);

    private static readonly Regex RomanNumeral = new(@"^(X{0,3})(IX|IV|V?I{0,3})$", RegexOptions.CultureInvariant);

    /// <summary>Letters and digits only, lower case: "F.E.A.R. 3" → "fear3", "Half-Life 2" → "halflife2".</summary>
    public static string LettersAndDigits(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// The looser name match on top of the plain "contains" search: punctuation-free, Roman numerals, initials, any word
    /// order and typos (see the class summary). A search of fewer than two letters or digits never matches here.
    /// </summary>
    public static bool NameMatchesLoosely(string gameName, string query)
    {
        var q = LettersAndDigits(query);
        if (q.Length < 2 || string.IsNullOrEmpty(gameName)) return false;
        var f = FormsOf(gameName);

        // The whole query as one piece
        if (f.Plain.Contains(q, StringComparison.Ordinal) || f.Numbered.Contains(q, StringComparison.Ordinal) || InitialsMatch(f, q))
            return true;

        // Word by word, any order: every typed word has to match
        var tokens = SplitWords(query).Select(w => w.ToLowerInvariant()).ToList();
        if (tokens.Count == 0) return false;
        foreach (var t in tokens)
            if (!TokenMatches(f, t)) return false;
        return true;
    }

    /// <summary>
    /// The name matches the search properly: the whole name ("sifu" → Sifu, punctuation ignored), or every typed word is a
    /// whole word of the name ("clone" → Clone Hero, "resident evil 4"). When any shown game matches this way, games that
    /// only matched by a near miss or part of a word are dropped (Sifu no longer brings up PC Building Simulator).
    /// </summary>
    public static bool NameMatchesWholeWords(string gameName, string query)
    {
        var q = LettersAndDigits(query);
        if (q.Length == 0 || string.IsNullOrEmpty(gameName)) return false;
        var f = FormsOf(gameName);
        if (f.Plain == q || f.Numbered == q) return true;

        var tokens = SplitWords(query);
        if (tokens.Count == 0) return false;
        var rawWords = SplitWords(gameName);
        foreach (var t in tokens)
        {
            var found = f.Words.Any(w => w.Equals(t, StringComparison.OrdinalIgnoreCase))
                        || rawWords.Any(w => w.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (!found) return false;
        }
        return true;
    }

    /// <summary>The plain search (part of the name as typed, any case) or the looser one.</summary>
    public static bool NameMatches(string gameName, string query)
        => !string.IsNullOrEmpty(gameName)
           && (gameName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) || NameMatchesLoosely(gameName, query));

    /// <summary>
    /// How well a game's name matches the search, for ordering results (lower is better, like Steam): 0 the whole name,
    /// 1 the name starts with it, 2 whole words of the name, 3 part of the name, 4 the initials, 5 a near miss (typo,
    /// words in another order), <see cref="NotTheName"/> not the name (store, engine, author…).
    /// </summary>
    public static int NameRank(string gameName, string query)
    {
        var q = LettersAndDigits(query);
        if (q.Length == 0 || string.IsNullOrEmpty(gameName)) return NotTheName;
        var f = FormsOf(gameName);
        if (f.Plain == q || f.Numbered == q) return 0;
        if (f.Plain.StartsWith(q, StringComparison.Ordinal) || f.Numbered.StartsWith(q, StringComparison.Ordinal)) return 1;
        if (NameMatchesWholeWords(gameName, query)) return 2;
        if (gameName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || f.Plain.Contains(q, StringComparison.Ordinal) || f.Numbered.Contains(q, StringComparison.Ordinal)) return 3;
        if (InitialsMatch(f, q)) return 4;
        return NameMatchesLoosely(gameName, query) ? 5 : NotTheName;
    }

    /// <summary>
    /// A tag (store, engine, graphics API, author…) matches only on whole words, so the first letters of a game's name
    /// don't bring up every game on that store or engine ("st" isn't Steam, "un" isn't Unreal). Spaces and punctuation
    /// are ignored ("dx12", "32bit", "uw fix", "unreal engine"). <paramref name="q"/> is the search as
    /// <see cref="LettersAndDigits"/>.
    /// </summary>
    public static bool TagMatches(string? tag, string q)
    {
        if (string.IsNullOrEmpty(tag) || q.Length == 0) return false;
        var words = TagWords.GetOrAdd(tag, t => SplitWords(t).Select(LettersAndDigits).Where(w => w.Length > 0).ToArray());
        for (var i = 0; i < words.Length; i++)
        {
            // A run of whole words starting here, joined: "unreal" + "engine" for "unreal engine"
            var len = 0;
            for (var j = i; j < words.Length; j++)
            {
                if (len + words[j].Length > q.Length
                    || string.CompareOrdinal(q, len, words[j], 0, words[j].Length) != 0) break;
                len += words[j].Length;
                if (len == q.Length) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Letters changed, added, removed or swapped with a neighbour to turn <paramref name="a"/> into <paramref name="b"/>
    /// (optimal string alignment). Stops early past <paramref name="max"/> (returns max + 1).
    /// </summary>
    internal static int TypoDistance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev2 = new int[b.Length + 1];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > max) return max + 1;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }

    private static SearchForm FormsOf(string name) => Forms.GetOrAdd(name, SearchForms);

    /// <summary>Initials: two characters from the start ("re"); three or more anywhere ("mw3").</summary>
    private static bool InitialsMatch(SearchForm f, string q) => q.Length >= 3
        ? f.Initials.Contains(q, StringComparison.Ordinal) || f.RawInitials.Contains(q, StringComparison.Ordinal)
        : f.Initials.StartsWith(q, StringComparison.Ordinal) || f.RawInitials.StartsWith(q, StringComparison.Ordinal);

    /// <summary>One typed word against a name: part of it, its initials, or a near miss of one of its words.</summary>
    private static bool TokenMatches(SearchForm f, string t)
    {
        if (f.Plain.Contains(t, StringComparison.Ordinal) || f.Numbered.Contains(t, StringComparison.Ordinal)) return true;
        if (t.Length >= 2 && InitialsMatch(f, t)) return true;

        var allowed = t.Length >= 5 ? 2 : t.Length == 4 ? 1 : 0;
        if (allowed == 0) return false;
        foreach (var w in f.Words)
        {
            if (Math.Abs(w.Length - t.Length) <= allowed && TypoDistance(t, w, allowed) <= allowed) return true;
            // Still typing: compare with the start of a longer word ("residnt" → "resident")
            if (w.Length > t.Length && TypoDistance(t, w[..t.Length], allowed) <= allowed) return true;
        }
        return false;
    }

    /// <summary>Words of a name or query: apostrophes join ("Marvel's"), every other non-letter / digit separates.</summary>
    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) current.Append(ch);
            else if (ch is '\'' or '’') continue;
            else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// <summary>
    /// A name's search forms: letters + digits; the same with Roman numerals as numbers; initials with numbers ("gta3",
    /// "ff7"); initials with numerals as written ("gtaiii", "ffx"); its words (lower case, numerals as numbers).
    /// </summary>
    internal static SearchForm SearchForms(string name)
    {
        var words = SplitWords(name);
        var numbered = new StringBuilder();
        var initials = new StringBuilder();
        var rawInitials = new StringBuilder();
        var lowerWords = new List<string>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var digits = w.All(char.IsDigit);
            var number = digits ? w : RomanValue(w, isLast: i == words.Count - 1);
            numbered.Append(number ?? w.ToLowerInvariant());
            initials.Append(number ?? char.ToLowerInvariant(w[0]).ToString());
            rawInitials.Append(number is not null ? w.ToLowerInvariant() : char.ToLowerInvariant(w[0]).ToString());
            lowerWords.Add(number ?? w.ToLowerInvariant());
        }
        return new SearchForm(LettersAndDigits(name), numbered.ToString(), initials.ToString(), rawInitials.ToString(), lowerWords);
    }

    /// <summary>
    /// "III" → "3" for a word written as a capital Roman numeral (I to XXXIX). A lone "I" only at the end of the name
    /// ("I Am Bread" isn't "1 Am Bread").
    /// </summary>
    private static string? RomanValue(string word, bool isLast)
    {
        if (word.Length == 0 || word != word.ToUpperInvariant() || !RomanNumeral.IsMatch(word)) return null;
        if (word == "I" && !isLast) return null;
        var total = 0;
        for (var i = 0; i < word.Length; i++)
        {
            var v = Digit(word[i]);
            var next = i + 1 < word.Length ? Digit(word[i + 1]) : 0;
            total += v < next ? -v : v;
        }
        return total > 0 ? total.ToString(CultureInfo.InvariantCulture) : null;

        static int Digit(char c) => c switch { 'I' => 1, 'V' => 5, 'X' => 10, _ => 0 };
    }
}
