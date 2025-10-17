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

    public record SymbolsStatusRequest
    {
        public string[] Symbols { get; init; } = [];
        public int Days { get; init; } = 10;
    }

    [ApiController]
    [Route("api/[controller]")]
    public class SymbolsController(IAlpacaAssetClient alpaca, IAlpacaMarketDataClient marketData, IOptions<SymbolGroupsOptions> groupOptions, ILogger<SymbolsController> logger) : ControllerBase
    {
        [HttpPost("status")]
        public async Task<IActionResult> GetSymbolsStatus([FromBody] SymbolsStatusRequest request, CancellationToken ct = default)
        {
            var list = NormalizeSymbols(request?.Symbols ?? []);
            if (list.Count == 0)
                return BadRequest(new { error = "At least one symbol is required", example = new { symbols = new[] { "AAPL", "MSFT" }, days = 10 } });

            var days = request?.Days > 0 ? request!.Days : 10;

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
