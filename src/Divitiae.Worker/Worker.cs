using Divitiae.Worker.Alpaca;
using Divitiae.Worker.Config;
using Divitiae.Worker.Strategy;
using Divitiae.Worker.Trading;
using Microsoft.Extensions.Options;

namespace Divitiae.Worker
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IOptions<AlpacaOptions> _options;
        private readonly IOptions<ScannerOptions> _scannerOptions;
        private readonly IAlpacaTradingClient _trading;
        private readonly IAlpacaMarketDataClient _marketData;
        private readonly IAlpacaAssetClient _assetClient;
        private readonly IBarCache _barCache;
        private readonly IStrategy _strategy;
        private readonly IClock _clock;
        private readonly IAssetScanner _scanner;
        private DateTime _lastScanLocal = DateTime.MinValue;

        public Worker(
            ILogger<Worker> logger,
            IOptions<AlpacaOptions> options,
            IOptions<ScannerOptions> scannerOptions,
            IAlpacaTradingClient trading,
            IAlpacaMarketDataClient marketData,
            IAlpacaAssetClient assetClient,
            IBarCache barCache,
            IStrategy strategy,
            IClock clock,
            IAssetScanner scanner)
        {
            _logger = logger;
            _options = options;
            _scannerOptions = scannerOptions;
            _trading = trading;
            _marketData = marketData;
            _assetClient = assetClient;
            _barCache = barCache;
            _strategy = strategy;
            _clock = clock;
            _scanner = scanner;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opts = _options.Value;
            var scanOpts = _scannerOptions.Value;
            _logger.LogInformation("Starting Alpaca worker for symbols: {Symbols}", string.Join(",", opts.Symbols));

            // Preload bars
            foreach (var symbol in opts.Symbols)
            {
                _logger.LogInformation("Seeding bars for {Symbol} (limit={Limit})", symbol, opts.BarsSeed);
                var seedBars = await _marketData.GetMinuteBarsAsync(symbol, opts.BarsSeed, stoppingToken);
                _barCache.Seed(symbol, seedBars);
                if (seedBars.Count > 0)
                {
                    var first = seedBars.First();
                    var last = seedBars.Last();
                    _logger.LogInformation("Seeded {Count} bars for {Symbol} from {Start} to {End}", seedBars.Count, symbol, first.Time, last.Time);
                }
                else
                {
                    _logger.LogWarning("No seed bars received for {Symbol}", symbol);
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // hourly scanning between 08-23 local
                    await TryRunScanIfDueAsync(scanOpts, stoppingToken);

                    foreach (var symbol in opts.Symbols)
                    {
                        var newBar = await _marketData.GetLatestMinuteBarAsync(symbol, stoppingToken);
                        if (newBar != null)
                        {
                            _barCache.Add(symbol, newBar);
                            _logger.LogInformation("New 1m bar {Symbol} @ {Time} O={O} H={H} L={L} C={C} V={V}", symbol, newBar.Time, newBar.Open, newBar.High, newBar.Low, newBar.Close, newBar.Volume);

                            var decision = _strategy.Evaluate(symbol, _barCache.Get(symbol));
                            _logger.LogDebug("Decision for {Symbol}: {Action} ({Reason})", symbol, decision.Action, decision.Reason ?? "");
                            if (decision.Action == TradeAction.Buy)
                            {
                                await TryEnterLongAsync(symbol, decision, stoppingToken);
                            }
                            else if (decision.Action == TradeAction.Sell)
                            {
                                await TryExitLongAsync(symbol, stoppingToken);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("No latest bar available for {Symbol}", symbol);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in main loop");
                }

                await Task.Delay(TimeSpan.FromSeconds(opts.PollingIntervalSeconds), stoppingToken);
            }
        }

        private async Task TryRunScanIfDueAsync(ScannerOptions scanOpts, CancellationToken ct)
        {
            if (!scanOpts.Enabled) return;
            var now = DateTime.Now;
            if (now.Hour < scanOpts.StartHourLocal || now.Hour >= scanOpts.EndHourLocal)
                return;

            if (_lastScanLocal == DateTime.MinValue || (now - _lastScanLocal).TotalMinutes >= scanOpts.IntervalMinutes)
            {
                _lastScanLocal = now;
                _logger.LogInformation("Running hourly scanner at {Now} (window {Start}-{End} local)", now, scanOpts.StartHourLocal, scanOpts.EndHourLocal);
                await _scanner.ScanAsync(ct);
            }
        }

        private async Task TryEnterLongAsync(string symbol, TradeDecision decision, CancellationToken ct)
        {
            var opts = _options.Value;

            var hasPosition = await _trading.HasOpenPositionAsync(symbol, ct);
            var hasOpenOrders = await _trading.HasOpenOrdersAsync(symbol, ct);
            _logger.LogInformation("Pre-checks {Symbol}: hasPosition={HasPos} hasOpenOrders={HasOrd}", symbol, hasPosition, hasOpenOrders);

            if (hasPosition)
            {
                _logger.LogInformation("Skip enter; already in position for {Symbol}", symbol);
                return;
            }
            if (hasOpenOrders)
            {
                _logger.LogInformation("Skip enter; open orders exist for {Symbol}", symbol);
                return;
            }

            var account = await _trading.GetAccountAsync(ct);
            var notional = Math.Max(account.Equity * (decimal)opts.PositionNotionalFraction, (decimal)opts.MinNotionalUsd);
            var last = decision.ReferencePrice ?? _barCache.GetLastClose(symbol) ?? 0m;
            if (last <= 0)
            {
                _logger.LogWarning("Cannot price entry for {Symbol}", symbol);
                return;
            }

            var tp = last * (1m + (decimal)opts.TakeProfitPercent);
            var sl = last * (1m - (decimal)opts.StopLossPercent);

            _logger.LogInformation("Submitting bracket buy for {Symbol}: notional={Notional} last={Last} tp={TP} sl={SL}", symbol, notional, last, tp, sl);

            await _trading.SubmitBracketOrderNotionalAsync(new BracketOrderRequest
            {
                Symbol = symbol,
                Side = OrderSide.Buy,
                NotionalUsd = notional,
                TakeProfitLimitPrice = decimal.Round(tp, 2),
                StopLossStopPrice = decimal.Round(sl, 2),
                TimeInForce = opts.TimeInForce
            }, ct);
        }

        private async Task TryExitLongAsync(string symbol, CancellationToken ct)
        {
            var inPosition = await _trading.HasOpenPositionAsync(symbol, ct);
            if (!inPosition)
            {
                _logger.LogInformation("Skip exit; no position for {Symbol}", symbol);
                return;
            }

            _logger.LogInformation("Flattening position for {Symbol}");
            await _trading.ClosePositionAsync(symbol, ct);
        }
    }
}
