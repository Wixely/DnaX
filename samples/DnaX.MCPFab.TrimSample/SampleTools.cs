using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DnaX.MCPFab.TrimSample;

/// <summary>In-memory state, standing in for a server's domain service.</summary>
public sealed class SampleStore
{
    private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal)
    {
        ["alpha"] = "first",
        ["beta"] = "second",
    };

    /// <summary>Number of stored items.</summary>
    public int Count => _items.Count;

    /// <summary>All stored items.</summary>
    public IReadOnlyDictionary<string, string> Items => _items;

    /// <summary>Looks up one item.</summary>
    public string? Find(string key) => _items.GetValueOrDefault(key);
}

/// <summary>Tools exercised by the trim smoke test.</summary>
[McpServerToolType]
public sealed class SampleTools
{
    /// <summary>Lists items, exercising object and array building.</summary>
    [McpServerTool(Name = "sample_list_items")]
    [Description("Lists the sample items.")]
    public static string ListItems(SampleStore store, McpFabLimitsOptions limits)
    {
        ArgumentNullException.ThrowIfNull(store);

        return McpJson.Object()
            .Set("count", store.Count)
            .Set("items", McpJson.Array(
                store.Items,
                item => McpJson.Object().Set("key", item.Key).Set("value", item.Value)))
            .ToJson(limits);
    }

    /// <summary>
    /// Returns one item, exercising the boxed-scalar path and the shared error envelope.
    /// </summary>
    [McpServerTool(Name = "sample_get_item")]
    [Description("Gets one sample item by key.")]
    public static string GetItem(SampleStore store, [Description("Item key.")] string key)
    {
        ArgumentNullException.ThrowIfNull(store);

        string? value = store.Find(key);
        if (value is null)
        {
            return McpJson.Error("not_found", $"No item named '{key}'.").ToJsonString();
        }

        return McpJson.Object()
            .Set("key", key)
            .Set("value", value)
            .Set("length", McpJson.Scalar(value.Length))
            .ToJsonString();
    }
}
