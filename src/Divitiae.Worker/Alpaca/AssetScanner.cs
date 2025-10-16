using Divitiae.Worker.Config;
using Microsoft.Extensions.Options;

namespace Divitiae.Worker.Alpaca
{
    public interface IAssetScanner
    {
        Task ScanAsync(CancellationToken ct);
    }

    public class AssetScanner(IAlpacaAssetClient assetClient, IOptions<ScannerOptions> options, ILogger<AssetScanner> logger) : IAssetScanner
    {
        private readonly TimeProvider _timeProvider = TimeProvider.System;

        public async Task ScanAsync(CancellationToken ct)
        {
            var opts = options.Value;
            if (!opts.Enabled)
            {
                logger.LogDebug("Scanner disabled");
                return;
            }

            var nowLocal = DateTimeOffset.Now;
            if (nowLocal.Hour < opts.StartHourLocal || nowLocal.Hour >= opts.EndHourLocal)
            {
                logger.LogDebug("Scanner skipped outside time window: now={Now} window={Start}-{End}", nowLocal, opts.StartHourLocal, opts.EndHourLocal);
                return;
            }

            logger.LogInformation("Starting asset scan at {Time} (window {Start}-{End} local, interval {Interval}m)", nowLocal, opts.StartHourLocal, opts.EndHourLocal, opts.IntervalMinutes);

            var assets = await assetClient.GetAssetsAsync(ct);
            var symbols = (opts.Symbols is { Length: > 0 }) ? opts.Symbols : assets.Where(a => a.Tradable).Select(a => a.Symbol).ToArray();

            var candidates = new List<(string Symbol, decimal PriceUsd)>();
            foreach (var symbol in symbols)
            {
                var priceUsd = await assetClient.GetLastTradePriceAsync(symbol, ct);
                if (priceUsd is null) continue;

                if (priceUsd.Value < opts.PriceThresholdUsd)
                {
                    candidates.Add((symbol, priceUsd.Value));
                }
            }

            candidates = candidates.OrderBy(c => c.PriceUsd).ToList();

            if (candidates.Count == 0)
            {
                logger.LogInformation("Scanner result: no symbols below {Threshold} USD", opts.PriceThresholdUsd);
                return;
            }

            logger.LogInformation("Scanner result ({Count} candidates under {Threshold} USD):", candidates.Count, opts.PriceThresholdUsd);
            foreach (var c in candidates)
            {
                logger.LogInformation(" - {Symbol}: last=${PriceUsd}", c.Symbol, Math.Round(c.PriceUsd, 2));
            }
        }
    }
}
