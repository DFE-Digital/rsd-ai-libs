using System.Text;

namespace GovUK.Dfe.AI.Agents.Quality;

/// <summary>
/// Tells from the start of a streamed answer whether it's a JSON object, e.g. a structured answer, holding the start
/// until it can. A JSON answer is shown whole at the end: its citations go inside its strings, which can't be done a
/// piece at a time.
/// </summary>
/// <param name="isJson">Already known to be JSON, e.g. the agent has an output schema.</param>
internal sealed class StreamedAnswerShape(bool isJson)
{
    private readonly StringBuilder _start = new();
    private bool? _isJson = isJson ? true : null;   // null: not known until the first non-white-space character

    public bool IsJson => _isJson == true;

    /// <summary>The text to pass on now: empty while it's not known yet, and always for a JSON answer.</summary>
    public string Add(string piece)
    {
        if (_isJson is null)
        {
            _start.Append(piece);
            var soFar = _start.ToString();
            if (string.IsNullOrWhiteSpace(soFar))
            {
                return string.Empty;
            }

            _isJson = Citations.StartsAsJsonObject(soFar);
            _start.Clear();
            piece = soFar;
        }

        return IsJson ? string.Empty : piece;
    }

    /// <summary>What's still held once the answer is finished: only white space, from an answer that never started.</summary>
    public string Flush()
    {
        var rest = _start.ToString();
        _start.Clear();
        return rest;
    }
}
