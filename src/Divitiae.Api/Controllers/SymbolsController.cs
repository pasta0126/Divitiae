using Divitiae.Api.Alpaca;
using Microsoft.AspNetCore.Mvc;

namespace Divitiae.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SymbolsController : ControllerBase
    {
        private readonly IAlpacaAssetClient _alpaca;
        private readonly IAlpacaMarketDataClient _marketData;
        private readonly ILogger<SymbolsController> _logger;

        public SymbolsController(IAlpacaAssetClient alpaca, IAlpacaMarketDataClient marketData, ILogger<SymbolsController> logger)
        {
            _alpaca = alpaca;
            _marketData = marketData;
            _logger = logger;
        }

        // GET api/symbols/status?symbols=AAPL&symbols=MSFT&days=10
        // Also supports comma-separated list: symbols=AAPL,MSFT,SPY
        [HttpGet("status")]
        public async Task<IActionResult> GetSymbolsStatus([FromQuery] string[] symbols, [FromQuery] int days = 10, CancellationToken ct = default)
        {
            var list = NormalizeSymbols(symbols);
            if (list.Count == 0)
                return BadRequest(new { error = "At least one symbol is required", example = "api/symbols/status?symbols=AAPL&symbols=MSFT" });

            if (days <= 0) days = 10;

            var assets = await _alpaca.GetAssetsAsync(ct);
            var assetMap = assets.ToDictionary(a => a.Symbol, StringComparer.OrdinalIgnoreCase);

            var items = new List<object>();
            foreach (var s in list.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                assetMap.TryGetValue(s, out var asset);

                var bars = await _marketData.GetDailyBarsAsync(s, days, ct);
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

            _logger.LogInformation("Symbols/status for {Count} symbols (days={Days})", items.Count, days);
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
