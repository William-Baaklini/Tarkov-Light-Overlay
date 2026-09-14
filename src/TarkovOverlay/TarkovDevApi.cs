using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TarkovOverlay;

/// <summary>Just the fields the popup shows. Keeping it narrow keeps ~5300 items around 1 MB.</summary>
public sealed record ItemPrice(
    string Id,
    string Name,
    string ShortName,
    int Flea,            // 24h average on the flea market, 0 when not tradeable
    int FleaLow,         // last observed low, closer to what it actually sells for
    double Change48h,
    string TraderName,
    int TraderPrice,
    string Link);

/// <summary>
/// Prices from the public tarkov.dev GraphQL API, cached to disk.
///
/// The whole item table is fetched once and kept, rather than queried per hover:
/// one request every 20 minutes is far cheaper than a network round trip every
/// time the cursor moves, and it means the scanner still works offline.
/// </summary>
public sealed class PriceBook
{
    private const string Endpoint = "https://api.tarkov.dev/graphql";

    // Deliberately conservative: only long-standing fields, so a schema change on
    // their side degrades one column rather than breaking the feature.
    private const string Query = """
        {"query":"{items{id name shortName avg24hPrice lastLowPrice changeLast48hPercent link sellFor{priceRUB vendor{name}}}}"}
        """;

    private Dictionary<string, ItemPrice> _byId = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _byId.Count; } }
    public DateTime UpdatedUtc { get; private set; }
    public string? LastError { get; private set; }

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TarkovOverlay", "prices.json");

    public ItemPrice? Get(string id)
    {
        lock (_lock) return _byId.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>Load whatever is on disk. Cheap, and leaves the app useful offline.</summary>
    public void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            using var fs = File.OpenRead(CachePath);
            var doc = JsonSerializer.Deserialize<CacheFile>(fs);
            if (doc?.Items is null) return;
            var map = doc.Items.ToDictionary(i => i.Id);
            lock (_lock) _byId = map;
            UpdatedUtc = doc.UpdatedUtc;
        }
        catch { /* a corrupt cache is not worth a dialog; the next refresh fixes it */ }
    }

    public bool IsStale(TimeSpan maxAge) => DateTime.UtcNow - UpdatedUtc > maxAge;

    /// <summary>
    /// Pull the item table. Safe to call from a background thread; failures leave
    /// the previous data in place and are reported through <see cref="LastError"/>.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("User-Agent", "TarkovLightOverlay");
            using var content = new StringContent(Query, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)resp.StatusCode}";
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs) &&
                errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            {
                LastError = errs[0].ValueKind == JsonValueKind.String
                    ? errs[0].GetString()
                    : errs[0].ToString();
                return false;
            }
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                LastError = "unexpected response shape";
                return false;
            }

            var map = new Dictionary<string, ItemPrice>(items.GetArrayLength());
            foreach (var it in items.EnumerateArray())
            {
                var id = Str(it, "id");
                if (string.IsNullOrEmpty(id)) continue;

                // Best cash sale to a trader, which is the number that decides
                // whether an item is worth picking up at all.
                string traderName = "";
                int traderPrice = 0;
                if (it.TryGetProperty("sellFor", out var sells) && sells.ValueKind == JsonValueKind.Array)
                    foreach (var s in sells.EnumerateArray())
                    {
                        int p = Int(s, "priceRUB");
                        if (p <= traderPrice) continue;
                        var vendor = s.TryGetProperty("vendor", out var v) ? Str(v, "name") : "";
                        // "Flea Market" shows up as a vendor; it is already its own column.
                        if (vendor.Contains("Flea", StringComparison.OrdinalIgnoreCase)) continue;
                        traderPrice = p;
                        traderName = vendor;
                    }

                map[id] = new ItemPrice(
                    id,
                    Str(it, "name"),
                    Str(it, "shortName"),
                    Int(it, "avg24hPrice"),
                    Int(it, "lastLowPrice"),
                    Dbl(it, "changeLast48hPercent"),
                    traderName,
                    traderPrice,
                    Str(it, "link"));
            }

            if (map.Count == 0) { LastError = "no items returned"; return false; }

            lock (_lock) _byId = map;
            UpdatedUtc = DateTime.UtcNow;
            LastError = null;
            SaveCache(map);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private void SaveCache(Dictionary<string, ItemPrice> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var tmp = CachePath + ".tmp";
            using (var fs = File.Create(tmp))
                JsonSerializer.Serialize(fs, new CacheFile
                {
                    UpdatedUtc = UpdatedUtc,
                    Items = map.Values.ToList(),
                });
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch { /* best effort */ }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private sealed class CacheFile
    {
        public DateTime UpdatedUtc { get; set; }
        public List<ItemPrice> Items { get; set; } = new();
    }
}
