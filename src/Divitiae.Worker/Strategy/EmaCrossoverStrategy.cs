using Divitiae.Worker.Config;
using Divitiae.Worker.Trading;
using Microsoft.Extensions.Options;

namespace Divitiae.Worker.Strategy
{
    public class EmaCrossoverStrategy : IStrategy
    {
        private readonly AlpacaOptions _options;
        private readonly ILogger<EmaCrossoverStrategy> _logger;

        public EmaCrossoverStrategy(IOptions<AlpacaOptions> options, ILogger<EmaCrossoverStrategy> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public TradeDecision Evaluate(string symbol, IReadOnlyList<Bar> bars)
        {
            if (bars.Count < Math.Max(_options.EmaShortPeriod, _options.EmaLongPeriod) + 2)
            {
                return new TradeDecision { Action = TradeAction.Hold, Reason = "Insufficient bars" };
            }

            var closes = bars.Select(b => b.Close).ToList();
            var emaS = Ema(closes, _options.EmaShortPeriod);
            var emaL = Ema(closes, _options.EmaLongPeriod);

            var last = closes[^1];
            var prevShort = emaS[^2];
            var prevLong = emaL[^2];
            var currShort = emaS[^1];
            var currLong = emaL[^1];

            _logger.LogDebug("EMA snapshot {Symbol}: close={Close} ema{Short}={EmaS} ema{Long}={EmaL}", symbol, last, _options.EmaShortPeriod, currShort, _options.EmaLongPeriod, currLong);

            var prevCrossUp = prevShort <= prevLong && currShort > currLong;
            var prevCrossDown = prevShort >= prevLong && currShort < currLong;

            if (prevCrossUp)
            {
                _logger.LogInformation("BUY signal {Symbol}: EMA{Short} crossed above EMA{Long}", symbol, _options.EmaShortPeriod, _options.EmaLongPeriod);
                return new TradeDecision { Action = TradeAction.Buy, ReferencePrice = last, Reason = "EMA short crossed above EMA long" };
            }
            if (prevCrossDown)
            {
                _logger.LogInformation("SELL signal {Symbol}: EMA{Short} crossed below EMA{Long}", symbol, _options.EmaShortPeriod, _options.EmaLongPeriod);
                return new TradeDecision { Action = TradeAction.Sell, ReferencePrice = last, Reason = "EMA short crossed below EMA long" };
            }
            return new TradeDecision { Action = TradeAction.Hold };
        }

        private static List<decimal> Ema(List<decimal> values, int period)
        {
            var result = new List<decimal>(values.Count);
            if (values.Count == 0) return result;
            var k = 2m / (period + 1);
            decimal? ema = null;
            for (int i = 0; i < values.Count; i++)
            {
                var price = values[i];
                if (ema is null)
                {
                    if (values.Count >= period)
                    {
                        var seedCount = Math.Min(period, i + 1);
                        ema = values.Take(seedCount).Average();
                    }
                    else
                    {
                        ema = price;
                    }
                }
                else
                {
                    ema = price * k + ema.Value * (1 - k);
                }
                result.Add(ema.Value);
            }
            return result;
        }
    }
}
