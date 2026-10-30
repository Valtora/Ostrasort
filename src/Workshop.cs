using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ostrasort;

/// <summary>What Steam reports for one Workshop item.</summary>
public readonly record struct WorkshopDetails(long TimeUpdated, string? Description, string? Title);

/// <summary>
/// Key-less Steam Workshop lookups (ISteamRemoteStorage): item details for the
/// update flag, descriptions and names, and the ordered contents of a
/// collection. Without Steamworks, Ostrasort can read the Workshop and link to
/// items, but cannot subscribe the player to anything.
/// </summary>
public static class Workshop
{
    private const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    private const string CollectionUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/";

    public static string ItemUrl(string id) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}";
    public static string SteamClientUrl(string id) => $"steam://url/CommunityFilePage/{id}";

    private static readonly Regex IdInText = new(@"(?:[?&]id=)?(\d{6,})", RegexOptions.Compiled);

    /// <summary>A Workshop id from a pasted link ("...filedetails/?id=123") or a bare number.</summary>
    public static string? ParseId(string text)
    {
        var m = IdInText.Match(text.Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Details for every id Steam resolves; ids it cannot resolve are absent.</summary>
    public static async Task<Dictionary<string, WorkshopDetails>> FetchDetailsAsync(
        HttpClient http, IReadOnlyList<string> ids, CancellationToken token)
    {
        var result = new Dictionary<string, WorkshopDetails>();
        // Steam takes a batch per request; stay well inside its limit
        foreach (var chunk in ids.Chunk(100))
        {
            var form = new List<KeyValuePair<string, string>> { new("itemcount", chunk.Length.ToString()) };
            for (var i = 0; i < chunk.Length; i++) form.Add(new($"publishedfileids[{i}]", chunk[i]));
            using var content = new FormUrlEncodedContent(form);
            using var resp = await http.PostAsync(DetailsUrl, content, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            foreach (var (k, v) in ParseDetails(await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false)))
                result[k] = v;
        }
        return result;
    }

    public static Dictionary<string, WorkshopDetails> ParseDetails(string json)
    {
        var result = new Dictionary<string, WorkshopDetails>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var respEl) ||
            !respEl.TryGetProperty("publishedfiledetails", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var d in arr.EnumerateArray())
        {
            if (!d.TryGetProperty("publishedfileid", out var idEl) || idEl.GetString() is not { } id) continue;
            if (d.TryGetProperty("result", out var rEl) && rEl.ValueKind == JsonValueKind.Number && rEl.GetInt32() != 1) continue;
            var tu = d.TryGetProperty("time_updated", out var tuEl) && tuEl.TryGetInt64(out var t) && t > 0 ? t : 0;
            var desc = d.TryGetProperty("description", out var deEl) && deEl.ValueKind == JsonValueKind.String ? deEl.GetString() : null;
            var title = d.TryGetProperty("title", out var tiEl) && tiEl.ValueKind == JsonValueKind.String ? tiEl.GetString() : null;
            result[id] = new WorkshopDetails(tu, desc, title);
        }
        return result;
    }

    /// <summary>
    /// The items in a collection, in the collection's own order. A collection
    /// nested inside it is expanded once; deeper nesting is ignored. Null when
    /// Steam does not know the id as a collection.
    /// </summary>
    public static async Task<List<string>?> FetchCollectionAsync(HttpClient http, string id, CancellationToken token, int depth = 0)
    {
        var form = new List<KeyValuePair<string, string>> { new("collectioncount", "1"), new("publishedfileids[0]", id) };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await http.PostAsync(CollectionUrl, content, token).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (ParseCollection(await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false)) is not { } children) return null;

        var items = new List<string>();
        foreach (var (child, isCollection) in children)
        {
            if (!isCollection) { if (!items.Contains(child)) items.Add(child); continue; }
            if (depth >= 1) continue;
            foreach (var inner in await FetchCollectionAsync(http, child, token, depth + 1).ConfigureAwait(false) ?? new())
                if (!items.Contains(inner)) items.Add(inner);
        }
        return items;
    }

    /// <summary>The children of the first collection in a GetCollectionDetails reply, ordered; null when it is not a collection.</summary>
    public static List<(string Id, bool IsCollection)>? ParseCollection(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var respEl) ||
            !respEl.TryGetProperty("collectiondetails", out var arr) || arr.ValueKind != JsonValueKind.Array ||
            arr.GetArrayLength() == 0)
            return null;
        var c = arr[0];
        if (c.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Number && r.GetInt32() != 1) return null;
        if (!c.TryGetProperty("children", out var kids) || kids.ValueKind != JsonValueKind.Array) return null;
        return kids.EnumerateArray()
            .Where(k => k.TryGetProperty("publishedfileid", out var p) && p.ValueKind == JsonValueKind.String)
            .Select(k => (
                Id: k.GetProperty("publishedfileid").GetString()!,
                Sort: k.TryGetProperty("sortorder", out var s) && s.TryGetInt32(out var n) ? n : int.MaxValue,
                IsCollection: k.TryGetProperty("filetype", out var f) && f.TryGetInt32(out var ft) && ft == 2))
            .OrderBy(k => k.Sort)
            .Select(k => (k.Id, k.IsCollection))
            .ToList();
    }
}
