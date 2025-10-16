using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Divitiae.Api.Alpaca
{
    public record Bar
    {
        public DateTime Time { get; init; }
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public long Volume { get; init; }
    }

    public interface IAlpacaMarketDataClient
    {
        Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, int days, CancellationToken ct);
    }

    internal static class JsonCfgMd
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public class AlpacaMarketDataClient : IAlpacaMarketDataClient
    {
        private readonly HttpClient _marketHttp;
        private readonly AlpacaOptions _opts;
        private readonly ILogger<AlpacaMarketDataClient> _logger;

        public AlpacaMarketDataClient(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> opts, ILogger<AlpacaMarketDataClient> logger)
        {
            _marketHttp = httpClientFactory.CreateClient("alpaca-marketdata");
            _opts = opts.Value;
            _logger = logger;
        }

        public async Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, int days, CancellationToken ct)
        {
            if (days <= 0) return Array.Empty<Bar>();
            var url = $"stocks/{symbol}/bars?timeframe=1Day&limit={days}&feed={Uri.EscapeDataString(_opts.MarketDataFeed)}";
            using var resp = await _marketHttp.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Daily bars failed for {Symbol}: {Status}", symbol, resp.StatusCode);
                return Array.Empty<Bar>();
            }
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonCfgMd.Options, ct);
            if (json.TryGetProperty("bars", out var barsEl) && barsEl.ValueKind == JsonValueKind.Array)
            {
                var list = new List<Bar>(barsEl.GetArrayLength());
                foreach (var el in barsEl.EnumerateArray())
                {
                    list.Add(new Bar
                    {
                        Time = el.GetProperty("t").GetDateTime(),
                        Open = el.GetProperty("o").GetDecimal(),
                        High = el.GetProperty("h").GetDecimal(),
                        Low = el.GetProperty("l").GetDecimal(),
                        Close = el.GetProperty("c").GetDecimal(),
                        Volume = el.GetProperty("v").GetInt64(),
                    });
                }
                return list;
            }
            return Array.Empty<Bar>();
        }
    }
}
