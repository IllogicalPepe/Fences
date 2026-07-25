using System.Text.RegularExpressions;
using GEmojiSharp;

namespace FenceDesk.Services;

/// <summary>
/// Converts emoji phrases and shortcodes in user-entered text into Unicode emoji.
/// Examples: "fingers crossed emoji" → 🤞, ":tada:" → 🎉, "fire emoji" → 🔥
/// </summary>
public static partial class EmojiTextConverter
{
    [GeneratedRegex(@"\b([a-zA-Z0-9]+(?:[ \-][a-zA-Z0-9]+)*)\s+[Ee][Mm][Oo][Jj][Ii]\b", RegexOptions.CultureInvariant)]
    private static partial Regex PhraseEmojiRegex();

    public static string Convert(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? string.Empty;

        // :shortcode: → emoji (e.g. ":tada:" → 🎉)
        var result = Emoji.Emojify(text);

        // "fingers crossed emoji" / "fire emoji" → emoji (anywhere in the string)
        return PhraseEmojiRegex().Replace(result, match =>
        {
            var resolved = ResolvePhrase(match.Groups[1].Value);
            return resolved ?? match.Value;
        });
    }

    private static string? ResolvePhrase(string phrase)
    {
        phrase = phrase.Trim();
        if (phrase.Length == 0)
            return null;

        foreach (var candidate in Candidates(phrase))
        {
            var hit = ResolveExact(candidate);
            if (hit is not null)
                return hit;
        }

        // Ambiguous Find() results only when the query itself is specific enough
        // (exact description / alias already covered above).
        return null;
    }

    private static IEnumerable<string> Candidates(string phrase)
    {
        yield return phrase;

        var spaced = phrase.Replace('-', ' ').Replace('_', ' ');
        if (!string.Equals(spaced, phrase, StringComparison.OrdinalIgnoreCase))
            yield return spaced;

        var underscored = phrase.Replace(' ', '_').Replace('-', '_');
        if (!string.Equals(underscored, phrase, StringComparison.OrdinalIgnoreCase))
            yield return underscored;

        // "fingers crossed" ↔ "crossed fingers"
        var words = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length >= 2)
        {
            var reversed = string.Join(' ', words.Reverse());
            yield return reversed;
            yield return string.Join('_', words.Reverse());
        }
    }

    private static string? ResolveExact(string query)
    {
        var aliasKey = query.Replace(' ', '_').Replace('-', '_').ToLowerInvariant();
        var byAlias = Emoji.Get(":" + aliasKey + ":");
        if (!string.IsNullOrEmpty(byAlias.Raw))
            return byAlias.Raw;

        foreach (var emoji in Emoji.Find(query))
        {
            if (string.IsNullOrEmpty(emoji.Raw))
                continue;

            if (string.Equals(emoji.Description, query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(emoji.Description?.Replace('-', ' '), query.Replace('_', ' ').Replace('-', ' '), StringComparison.OrdinalIgnoreCase))
                return emoji.Raw;

            if (emoji.Aliases is { Length: > 0 } &&
                emoji.Aliases.Any(a => string.Equals(a, aliasKey, StringComparison.OrdinalIgnoreCase)))
                return emoji.Raw;
        }

        // Unique Find hit (e.g. description/tag/alias substring that only matches one emoji)
        var unique = Emoji.Find(query)
            .Where(e => !string.IsNullOrEmpty(e.Raw))
            .DistinctBy(e => e.Raw)
            .Take(2)
            .ToList();
        if (unique.Count == 1)
            return unique[0].Raw;

        return null;
    }
}
