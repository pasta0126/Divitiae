using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Divitiae.Api.Alpaca
{
    internal static class JsonCfg
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public class AlpacaAssetClient : IAlpacaAssetClient
    {
        private readonly HttpClient _tradingHttp;
        private readonly HttpClient _marketHttp;
        private readonly AlpacaOptions _opts;
        private readonly ILogger<AlpacaAssetClient> _logger;

        public AlpacaAssetClient(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> opts, ILogger<AlpacaAssetClient> logger)
        {
            _tradingHttp = httpClientFactory.CreateClient("alpaca-trading");
            _marketHttp = httpClientFactory.CreateClient("alpaca-marketdata");
            _opts = opts.Value;
            _logger = logger;
        }

        public async Task<IReadOnlyList<Asset>> GetAssetsAsync(CancellationToken ct)
        {
            var list = new List<Asset>();
            using var resp = await _tradingHttp.GetAsync("assets?status=active&asset_class=us_equity", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Assets request failed: {Status}", resp.StatusCode);
                return list;
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonCfg.Options, ct);
            if (json.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in json.EnumerateArray())
                {
                    if (!el.TryGetProperty("symbol", out var s)) continue;
                    var symbol = s.GetString() ?? string.Empty;
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? symbol : symbol;
                    var tradable = el.TryGetProperty("tradable", out var t) && t.GetBoolean();
                    var marginable = el.TryGetProperty("marginable", out var m) && m.GetBoolean();
                    var exchange = el.TryGetProperty("exchange", out var e) ? e.GetString() ?? string.Empty : string.Empty;
                    string[] attrs = Array.Empty<string>();
                    if (el.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Array)
                    {
                        attrs = a.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                    }
                    list.Add(new Asset { Symbol = symbol, Name = name, Tradable = tradable, Marginable = marginable, Exchange = exchange, Attributes = attrs });
                }
            }
            return list;
        }

        public async Task<decimal?> GetLastTradePriceAsync(string symbol, CancellationToken ct)
        {
            var url = $"stocks/{symbol}/trades/latest?feed={Uri.EscapeDataString(_opts.MarketDataFeed)}";
            using var resp = await _marketHttp.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Last trade request failed for {Symbol}: {Status}", symbol, resp.StatusCode);
                return null;
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonCfg.Options, ct);
            if (json.TryGetProperty("trade", out var trade) && trade.ValueKind == JsonValueKind.Object && trade.TryGetProperty("p", out var p))
            {
                return p.GetDecimal();
            }
            return null;
        }
    }
}
