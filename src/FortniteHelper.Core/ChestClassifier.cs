namespace FortniteHelper.Core;

/// <summary>
/// Decides whether OCR text from around the crosshair is a chest / ammo box interaction prompt.
/// OCR on game HUD text is noisy ("Chcst", "CHEST", "Ch est"), so words are matched with a small edit distance.
/// </summary>
public sealed class ChestClassifier
{
    readonly List<(ContainerKind Kind, string[] Words)> _phrases = [];

    public ChestClassifier(IEnumerable<string> chestKeywords, IEnumerable<string> ammoKeywords)
        : this(chestKeywords, ammoKeywords, []) { }

    /// <param name="searchKeywords">Generic container prompts ("Search") - Fortnite shows the same prompt for chests and ammo boxes.</param>
    public ChestClassifier(IEnumerable<string> chestKeywords, IEnumerable<string> ammoKeywords, IEnumerable<string> searchKeywords)
    {
        foreach (var k in chestKeywords) Add(ContainerKind.Chest, k);
        foreach (var k in ammoKeywords) Add(ContainerKind.AmmoBox, k);
        // Longer phrases first so "ammo box" wins over a lone "box" etc. (stable: chest before ammo on ties)
        var named = _phrases.OrderByDescending(p => p.Words.Length).ToList();
        _phrases.Clear();
        _phrases.AddRange(named);
        // A named container always wins over the generic "Search".
        foreach (var k in searchKeywords) Add(ContainerKind.Container, k);
    }

    void Add(ContainerKind kind, string phrase)
    {
        var words = Tokenize(phrase);
        if (words.Length > 0) _phrases.Add((kind, words));
    }

    public static string[] Tokenize(string text)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            char c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z') cur.Append(c);
            else if (c == '0') cur.Append('o'); // OCR often reads O as 0 in caps fonts
            else if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens.ToArray();
    }

    public ContainerKind? Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = Tokenize(text);
        if (tokens.Length == 0) return null;

        // Also try each pair of adjacent tokens joined, for split words like "Ch est".
        var joined = new string[Math.Max(0, tokens.Length - 1)];
        for (int i = 0; i + 1 < tokens.Length; i++) joined[i] = tokens[i] + tokens[i + 1];

        foreach (var (kind, words) in _phrases)
        {
            if (words.Length == 1)
            {
                if (tokens.Any(t => WordMatches(t, words[0])) || joined.Any(t => WordMatches(t, words[0], strict: true)))
                    return kind;
                continue;
            }
            for (int i = 0; i + words.Length <= tokens.Length; i++)
            {
                bool all = true;
                for (int w = 0; w < words.Length && all; w++) all = WordMatches(tokens[i + w], words[w]);
                if (all) return kind;
            }
            // Words glued together by OCR: "ammobox"
            var glued = string.Concat(words);
            if (tokens.Any(t => WordMatches(t, glued))) return kind;
        }
        return null;
    }

    static bool WordMatches(string token, string word, bool strict = false)
    {
        if (token == word) return true;
        if (strict || word.Length < 5) return false;
        // Allow a plural ("chests") or one OCR mistake on words of 5+ letters.
        if (token.Length == word.Length + 1 && token.StartsWith(word, StringComparison.Ordinal)) return true;
        return Math.Abs(token.Length - word.Length) <= 1 && Distance(token, word) <= 1;
    }

    static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
