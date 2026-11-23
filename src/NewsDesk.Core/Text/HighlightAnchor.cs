namespace NewsDesk.Core.Text;

/// <summary>
/// Locates a saved highlight in (possibly changed) article text. A highlight stores its quote and the offset where it
/// was made; if the text changed, the closest exact occurrence of the quote is used. If the quote no longer occurs,
/// the highlight is reported as referring to older content and keeps its original quote.
/// </summary>
public static class HighlightAnchor
{
    public const int MaxQuoteLength = 2000;

    /// <summary>Returns the start offset of <paramref name="quote"/> in <paramref name="text"/>, or null if it no longer occurs.</summary>
    public static int? Resolve(string text, string quote, int originalStart)
    {
        if (quote.Length == 0 || quote.Length > text.Length) return null;
        if (originalStart >= 0 && originalStart + quote.Length <= text.Length &&
            string.CompareOrdinal(text, originalStart, quote, 0, quote.Length) == 0)
            return originalStart;

        int? best = null;
        var index = text.IndexOf(quote, StringComparison.Ordinal);
        while (index >= 0)
        {
            if (best is null || Math.Abs(index - originalStart) < Math.Abs(best.Value - originalStart)) best = index;
            index = text.IndexOf(quote, index + 1, StringComparison.Ordinal);
        }
        return best;
    }

    /// <summary>
    /// Normalizes a user selection: trims surrounding whitespace (adjusting the offset) and rejects empty or oversized selections.
    /// </summary>
    public static (int Start, string Quote)? Normalize(string text, int start, int length)
    {
        if (start < 0 || length <= 0 || start + length > text.Length) return null;
        var end = start + length;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (end <= start || end - start > MaxQuoteLength) return null;
        return (start, text[start..end]);
    }
}
