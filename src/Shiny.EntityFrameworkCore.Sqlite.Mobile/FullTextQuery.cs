using System.Text;

namespace Shiny.EntityFrameworkCore.Sqlite.Mobile;

/// <summary>Helpers for building FTS5 query strings.</summary>
public static class FullTextQuery
{
    /// <summary>
    /// Turns raw user input (a search box) into a safe FTS5 query: every whitespace-separated word becomes a quoted
    /// term, so punctuation and FTS5 operators in the input (<c>AND</c>, <c>*</c>, <c>"</c>, <c>:</c>, <c>-</c>) are
    /// searched for literally instead of causing a syntax error. Terms are implicitly AND-ed. Blank input yields an
    /// empty phrase, which matches nothing.
    /// </summary>
    /// <param name="text">The user's text.</param>
    /// <param name="prefixLastTerm">Make the last word a prefix search (search-as-you-type).</param>
    public static string Escape(string? text, bool prefixLastTerm = false)
    {
        var terms = (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return "\"\"";

        var sb = new StringBuilder();
        for (var i = 0; i < terms.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');

            sb.Append('"').Append(terms[i].Replace("\"", "\"\"")).Append('"');
            if (prefixLastTerm && i == terms.Length - 1)
                sb.Append('*');
        }
        return sb.ToString();
    }
}
