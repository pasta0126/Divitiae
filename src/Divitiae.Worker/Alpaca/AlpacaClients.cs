using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Divitiae.Worker.Config;
using Divitiae.Worker.Trading;
using Microsoft.Extensions.Options;

namespace Divitiae.Worker.Alpaca
{
    internal static class JsonCfg
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public interface IAlpacaAssetClient
    {
        Task<IReadOnlyList<Asset>> GetAssetsAsync(CancellationToken ct);
        Task<decimal?> GetLastTradePriceAsync(string symbol, CancellationToken ct);
    }

    public record Asset
    {
        public required string Symbol { get; init; }
        public required string Name { get; init; }
        public bool Tradable { get; init; }
        public bool Marginable { get; init; }
        public string Class { get; init; } = "us_equity";
    }

    public class AlpacaAssetClient : IAlpacaAssetClient
    {
        private readonly HttpClient _tradingHttp;
        private readonly HttpClient _marketHttp;
        private readonly ILogger<AlpacaAssetClient> _logger;
        private readonly AlpacaOptions _opts;

        public AlpacaAssetClient(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> opts, ILogger<AlpacaAssetClient> logger)
        {
            _tradingHttp = httpClientFactory.CreateClient("alpaca-trading");
            _marketHttp = httpClientFactory.CreateClient("alpaca-marketdata");
            _logger = logger;
            _opts = opts.Value;
        }

        public async Task<IReadOnlyList<Asset>> GetAssetsAsync(CancellationToken ct)
        {
            var list = new List<Asset>();
            using var resp = await _tradingHttp.GetAsync("assets?status=active&asset_class=us_equity", ct);
            resp.EnsureSuccessStatusCode();
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
                    list.Add(new Asset { Symbol = symbol, Name = name, Tradable = tradable, Marginable = marginable });
                }
            }
            _logger.LogInformation("Fetched {Count} active assets", list.Count);
            return list;
        }

        public async Task<decimal?> GetLastTradePriceAsync(string symbol, CancellationToken ct)
        {
            var url = $"stocks/{symbol}/trades/latest?feed={Uri.EscapeDataString(_opts.MarketDataFeed)}";
            using var resp = await _marketHttp.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get last trade for {Symbol}: {Status}", symbol, resp.StatusCode);
                return null;
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonCfg.Options, ct);
            if (json.TryGetProperty("trade", out var trade) && trade.ValueKind == JsonValueKind.Object)
            {
                if (trade.TryGetProperty("p", out var priceEl))
                {
                    return priceEl.GetDecimal();
                }
            }
            return null;
        }
    }

    public class AlpacaTradingClient : IAlpacaTradingClient
    {
        private readonly HttpClient _http;
        private readonly AlpacaOptions _opts;
        private readonly ILogger<AlpacaTradingClient> _logger;

        public AlpacaTradingClient(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> opts, ILogger<AlpacaTradingClient> logger)
        {
            _http = httpClientFactory.CreateClient("alpaca-trading");
            _opts = opts.Value;
            _logger = logger;
        }

        public async Task<Account> GetAccountAsync(CancellationToken ct)
        {
            var acc = await _http.GetFromJsonAsync<Account>("account", JsonCfg.Options, ct);
            if (acc is null) throw new InvalidOperationException("Cannot fetch account");
            _logger.LogDebug("Account snapshot: equity={Equity} buyingPower={BuyingPower} currency={Currency}", acc.Equity, acc.BuyingPower, acc.Currency);
            return acc;
        }

        public async Task<bool> HasOpenPositionAsync(string symbol, CancellationToken ct)
        {
            var resp = await _http.GetAsync($"positions/{symbol}", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("No open position for {Symbol}", symbol);
                return false;
            }
            resp.EnsureSuccessStatusCode();
            _logger.LogDebug("Open position exists for {Symbol}", symbol);
            return true;
        }

        public async Task<bool> HasOpenOrdersAsync(string symbol, CancellationToken ct)
        {
            var url = $"orders?status=open&symbols={Uri.EscapeDataString(symbol)}";
            var doc = await _http.GetFromJsonAsync<JsonElement>(url, JsonCfg.Options, ct);
            if (doc.ValueKind == JsonValueKind.Array)
            {
                var count = doc.GetArrayLength();
                _logger.LogDebug("Open orders for {Symbol}: {Count}", symbol, count);
                return count > 0;
            }
            return false;
        }

        public async Task SubmitBracketOrderNotionalAsync(BracketOrderRequest request, CancellationToken ct)
        {
            // Prefer trailing stop if configured, else fixed stop loss
            object stopLoss = _opts.TrailingStopPercent > 0
                ? new { trail_percent = (decimal)_opts.TrailingStopPercent }
                : new { stop_price = request.StopLossStopPrice };

            var body = new
            {
                symbol = request.Symbol,
                side = request.Side == OrderSide.Buy ? "buy" : "sell",
                type = "market",
                time_in_force = request.TimeInForce,
                notional = request.NotionalUsd,
                order_class = "bracket",
                take_profit = new { limit_price = request.TakeProfitLimitPrice },
                stop_loss = stopLoss
            };

            _logger.LogInformation("POST /orders {Symbol} side={Side} tif={TIF} notional={Notional} tp={TP} sl/trail={SL}", request.Symbol, request.Side, request.TimeInForce, request.NotionalUsd, request.TakeProfitLimitPrice, _opts.TrailingStopPercent > 0 ? $"trail%={(decimal)_opts.TrailingStopPercent}" : request.StopLossStopPrice);
            var resp = await _http.PostAsJsonAsync("orders", body, JsonCfg.Options, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Order submit failed: {Status} {Body}", resp.StatusCode, content);
                resp.EnsureSuccessStatusCode();
            }
            else
            {
                _logger.LogInformation("Order submitted successfully for {Symbol}", request.Symbol);
            }
        }

        public async Task ClosePositionAsync(string symbol, CancellationToken ct)
        {
            _logger.LogInformation("DELETE /positions/{Symbol}", symbol);
            var resp = await _http.DeleteAsync($"positions/{symbol}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Close position failed: {Status} {Body}", resp.StatusCode, content);
                resp.EnsureSuccessStatusCode();
            }
            else
            {
                _logger.LogInformation("Position close request sent for {Symbol}", symbol);
            }
        }
    }

    public class AlpacaMarketDataClient : IAlpacaMarketDataClient
    {
        private readonly HttpClient _http;
        private readonly AlpacaOptions _opts;
        private readonly ILogger<AlpacaMarketDataClient> _logger;

        public AlpacaMarketDataClient(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> opts, ILogger<AlpacaMarketDataClient> logger)
        {
            _http = httpClientFactory.CreateClient("alpaca-marketdata");
            _opts = opts.Value;
            _logger = logger;
        }

        private record BarsResponse(BarDTO[] Bars);
        private record BarDTO
        {
            public DateTime T { get; init; }
            public decimal O { get; init; }
            public decimal H { get; init; }
            public decimal L { get; init; }
            public decimal C { get; init; }
            public long V { get; init; }
        }

        private static Bar Map(BarDTO dto) => new()
        {
            Time = dto.T,
            Open = dto.O,
            High = dto.H,
            Low = dto.L,
            Close = dto.C,
            Volume = dto.V
        };

        public async Task<IReadOnlyList<Bar>> GetMinuteBarsAsync(string symbol, int limit, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var start = now.AddMinutes(-limit - 5);
            var url = $"stocks/{symbol}/bars?timeframe=1Min&limit={limit}&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(now.ToString("O"))}&feed={Uri.EscapeDataString(_opts.MarketDataFeed)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Market data bars failed for {Symbol}: {Status} {Body}", symbol, resp.StatusCode, content);
                return Array.Empty<Bar>();
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonCfg.Options, ct);
            if (json.TryGetProperty("bars", out var barsEl) && barsEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<Bar>(barsEl.GetArrayLength());
                foreach (var el in barsEl.EnumerateArray())
                {
                    var dto = new BarDTO
                    {
                        T = el.GetProperty("t").GetDateTime(),
                        O = el.GetProperty("o").GetDecimal(),
                        H = el.GetProperty("h").GetDecimal(),
                        L = el.GetProperty("l").GetDecimal(),
                        C = el.GetProperty("c").GetDecimal(),
                        V = el.GetProperty("v").GetInt64(),
                    };
                    list.Add(Map(dto));
                }
                return list;
            }
            return Array.Empty<Bar>();
        }

        public async Task<Bar?> GetLatestMinuteBarAsync(string symbol, CancellationToken ct)
        {
            var url = $"stocks/{symbol}/bars?timeframe=1Min&limit=1&feed={Uri.EscapeDataString(_opts.MarketDataFeed)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Latest bar failed for {Symbol}: {Status} {Body}", symbol, resp.StatusCode, content);
                return null;
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonCfg.Options, ct);
            if (json.TryGetProperty("bars", out var barsEl) && barsEl.ValueKind == JsonValueKind.Array && barsEl.GetArrayLength() > 0)
            {
                var el = barsEl.EnumerateArray().Last();
                var dto = new BarDTO
                {
                    T = el.GetProperty("t").GetDateTime(),
                    O = el.GetProperty("o").GetDecimal(),
                    H = el.GetProperty("h").GetDecimal(),
                    L = el.GetProperty("l").GetDecimal(),
                    C = el.GetProperty("c").GetDecimal(),
                    V = el.GetProperty("v").GetInt64(),
                };
                return Map(dto);
            }
            return null;
        }
    }
}
