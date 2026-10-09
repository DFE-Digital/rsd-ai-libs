using System.Text;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>
/// Cleans a streamed answer as <see cref="AnswerLinks"/> cleans a whole one. Text is passed on as soon as it's safe; from
/// where an image, link or HTML tag might still be being written, it waits for the rest, so an image never reaches the
/// page before it can be removed.
/// </summary>
internal sealed class StreamedAnswerCleaner
{
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
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '!' or '[' or '<' && !IsFinished(text, i))
            {
                return i;
            }
        }

        return text.Length;
    }

    private static bool IsFinished(string text, int start)
    {
        switch (text[start])
        {
            case '<':
                return text.IndexOf('>', start) >= 0;
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
