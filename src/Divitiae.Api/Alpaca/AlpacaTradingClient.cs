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

    public record MarketClock
    {
        public bool IsOpen { get; init; }
        public DateTime? NextOpen { get; init; }
        public DateTime? NextClose { get; init; }
    }

    public interface IAlpacaTradingClient
    {
        Task<Account> GetAccountAsync(CancellationToken ct);
        Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct);
        Task ClosePositionAsync(string symbol, CancellationToken ct);
        Task<MarketClock> GetClockAsync(CancellationToken ct);
        Task<bool> IsMarketOpenAsync(CancellationToken ct);
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
            var json = await resp.Content.ReadFromJsonAsync<Position[]>(JsonCfgTrading.Options, ct);
            return json ?? Array.Empty<Position>();
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

        public async Task<MarketClock> GetClockAsync(CancellationToken ct)
        {
            var doc = await _http.GetFromJsonAsync<JsonElement>("clock", JsonCfgTrading.Options, ct);
            bool isOpen = false; DateTime? nextOpen = null; DateTime? nextClose = null;
            if (doc.ValueKind == JsonValueKind.Object)
            {
                if (doc.TryGetProperty("is_open", out var openEl))
                    isOpen = openEl.ValueKind == JsonValueKind.True;
                if (doc.TryGetProperty("next_open", out var no) && no.ValueKind == JsonValueKind.String && DateTime.TryParse(no.GetString(), out var dtNo))
                    nextOpen = dtNo;
                if (doc.TryGetProperty("next_close", out var nc) && nc.ValueKind == JsonValueKind.String && DateTime.TryParse(nc.GetString(), out var dtNc))
                    nextClose = dtNc;
            }
            return new MarketClock { IsOpen = isOpen, NextOpen = nextOpen, NextClose = nextClose };
        }

        public async Task<bool> IsMarketOpenAsync(CancellationToken ct)
        {
            var c = await GetClockAsync(ct);
            return c.IsOpen;
        }
    }
}
