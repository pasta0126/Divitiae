using Divitiae.Api.Alpaca;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Divitiae.Api.Controllers
{
    public class SymbolGroupsOptions
    {
        public string[] TopStocks { get; set; } = [];
        public string[] TopEtfs { get; set; } = [];
        public string[] Under80Usd { get; set; } = [];
    }

    [ApiController]
    [Route("api/[controller]")]
    public class SymbolsController(IAlpacaAssetClient alpaca, IAlpacaMarketDataClient marketData, IOptions<SymbolGroupsOptions> groupOptions, ILogger<SymbolsController> logger) : ControllerBase
    {
        // GET api/symbols/status?symbols=AAPL&symbols=MSFT&days=10
        // Also supports comma-separated list: symbols=AAPL,MSFT,SPY
        [HttpGet("status")]
        public async Task<IActionResult> GetSymbolsStatus([FromQuery] string[] symbols, [FromQuery] int days = 10, CancellationToken ct = default)
        {
            var list = NormalizeSymbols(symbols);
            if (list.Count == 0)
                return BadRequest(new { error = "At least one symbol is required", example = "api/symbols/status?symbols=AAPL&symbols=MSFT" });

            if (days <= 0) days = 10;

            var assets = await alpaca.GetAssetsAsync(ct);
            var assetMap = assets.ToDictionary(a => a.Symbol, StringComparer.OrdinalIgnoreCase);

            var items = new List<object>();
            foreach (var s in list.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                assetMap.TryGetValue(s, out var asset);

                var bars = await marketData.GetDailyBarsAsync(s, days, ct);
                var last = bars.LastOrDefault();
                var prev = bars.Count > 1 ? bars[^2] : null;
                var change = (last != null && prev != null && prev.Close != 0) ? (decimal?)((last.Close - prev.Close) / prev.Close * 100m) : null;

                var price = last?.Close; // use daily close as reference; could be replaced by last trade if desired

                items.Add(new
                {
                    symbol = s.ToUpperInvariant(),
                    name = asset?.Name ?? s.ToUpperInvariant(),
                    exchange = asset?.Exchange,
                    lastPriceUsd = price,
                    changePctFromPrevClose = change,
                    tradable = asset?.Tradable,
                    marginable = asset?.Marginable,
                    days,
                    bars
                });
            }

            logger.LogInformation("Symbols/status for {Count} symbols (days={Days})", items.Count, days);
            return Ok(new { count = items.Count, days, items });
        }

        // Group endpoints: TopStocks, TopEtfs, Under80Usd
        [HttpGet("groups/top-stocks")] public Task<IActionResult> GetTopStocks([FromQuery] int days = 10, CancellationToken ct = default)
            => GetGroup(groupOptions.Value.TopStocks, days, ct);
        [HttpGet("groups/top-etfs")] public Task<IActionResult> GetTopEtfs([FromQuery] int days = 10, CancellationToken ct = default)
            => GetGroup(groupOptions.Value.TopEtfs, days, ct);
        [HttpGet("groups/under-80-usd")] public Task<IActionResult> GetUnder80([FromQuery] int days = 10, CancellationToken ct = default)
            => GetGroup(groupOptions.Value.Under80Usd, days, ct);

        private async Task<IActionResult> GetGroup(IEnumerable<string> symbols, int days, CancellationToken ct)
        {
            var list = NormalizeSymbols(symbols?.ToArray() ?? Array.Empty<string>());
            if (list.Count == 0) return Ok(new { count = 0, days, items = Array.Empty<object>() });
            if (days <= 0) days = 10;

            var assets = await alpaca.GetAssetsAsync(ct);
            var assetMap = assets.ToDictionary(a => a.Symbol, StringComparer.OrdinalIgnoreCase);

            var items = new List<object>();
            foreach (var s in list.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                assetMap.TryGetValue(s, out var asset);
                var bars = await marketData.GetDailyBarsAsync(s, days, ct);
                var last = bars.LastOrDefault();
                var prev = bars.Count > 1 ? bars[^2] : null;
                var change = (last != null && prev != null && prev.Close != 0) ? (decimal?)((last.Close - prev.Close) / prev.Close * 100m) : null;
                var price = last?.Close;

                items.Add(new
                {
                    symbol = s.ToUpperInvariant(),
                    name = asset?.Name ?? s.ToUpperInvariant(),
                    exchange = asset?.Exchange,
                    lastPriceUsd = price,
                    changePctFromPrevClose = change,
                    tradable = asset?.Tradable,
                    marginable = asset?.Marginable,
                    days,
                    bars
                });
            }

            logger.LogInformation("Symbols/groups query for {Count} symbols (days={Days})", items.Count, days);
            return Ok(new { count = items.Count, days, items });
        }

        private static List<string> NormalizeSymbols(string[] symbols)
        {
            var result = new List<string>();
            if (symbols == null) return result;
            foreach (var raw in symbols)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var p in parts)
                {
                    if (!string.IsNullOrWhiteSpace(p)) result.Add(p);
                }
            }
            return result;
        }
    }
}
