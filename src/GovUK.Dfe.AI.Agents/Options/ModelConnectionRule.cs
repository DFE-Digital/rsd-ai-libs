namespace GovUK.Dfe.AI.Agents.Options;

/// <summary>
/// When <c>Foundry:DefaultModel</c> is <c>&lt;connection&gt;/&lt;model&gt;</c>, every model must be on that connection, so
/// every model call goes through the connection the app chose, e.g. an API Management gateway.
/// </summary>
internal static class ModelConnectionRule
{
    /// <summary>The connection in <c>&lt;connection&gt;/&lt;model&gt;</c>, or null for a deployment named directly.</summary>
    public static string? ConnectionOf(string? model)
    {
        var separator = model?.IndexOf('/') ?? -1;
        return separator > 0 && separator < model!.Length - 1 ? model[..separator] : null;
    }

    /// <summary>Any model when <paramref name="connection"/> is null; else only <c>&lt;connection&gt;/&lt;model&gt;</c>.</summary>
    public static bool Allows(string? connection, string? model)
        => connection is null || string.Equals(ConnectionOf(model), connection, StringComparison.OrdinalIgnoreCase);
}
