using Divitiae.Api.Alpaca;
using Microsoft.AspNetCore.Mvc;

namespace Divitiae.Api.Controllers
{
    [ApiController]
    [Route("api/market")]
    public class MarketController(IAlpacaTradingClient trading, ILogger<MarketController> logger) : ControllerBase
    {
        // GET api/market/clock
        [HttpGet("clock")]
        public async Task<IActionResult> GetClock(CancellationToken ct)
        {
            var clock = await trading.GetClockAsync(ct);
            logger.LogInformation("Clock: isOpen={IsOpen} nextOpen={NextOpen} nextClose={NextClose}", clock.IsOpen, clock.NextOpen, clock.NextClose);
            return Ok(clock);
        }

        // GET api/market/is-open
        [HttpGet("is-open")]
        public async Task<IActionResult> IsOpen(CancellationToken ct)
        {
            var isOpen = await trading.IsMarketOpenAsync(ct);
            return Ok(new { isOpen });
        }
    }
}
