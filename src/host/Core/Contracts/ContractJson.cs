using System.Text.Json;
using System.Text.Json.Serialization;

namespace ButtonBox.Core.Contracts;

/// <summary>
/// The one set of serializer options for every profile, layout, catalog and protocol message.
/// The generated types depend on these options, so never serialize them with anything else.
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>The widget a client message refers to, or null for messages that carry none.</summary>
    /// <remarks>Also the Core build's own use of the generated types: a schema rename breaks it at compile time.</remarks>
    public static string? WidgetId(ClientMessage message) => message switch
    {
        GestureMessage gesture => gesture.WidgetId,
        SelectMessage select => select.WidgetId,
        _ => null,
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            // Property and enum names are explicit in the generated types, so no naming policy applies.
            PropertyNamingPolicy = null,
            // JSON written by a browser may put "type" anywhere in the object.
            AllowOutOfOrderMetadataProperties = true,
            // Optional schema properties are absent, never null.
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Until schema validation exists (Phase 2), unknown fields from a client are an error.
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
