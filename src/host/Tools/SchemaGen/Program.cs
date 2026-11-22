using System.Text.RegularExpressions;
using NJsonSchema;
using NJsonSchema.CodeGeneration;
using NJsonSchema.CodeGeneration.CSharp;
using NJsonSchema.References;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: SchemaGen <schemasDir> <outputFile> <namespace>");
    return 2;
}

var (schemasDir, outputFile, ns) = (args[0], args[1], args[2]);

var settings = new CSharpGeneratorSettings
{
    Namespace = ns,
    JsonLibrary = CSharpJsonLibrary.SystemTextJson,
    TypeNameGenerator = new TitleTypeNameGenerator(),
    ClassStyle = CSharpClassStyle.Poco,
    GenerateNullableReferenceTypes = true,
    GenerateDataAnnotations = false,
    UseRequiredKeyword = true,
    GenerateOptionalPropertiesAsNullable = true,
    DateType = "System.DateTimeOffset",
    ArrayType = "System.Collections.Generic.IReadOnlyList",
    ArrayInstanceType = "System.Collections.Generic.List",
    DictionaryType = "System.Collections.Generic.IReadOnlyDictionary",
    DictionaryInstanceType = "System.Collections.Generic.Dictionary",
    JsonPolymorphicSerializationStyle = CSharpJsonPolymorphicSerializationStyle.SystemTextJson,
    ArrayBaseType = "System.Collections.Generic.List",
    DictionaryBaseType = "System.Collections.Generic.Dictionary",
};

// All files are loaded through one cache, so a definition referenced from several files is a single
// schema instance and is emitted once.
var cache = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
var files = Directory.GetFiles(schemasDir, "*.schema.json").Select(Path.GetFullPath)
    .OrderBy(f => f, StringComparer.Ordinal).ToList();
foreach (var file in files)
{
    await SharedResolver.LoadAsync(file, cache);
}

var root = new JsonSchema();
foreach (var file in files)
{
    var schema = cache[file];
    var name = Path.GetFileName(file).Split('.')[0];
    if (schema.Type.HasFlag(JsonObjectType.Object))
    {
        root.Definitions[name] = schema;
    }
    foreach (var (key, definition) in schema.Definitions)
    {
        root.Definitions[$"{name}.{key}"] = definition;
    }
}

// A oneOf-only union wrapper (e.g. Widget) is C#-wise just its discriminated base class: alias it.
foreach (var (key, wrapper) in root.Definitions.Where(d => d.Value.OneOf.Count > 0 && d.Value.Properties.Count == 0).ToList())
{
    var bases = wrapper.OneOf
        .Select(v => v.ActualSchema.AllOf.FirstOrDefault()?.ActualSchema)
        .Distinct()
        .ToList();
    if (bases is not [{ DiscriminatorObject.PropertyName: not null } baseSchema])
    {
        Console.Error.WriteLine($"SchemaGen : error SG0001: oneOf '{key}' must list only variants that are an allOf whose first entry is the same base schema with a discriminator.");
        return 1;
    }
    baseSchema.Title = wrapper.Title;
    wrapper.OneOf.Clear();
    wrapper.Reference = baseSchema;
    root.Definitions.Remove(key);
}

// System.Text.Json writes the discriminator itself: derived classes must not also carry it as a property.
// The bases are open only because draft-07 cannot close an allOf, so close them for the generated code.
// The bases are abstract: JSON without a known discriminator must never become a bare base object.
foreach (var baseSchema in root.Definitions.Values.Where(d => d.DiscriminatorObject?.PropertyName != null).ToList())
{
    var discriminatorObject = baseSchema.DiscriminatorObject!;
    var discriminator = discriminatorObject.PropertyName!;
    baseSchema.AllowAdditionalProperties = false;
    baseSchema.IsAbstract = true;
    foreach (var derived in discriminatorObject.Mapping.Values.SelectMany(m => m.ActualSchema.AllOf.Prepend(m.ActualSchema)).Where(d => d.Reference == null))
    {
        derived.AllowAdditionalProperties = false;
        derived.Properties.Remove(discriminator);
        derived.RequiredProperties.Remove(discriminator);
    }
}

var resolver = new CSharpTypeResolver(settings);
resolver.RegisterSchemaDefinitions(root.Definitions);
var all = new CSharpGenerator(root, settings, resolver).GenerateTypes().ToList();

var artifacts = all;

var body = string.Join("\n\n", artifacts
    .OrderBy(a => a.Category).ThenBy(a => a.TypeName, StringComparer.Ordinal)
    .Select(a => a.Code.Trim()));

// System.Text.Json ignores [EnumMember]; its own attribute (.NET 9+) keeps the wire values exact.
body = Regex.Replace(body, @"\[System\.Runtime\.Serialization\.EnumMember\(Value = @""([^""]*)""\)\]",
    "[System.Text.Json.Serialization.JsonStringEnumMemberName(\"$1\")]");

if (body.Contains("[System.Runtime.Serialization.EnumMember") || body.Contains("[EnumMember"))
{
    Console.Error.WriteLine("SchemaGen : error SG0002: an [EnumMember] attribute is left in the output; System.Text.Json would ignore it.");
    return 1;
}

// The default string enum converter also accepts integers ("gesture":3); the schemas allow only the names.
// StrictEnumConverter lives in the output namespace (ButtonBox.Core.Contracts).
body = body.Replace("typeof(System.Text.Json.Serialization.JsonStringEnumConverter<", "typeof(StrictEnumConverter<");

if (body.Contains("JsonStringEnumConverter"))
{
    Console.Error.WriteLine("SchemaGen : error SG0003: a JsonStringEnumConverter is left in the output; it would accept integer enum values.");
    return 1;
}

var text = $"// <auto-generated>Generated by SchemaGen from /schemas. Do not edit.</auto-generated>\n"
    + "#nullable enable\n#pragma warning disable\n\n"
    + $"namespace {ns}\n{{\n{body}\n}}\n";

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
File.WriteAllText(outputFile, text.Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
return 0;

sealed class SharedResolver(JsonSchema root, Dictionary<string, JsonSchema> cache)
    : JsonReferenceResolver(new JsonSchemaAppender(root, new DefaultTypeNameGenerator()))
{
    public static async Task<JsonSchema> LoadAsync(string path, Dictionary<string, JsonSchema> cache)
    {
        path = Path.GetFullPath(path);
        if (!cache.TryGetValue(path, out var schema))
        {
            schema = await JsonSchema.FromFileAsync(path, s => new SharedResolver(s, cache));
            cache[path] = schema;
        }
        return schema;
    }

    public override Task<IJsonReference> ResolveFileReferenceAsync(string filePath, CancellationToken cancellationToken = default)
        => LoadAsync(filePath, cache).ContinueWith(t => (IJsonReference)t.Result, cancellationToken);
}

// Type names come from "title", never from the property or definition that happened to reach a schema first.
sealed class TitleTypeNameGenerator : DefaultTypeNameGenerator
{
    public override string Generate(JsonSchema schema, string? typeNameHint, IEnumerable<string> reservedTypeNames)
        => base.Generate(schema, string.IsNullOrEmpty(schema.Title) ? typeNameHint : schema.Title, reservedTypeNames);
}
