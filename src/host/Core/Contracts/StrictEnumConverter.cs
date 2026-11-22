using System.Text.Json.Serialization;

namespace ButtonBox.Core.Contracts;

/// <summary>
/// String-only enum converter for the generated contracts. The default <see cref="JsonStringEnumConverter{TEnum}"/>
/// also accepts integers, so <c>"gesture":3</c> would pass as a gesture although the schema allows only names.
/// SchemaGen points every generated enum property at this converter.
/// </summary>
public sealed class StrictEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;
