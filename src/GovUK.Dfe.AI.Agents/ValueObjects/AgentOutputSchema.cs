using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace GovUK.Dfe.AI.Agents.ValueObjects;

/// <summary>A JSON schema the agent's answer must follow.</summary>
/// <param name="Name">A short name for the format, e.g. "ofsted_findings" (letters, digits, '_' and '-').</param>
/// <param name="Description">What the output is, to help the model.</param>
public sealed record AgentOutputSchema(string Name, string JsonSchema, string? Description = null)
{
    // The schema exporter needs a type resolver; the same options read answers back, so names match.
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// A strict schema for <typeparamref name="T"/>: in every object, at any depth, every property is required and no others
    /// are allowed, as Foundry's strict structured output requires.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> holds a dictionary: its keys aren't known in advance, so it can't be strict. Use a list of
    /// records with a key property instead.
    /// </exception>
    public static AgentOutputSchema For<T>(string name, string? description = null)
    {
        var schema = WebOptions.GetJsonSchemaAsNode(typeof(T), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) => Strict(typeof(T), context, node),
        });

        return new AgentOutputSchema(name, schema.ToJsonString(), description);
    }

    /// <summary>
    /// Closes an object node: its properties all required, no others allowed. An object with no properties, e.g. an empty
    /// record, is closed as it is. One whose extra properties are open, i.e. a dictionary, is refused.
    /// </summary>
    private static JsonNode Strict(Type root, JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject obj || !IsObject(obj))
        {
            return node;
        }

        if (obj["properties"] is not JsonObject properties)
        {
            if (AllowsExtraProperties(obj["additionalProperties"]))
            {
                throw new NotSupportedException(string.Format(Constants.ErrorMessages.SchemaHasDictionary, root.Name,
                    Where(context), context.TypeInfo.Type.Name));
            }

            properties = [];
            obj["properties"] = properties;
        }

        obj["additionalProperties"] = false;
        obj["required"] = new JsonArray([.. properties.Select(property => (JsonNode)property.Key)]);
        return node;
    }

    /// <summary>Whether <c>additionalProperties</c> lets other properties in: anything set but <c>false</c>, e.g. a dictionary's value schema.</summary>
    private static bool AllowsExtraProperties(JsonNode? additionalProperties)
        => additionalProperties is not null
           && !(additionalProperties is JsonValue value && value.TryGetValue<bool>(out var allowed) && !allowed);

    /// <summary>Whether the node's type is "object", alone or with "null".</summary>
    private static bool IsObject(JsonObject node) => node["type"] switch
    {
        JsonValue type => type.TryGetValue<string>(out var name) && name == "object",
        JsonArray types => types.Any(static type => type is JsonValue value && value.TryGetValue<string>(out var name) && name == "object"),
        _ => false,
    };

    /// <summary>Where in the schema the node is, e.g. "#/properties/scores", for the error.</summary>
    private static string Where(JsonSchemaExporterContext context)
        => context.Path.IsEmpty ? "#" : "#/" + string.Join('/', context.Path.ToArray());

    internal static JsonSerializerOptions SerializerOptions => WebOptions;
}
