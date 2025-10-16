using Divitiae.Worker.Config;
using Divitiae.Worker.Trading;
using Microsoft.Extensions.Options;

namespace Divitiae.Worker.Strategy
{
    public class EmaCrossoverStrategy(IOptions<AlpacaOptions> options, ILogger<EmaCrossoverStrategy> logger) : IStrategy
    {
        private readonly AlpacaOptions _options = options.Value;

        public TradeDecision Evaluate(string symbol, IReadOnlyList<Bar> bars)
        {
            if (bars.Count < Math.Max(_options.EmaShortPeriod, _options.EmaLongPeriod) + 2)
            {
                return new TradeDecision { Action = TradeAction.Hold, Reason = "Insufficient bars", LastClose = bars.LastOrDefault()?.Close };
            }

            var closes = bars.Select(b => b.Close).ToList();
            var emaS = Ema(closes, _options.EmaShortPeriod);
            var emaL = Ema(closes, _options.EmaLongPeriod);

            var last = closes[^1];
            var prevShort = emaS[^2];
            var prevLong = emaL[^2];
            var currShort = emaS[^1];
            var currLong = emaL[^1];

            var prevCrossUp = prevShort <= prevLong && currShort > currLong;
            var prevCrossDown = prevShort >= prevLong && currShort < currLong;

            if (prevCrossUp)
            {
                return new TradeDecision { Action = TradeAction.Buy, ReferencePrice = last, Reason = $"EMA{_options.EmaShortPeriod} crossed above EMA{_options.EmaLongPeriod}", LastClose = last, EmaShort = currShort, EmaLong = currLong };
            }
            if (prevCrossDown)
            {
                return new TradeDecision { Action = TradeAction.Sell, ReferencePrice = last, Reason = $"EMA{_options.EmaShortPeriod} crossed below EMA{_options.EmaLongPeriod}", LastClose = last, EmaShort = currShort, EmaLong = currLong };
            }
            return new TradeDecision { Action = TradeAction.Hold, Reason = "No crossover", LastClose = last, EmaShort = currShort, EmaLong = currLong };
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
