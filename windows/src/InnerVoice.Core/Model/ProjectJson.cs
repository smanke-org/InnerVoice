// project.json, written so the Mac app (Swift's JSONEncoder with sorted keys) reads it and
// the other way round. The rules, from docs/WINDOWS-PLAN.md:
//  1. Dates are numbers: seconds since 2001-01-01 UTC (AppleDate).
//  2. UUIDs are uppercase strings.
//  3. Every non-optional property is always written (Swift's synthesized Codable fails on
//     a missing key); optional ones are left out when null, like Swift writes them.
//  4. Enums are their raw strings (camelCase), sets are arrays.
//  5. Unknown fields are kept (ModelObject.Extra) and written back.
// Keys are sorted, like the Mac's output, so the two apps' files diff cleanly.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InnerVoice.Core;

public static class ProjectJson
{
    public static readonly JsonSerializerOptions Options = Make();

    static JsonSerializerOptions Make()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        o.Converters.Add(new UpperGuidConverter());
        o.Converters.Add(new AppleDateConverter());
        // Before the enum converter: "qwen" and "chatterbox" read as Pocket TTS, an engine this
        // version doesn't know as Mix Engines (the Mac's VoiceEngine.init(from:)).
        o.Converters.Add(new VoiceEngineConverter());
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return o;
    }

    public static Project Decode(byte[] json)
    {
        var p = JsonSerializer.Deserialize<Project>(json, Options) ?? throw new JsonException("project.json is empty.");
        return p;
    }

    public static byte[] Encode(Project project)
    {
        var node = JsonSerializer.SerializeToNode(project, Options)!;
        var sorted = Sort(node);
        return JsonSerializer.SerializeToUtf8Bytes(sorted, new JsonSerializerOptions { Encoder = Options.Encoder });
    }

    /// Recursively orders object keys by ordinal comparison, like Swift's `.sortedKeys`.
    static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Sort(kv.Value?.DeepClone())))),
        JsonArray arr => new JsonArray(arr.Select(n => Sort(n?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    sealed class UpperGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Guid.Parse(reader.GetString() ?? throw new JsonException("Expected a UUID."));

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("D").ToUpperInvariant());

        public override Guid ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Guid.Parse(reader.GetString()!);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
            writer.WritePropertyName(value.ToString("D").ToUpperInvariant());
    }

    sealed class AppleDateConverter : JsonConverter<AppleDate>
    {
        public override AppleDate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetDouble());

        public override void Write(Utf8JsonWriter writer, AppleDate value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.Seconds);
    }
}
