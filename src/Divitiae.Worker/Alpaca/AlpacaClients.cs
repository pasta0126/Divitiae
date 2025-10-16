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
            var body = new
            {
                symbol = request.Symbol,
                side = request.Side == OrderSide.Buy ? "buy" : "sell",
                type = "market",
                time_in_force = request.TimeInForce,
                notional = request.NotionalUsd,
                order_class = "bracket",
                take_profit = new { limit_price = request.TakeProfitLimitPrice },
                stop_loss = new { stop_price = request.StopLossStopPrice }
            };

            _logger.LogInformation("POST /orders {Symbol} side={Side} tif={TIF} notional={Notional} tp={TP} sl={SL}", request.Symbol, request.Side, request.TimeInForce, request.NotionalUsd, request.TakeProfitLimitPrice, request.StopLossStopPrice);
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
            var url = $"stocks/{symbol}/bars?timeframe=1Min&limit={limit}&start={Uri.EscapeDataString(start.ToString("O"))}&end={Uri.EscapeDataString(now.ToString("O"))}";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
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
                _logger.LogInformation("Fetched {Count} seed bars for {Symbol}", list.Count, symbol);
                return list;
            }
            _logger.LogWarning("No bars array in market data response for {Symbol}", symbol);
            return Array.Empty<Bar>();
        }

        public async Task<Bar?> GetLatestMinuteBarAsync(string symbol, CancellationToken ct)
        {
            var url = $"stocks/{symbol}/bars?timeframe=1Min&limit=1";
            using var resp = await _http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
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
                var mapped = Map(dto);
                _logger.LogDebug("Latest bar {Symbol}: {Time} O={O} H={H} L={L} C={C} V={V}", symbol, mapped.Time, mapped.Open, mapped.High, mapped.Low, mapped.Close, mapped.Volume);
                return mapped;
            }
            _logger.LogWarning("No latest bar returned for {Symbol}", symbol);
            return null;
        }
    }
}
