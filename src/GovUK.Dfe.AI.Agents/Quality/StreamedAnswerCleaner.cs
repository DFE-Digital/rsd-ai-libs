using System.Buffers;
using System.Text;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>
/// Cleans a streamed answer as <see cref="AnswerLinks"/> cleans a whole one. Text is passed on as soon as it's safe; from
/// where an image, link or HTML tag might still be being written, it waits for the rest, so an image never reaches the
/// page before it can be removed.
/// </summary>
internal sealed class StreamedAnswerCleaner
{
    /// <summary>Where an image, link or HTML tag can start.</summary>
    private static readonly SearchValues<char> SpanStarts = SearchValues.Create("![<");

    private readonly StringBuilder _pending = new();

    /// <summary>The cleaned text that's safe to show now; empty while it waits.</summary>
    public string Add(string text)
    {
        _pending.Append(text);
        var pending = _pending.ToString();
        var safe = SafeLength(pending);
        if (safe == 0)
        {
            return string.Empty;
        }

        _pending.Remove(0, safe);
        return AnswerLinks.Clean(pending[..safe])!;
    }

    /// <summary>Everything still waiting, cleaned, once the answer is finished.</summary>
    public string Flush()
    {
        var rest = AnswerLinks.Clean(_pending.ToString())!;
        _pending.Clear();
        return rest;
    }

    /// <summary>The length up to the first image, link or tag that isn't finished yet.</summary>
    private static int SafeLength(string text)
    {
        var position = 0;
        while (text.AsSpan(position).IndexOfAny(SpanStarts) is var offset and >= 0)
        {
            var start = position + offset;
            if (!IsFinished(text, start))
            {
                return start;
            }

            // A finished image, link or tag is skipped whole, so a "<" inside it can't split it.
            position = Math.Max(start + 1, SpanEnd(text, start));
        }

        return text.Length;
    }

    /// <summary>The index just after the finished image, link or tag starting at <paramref name="start"/>, or <paramref name="start"/> if there isn't one.</summary>
    private static int SpanEnd(string text, int start) => text[start] switch
    {
        '<' => TagEnd(text, start),
        '!' => start + 1 < text.Length && text[start + 1] == '[' ? LinkEnd(text, start + 1) : start,
        _ => LinkEnd(text, start),
    };

    private static int TagEnd(string text, int start)
    {
        if (start + 1 >= text.Length || !IsTagStart(text[start + 1]))
        {
            return start;
        }

        var close = text.IndexOf('>', start);
        return close >= 0 ? close + 1 : start;
    }

    private static int LinkEnd(string text, int start)
    {
        var bracket = text.IndexOf(']', start);
        if (bracket < 0 || bracket == text.Length - 1 || text[bracket + 1] != '(')
        {
            return start;
        }

        var paren = text.IndexOf(')', bracket);
        return paren >= 0 ? paren + 1 : start;
    }

    private static bool IsTagStart(char next) => char.IsAsciiLetter(next) || next is '/' or '!' or '?';

    private static bool IsFinished(string text, int start)
    {
        switch (text[start])
        {
            case '<':
                // Only a "<" before a letter, "/", "!" or "?" starts a tag; at the very end, the next character is still to come.
                return start + 1 < text.Length && (!IsTagStart(text[start + 1]) || text.IndexOf('>', start) >= 0);
            case '!':
                // "!" ends a sentence unless "[" follows; at the very end it might still become an image.
                return start + 1 < text.Length && (text[start + 1] != '[' || IsFinished(text, start + 1));
            default:
                var close = text.IndexOf(']', start);
                if (close < 0 || close == text.Length - 1)
                {
                    return false;   // "[...": the text, or a "(" after it, is still to come
                }

                return text[close + 1] != '(' || text.IndexOf(')', close) >= 0;
        }
    }
}
