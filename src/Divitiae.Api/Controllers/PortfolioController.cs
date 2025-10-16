using Divitiae.Api.Alpaca;
using Microsoft.AspNetCore.Mvc;

namespace Divitiae.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PortfolioController(IAlpacaTradingClient trading, ILogger<PortfolioController> logger) : ControllerBase
    {
        // GET api/portfolio
        [HttpGet]
        public async Task<IActionResult> GetPortfolio(CancellationToken ct)
        {
            var account = await trading.GetAccountAsync(ct);
            var positions = await trading.GetPositionsAsync(ct);
            logger.LogInformation("Portfolio requested: positions={Count}", positions.Count);
            return Ok(new
            {
                account,
                positions,
                totals = new
                {
                    marketValue = positions.Sum(p => p.MarketValue),
                    unrealizedPl = positions.Sum(p => p.UnrealizedPl),
                    count = positions.Count
                }
            });
        }

        // POST api/portfolio/close/AAPL
        [HttpPost("close/{symbol}")]
        public async Task<IActionResult> ClosePosition([FromRoute] string symbol, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return BadRequest(new { error = "symbol is required" });
            await trading.ClosePositionAsync(symbol, ct);
            logger.LogInformation("Close request sent for {Symbol}", symbol);
            return Accepted(new { symbol = symbol.ToUpperInvariant(), status = "close_submitted" });
        }
    }
}
