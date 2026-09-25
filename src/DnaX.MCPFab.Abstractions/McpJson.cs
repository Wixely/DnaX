using System.Text.Json.Nodes;

namespace DnaX.MCPFab;

/// <summary>
/// Trim-safe JSON building for tool results.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>JsonSerializer.Serialize(new { ... })</c>, which the estate used at roughly 427
/// sites. Anonymous types cannot be source-generated and nothing statically references their
/// property getters, so under trimming they are stripped and the call returns <c>{}</c> - it does
/// not throw, which is why the problem survived undetected.
/// </para>
/// <para>
/// <see cref="JsonObject"/>, <see cref="JsonArray"/> and <see cref="JsonValue"/> have built-in
/// converters in the well-known resolver set, so building a node and calling
/// <see cref="JsonNode.ToJsonString"/> is trim- and AOT-safe with no per-server
/// <c>JsonSerializerContext</c> and no <c>[JsonSerializable]</c> to maintain.
/// </para>
/// <para>
/// <see cref="Set(JsonObject, string, string?)"/> and its overloads skip nulls, reproducing the
/// <c>JsonIgnoreCondition.WhenWritingNull</c> that every server's <c>JsonOpts.Default</c> set.
/// <c>ReferenceHandler.IgnoreCycles</c> becomes unnecessary: a node built this way is a tree.
/// </para>
/// </remarks>
public static class McpJson
{
    /// <summary>Starts a new JSON object.</summary>
    public static JsonObject Object() => [];

    /// <summary>Sets a string member, skipping the write when the value is null.</summary>
    public static JsonObject Set(this JsonObject target, string name, string? value)
        => SetNode(target, name, value is null ? null : JsonValue.Create(value));

    /// <summary>Sets an integer member, skipping the write when the value is null.</summary>
    public static JsonObject Set(this JsonObject target, string name, long? value)
        => SetNode(target, name, value is null ? null : JsonValue.Create(value.Value));

    /// <summary>Sets a floating-point member, skipping the write when the value is null.</summary>
    public static JsonObject Set(this JsonObject target, string name, double? value)
        => SetNode(target, name, value is null ? null : JsonValue.Create(value.Value));

    /// <summary>Sets a decimal member, skipping the write when the value is null.</summary>
    public static JsonObject Set(this JsonObject target, string name, decimal? value)
        => SetNode(target, name, value is null ? null : JsonValue.Create(value.Value));

    /// <summary>Sets a boolean member, skipping the write when the value is null.</summary>
    public static JsonObject Set(this JsonObject target, string name, bool? value)
        => SetNode(target, name, value is null ? null : JsonValue.Create(value.Value));

    /// <summary>Sets a timestamp member, skipping the write when the value is null.</summary>
    public static JsonObject Set(this JsonObject target, string name, DateTimeOffset? value)
        => SetNode(target, name, value is null ? null : JsonValue.Create(value.Value));

    /// <summary>Sets a nested node member, skipping the write when the value is null.</summary>
    public static JsonObject Set(this JsonObject target, string name, JsonNode? value)
        => SetNode(target, name, value);

    /// <summary>Projects a sequence into a JSON array, skipping elements that map to null.</summary>
    public static JsonArray Array<T>(IEnumerable<T> source, Func<T, JsonNode?> map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);

        JsonArray array = [];
        foreach (T item in source)
        {
            if (map(item) is { } node)
            {
                array.Add(node);
            }
        }

        return array;
    }

    /// <summary>
    /// Projects a sequence of key/value pairs into a JSON object, as a dictionary serialises.
    /// </summary>
    /// <remarks>
    /// <c>Dictionary&lt;string, T&gt;</c> writes as a JSON object, not an array of pairs, so a
    /// rewrite that treated one as an ordinary sequence would change the payload's shape. Insertion
    /// order is preserved, matching what the reflection-based serialiser produced.
    /// </remarks>
    public static JsonObject Map<T>(IEnumerable<KeyValuePair<string, T>> source, Func<T, JsonNode?> map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);

        JsonObject target = [];
        foreach (KeyValuePair<string, T> pair in source)
        {
            // Unlike Set, a null value is written: a dictionary entry that exists with a null value
            // is not the same as an absent key, and WhenWritingNull did not remove it.
            target[pair.Key] = map(pair.Value);
        }

        return target;
    }

    /// <summary>
    /// Converts a boxed value of unknown runtime type into a JSON value.
    /// </summary>
    /// <remarks>
    /// The one type switch in MCPFab, and the reason source-generated contexts were not chosen:
    /// a database cell arrives boxed as <see cref="decimal"/>, <see cref="DateTime"/>,
    /// <see cref="byte"/>[] or a provider-specific type, and a generated context can only emit a
    /// resolver for <see cref="object"/>, which fails at runtime for anything unregistered.
    /// Types not listed here fall back to their string form rather than reaching for reflection.
    /// </remarks>
    public static JsonNode? Scalar(object? value) => value switch
    {
        null or DBNull => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        short sh => JsonValue.Create(sh),
        byte by => JsonValue.Create(by),
        sbyte sb => JsonValue.Create(sb),
        uint ui => JsonValue.Create(ui),
        ulong ul => JsonValue.Create(ul),
        ushort us => JsonValue.Create(us),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal m => JsonValue.Create(m),
        DateTimeOffset dto => JsonValue.Create(dto),
        DateTime dt => JsonValue.Create(dt),
        DateOnly date => JsonValue.Create(date.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
        TimeOnly time => JsonValue.Create(time.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
        TimeSpan ts => JsonValue.Create(ts.ToString("c", System.Globalization.CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        JsonNode node => node.DeepClone(),
        _ => JsonValue.Create(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
    };

    /// <summary>
    /// Builds the error envelope tools return, so failures have one shape across every server.
    /// </summary>
    /// <param name="code">Stable machine-readable code, for example <c>permission_denied</c>.</param>
    /// <param name="message">Human-readable explanation, ideally naming how to proceed.</param>
    /// <param name="detail">Optional structured detail.</param>
    public static JsonObject Error(string code, string message, JsonObject? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return Object()
            .Set("ok", false)
            .Set("code", code)
            .Set("error", message)
            .Set("detail", detail);
    }

    /// <summary>
    /// Renders a node, truncating into a structured envelope when it exceeds
    /// <see cref="McpFabLimitsOptions.MaxChars"/> rather than emitting malformed JSON.
    /// </summary>
    public static string ToJson(this JsonNode node, McpFabLimitsOptions? limits = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        string json = node.ToJsonString();
        int max = limits?.MaxChars ?? 0;
        if (max <= 0 || json.Length <= max)
        {
            return json;
        }

        // Leave room for the envelope itself so the truncated result is still valid JSON.
        int room = System.Math.Max(0, max - 400);
        return Object()
            .Set("truncated", true)
            .Set("originalChars", json.Length)
            .Set("maxChars", max)
            .Set("hint", "Response exceeded the configured MaxChars. 'partial' holds the leading fragment as text.")
            .Set("partial", json[..room])
            .ToJsonString();
    }

    private static JsonObject SetNode(JsonObject target, string name, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (value is not null)
        {
            target[name] = value;
        }

        return target;
    }
}
