using Divitiae.Worker.Alpaca;
using Divitiae.Worker.Config;
using Divitiae.Worker.Strategy;
using Divitiae.Worker.Trading;
using Microsoft.Extensions.Options;

namespace Divitiae.Worker
{
    public class Worker(
        ILogger<Worker> logger,
        IOptions<AlpacaOptions> options,
        IOptions<WorkerOptions> workerOptions,
        IAlpacaTradingClient trading,
        IAlpacaMarketDataClient marketData,
        IBarCache barCache,
        IStrategy strategy,
        IClock clock) : BackgroundService
    {
        private readonly Dictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
        private TimeSpan InsufficientFundsCooldown => TimeSpan.FromMinutes(workerOptions.Value.InsufficientFundsCooldownMinutes);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opts = options.Value;
            logger.LogInformation("Starting Alpaca worker for symbols: {Symbols}", string.Join(",", opts.Symbols));

            // Preload bars
            foreach (var symbol in opts.Symbols)
            {
                logger.LogDebug("Seeding bars for {Symbol} (limit={Limit})", symbol, opts.BarsSeed);
                var seedBars = await marketData.GetMinuteBarsAsync(symbol, opts.BarsSeed, stoppingToken);
                barCache.Seed(symbol, seedBars);
                if (seedBars.Count > 0)
                {
                    var first = seedBars.First();
                    var last = seedBars.Last();
                    logger.LogDebug("Seeded {Count} bars for {Symbol} from {Start} to {End}", seedBars.Count, symbol, first.Time, last.Time);
                }
                else
                {
                    logger.LogWarning("No seed bars received for {Symbol}", symbol);
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var symbol in opts.Symbols)
                    {
                        var newBar = await marketData.GetLatestMinuteBarAsync(symbol, stoppingToken);
                        if (newBar != null)
                        {
                            barCache.Add(symbol, newBar);
                            logger.LogDebug("1m bar {Symbol} {Time} O={O} H={H} L={L} C={C} V={V}", symbol, newBar.Time, newBar.Open, newBar.High, newBar.Low, newBar.Close, newBar.Volume);

                            var decision = strategy.Evaluate(symbol, barCache.Get(symbol));
                            if (decision.Action == TradeAction.Buy)
                            {
                                await TryEnterLongAsync(symbol, decision, stoppingToken);
                            }
                            else if (decision.Action == TradeAction.Sell)
                            {
                                await TryExitLongAsync(symbol, stoppingToken);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in main loop");
                }

                await Task.Delay(TimeSpan.FromSeconds(opts.PollingIntervalSeconds), stoppingToken);
            }
        }

        private bool IsOnCooldown(string symbol)
        {
            if (_cooldownUntil.TryGetValue(symbol, out var until))
            {
                if (clock.UtcNow < until)
                {
                    logger.LogInformation("Skip enter for {Symbol}; cooldown until {Until}", symbol, until);
                    return true;
                }
                _cooldownUntil.Remove(symbol);
            }
            return false;
        }

        private void StartCooldown(string symbol, string reason)
        {
            var until = clock.UtcNow.Add(InsufficientFundsCooldown);
            _cooldownUntil[symbol] = until;
            logger.LogWarning("Cooldown {Symbol} until {Until}. Reason: {Reason}", symbol, until, reason);
        }

        private async Task TryEnterLongAsync(string symbol, TradeDecision decision, CancellationToken ct)
        {
            var opts = options.Value;

            if (IsOnCooldown(symbol)) return;

            // Market open check
            var marketOpen = await trading.IsMarketOpenAsync(ct);
            if (!marketOpen)
            {
                logger.LogInformation("Market closed. Skip buy for {Symbol}", symbol);
                return;
            }

            var hasPosition = await trading.HasOpenPositionAsync(symbol, ct);
            var hasOpenOrders = await trading.HasOpenOrdersAsync(symbol, ct);
            logger.LogInformation("Pre-checks {Symbol}: pos={HasPos} orders={HasOrd}", symbol, hasPosition, hasOpenOrders);

            if (hasPosition || hasOpenOrders) return;

            var account = await trading.GetAccountAsync(ct);

            // Determine target notional based on equity and configured fraction, but cap by available buying power
            var target = Math.Max(account.Equity * (decimal)opts.PositionNotionalFraction, (decimal)opts.MinNotionalUsd);
            var notional = Math.Min(account.BuyingPower, target);

            if (account.BuyingPower < (decimal)opts.MinNotionalUsd)
            {
                logger.LogInformation("Insufficient buying power {BP} < {Min} for {Symbol}", account.BuyingPower, (decimal)opts.MinNotionalUsd, symbol);
                StartCooldown(symbol, "Insufficient buying power");
                return;
            }

            var last = decision.ReferencePrice ?? barCache.GetLastClose(symbol) ?? 0m;
            if (last <= 0)
            {
                logger.LogWarning("Cannot price entry for {Symbol}", symbol);
                return;
            }

            var tp = last * (1m + (decimal)opts.TakeProfitPercent);
            var sl = last * (1m - (decimal)opts.StopLossPercent);

            try
            {
                await trading.SubmitBracketOrderNotionalAsync(new BracketOrderRequest
                {
                    Symbol = symbol,
                    Side = OrderSide.Buy,
                    NotionalUsd = decimal.Round(notional, 2),
                    TakeProfitLimitPrice = decimal.Round(tp, 2),
                    StopLossStopPrice = decimal.Round(sl, 2),
                    TimeInForce = opts.TimeInForce
                }, ct);

                logger.LogInformation("BUY submitted {Symbol}: notional={Notional} last={Last} tp={TP} sl/trail%={Trail}",
                    symbol, notional, last, decimal.Round(tp, 2), options.Value.TrailingStopPercent > 0 ? (decimal?)options.Value.TrailingStopPercent : null);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Order submit failed for {Symbol}; cooldown", symbol);
                StartCooldown(symbol, "Order submission failure");
            }
        }

        private async Task TryExitLongAsync(string symbol, CancellationToken ct)
        {
            var inPosition = await trading.HasOpenPositionAsync(symbol, ct);
            if (!inPosition)
            {
                logger.LogInformation("Skip exit; no position for {Symbol}", symbol);
                return;
            }

            logger.LogInformation("Flatten {Symbol}", symbol);
            await trading.ClosePositionAsync(symbol, ct);
        }
    }
}
