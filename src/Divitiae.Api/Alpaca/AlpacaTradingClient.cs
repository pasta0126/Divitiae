using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Divitiae.Api.Alpaca
{
    internal static class JsonCfgTrading
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public record Account
    {
        public decimal BuyingPower { get; init; }
        public decimal Equity { get; init; }
        public string Currency { get; init; } = "USD";
    }

    public record Position
    {
        public string Symbol { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public decimal AvgEntryPrice { get; init; }
        public decimal CurrentPrice { get; init; }
        public decimal MarketValue { get; init; }
        public decimal UnrealizedPl { get; init; }
        public string Side { get; init; } = string.Empty;
    }

    public interface IAlpacaTradingClient
    {
        Task<Account> GetAccountAsync(CancellationToken ct);
        Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct);
        Task ClosePositionAsync(string symbol, CancellationToken ct);
    }

    public class AlpacaTradingClient : IAlpacaTradingClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<AlpacaTradingClient> _logger;

        public AlpacaTradingClient(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> opts, ILogger<AlpacaTradingClient> logger)
        {
            _http = httpClientFactory.CreateClient("alpaca-trading");
            _logger = logger;
        }

        public async Task<Account> GetAccountAsync(CancellationToken ct)
        {
            var acc = await _http.GetFromJsonAsync<Account>("account", JsonCfgTrading.Options, ct);
            if (acc is null) throw new InvalidOperationException("Cannot fetch account");
            return acc;
        }

        public async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct)
        {
            using var resp = await _http.GetAsync("positions", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Array.Empty<Position>();
            }
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonCfgTrading.Options, ct);
            var list = new List<Position>();
            if (json.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in json.EnumerateArray())
                {
                    try
                    {
                        list.Add(new Position
                        {
                            Symbol = el.TryGetProperty("symbol", out var s) ? (s.GetString() ?? string.Empty) : string.Empty,
                            Quantity = el.TryGetProperty("qty", out var q) ? decimal.Parse(q.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0m,
                            AvgEntryPrice = el.TryGetProperty("avg_entry_price", out var a) ? decimal.Parse(a.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0m,
                            CurrentPrice = el.TryGetProperty("current_price", out var cp) ? decimal.Parse(cp.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0m,
                            MarketValue = el.TryGetProperty("market_value", out var mv) ? decimal.Parse(mv.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0m,
                            UnrealizedPl = el.TryGetProperty("unrealized_pl", out var pl) ? decimal.Parse(pl.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0m,
                            Side = el.TryGetProperty("side", out var sd) ? (sd.GetString() ?? string.Empty) : string.Empty
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse position element: {Element}", el.ToString());
                    }
                }
            }
            return list;
        }

        public async Task ClosePositionAsync(string symbol, CancellationToken ct)
        {
            _logger.LogInformation("DELETE /positions/{Symbol}", symbol);
            using var resp = await _http.DeleteAsync($"positions/{symbol}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Close position failed for {Symbol}: {Status} {Body}", symbol, resp.StatusCode, body);
                resp.EnsureSuccessStatusCode();
            }
        }
    }
}
