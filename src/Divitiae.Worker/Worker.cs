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

        private static decimal Floor2(decimal x) => Math.Floor(x * 100m) / 100m;
        private static decimal Ceil2(decimal x) => Math.Ceiling(x * 100m) / 100m;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opts = options.Value;
            logger.LogInformation("Cycle start: symbols=[{Symbols}]", string.Join(",", opts.Symbols));

            // Preload bars once at startup (noisy details suppressed)
            foreach (var symbol in opts.Symbols)
            {
                var seedBars = await marketData.GetMinuteBarsAsync(symbol, opts.BarsSeed, stoppingToken);
                barCache.Seed(symbol, seedBars);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var cycleStart = clock.UtcNow;
                try
                {
                    var marketOpen = await trading.IsMarketOpenAsync(stoppingToken);
                    logger.LogInformation("Market {State} at {Time}", marketOpen ? "OPEN" : "CLOSED", cycleStart);
                    if (!marketOpen)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(opts.PollingIntervalSeconds), stoppingToken);
                        logger.LogInformation("Cycle end: market closed (duration {Dur} ms)", (int)(clock.UtcNow - cycleStart).TotalMilliseconds);
                        continue;
                    }

                    foreach (var symbol in opts.Symbols)
                    {
                        var bar = await marketData.GetLatestMinuteBarAsync(symbol, stoppingToken);
                        if (bar is null) continue;
                        barCache.Add(symbol, bar);

                        var decision = strategy.Evaluate(symbol, barCache.Get(symbol));

                        // Price change info from previous close if available
                        var prevClose = barCache.Get(symbol).Count > 1 ? barCache.Get(symbol)[^2].Close : (decimal?)null;
                        decimal? changePct = prevClose.HasValue && prevClose.Value != 0 ? (bar.Close - prevClose.Value) / prevClose.Value * 100m : null;

                        logger.LogInformation("Checked {Symbol}: close={Close} {Delta}",
                            symbol,
                            bar.Close,
                            changePct.HasValue ? $"change={changePct.Value:F2}%" : "change=n/a");

                        if (decision.Action == TradeAction.Buy)
                        {
                            await TryEnterLongAsync(symbol, decision, stoppingToken);
                        }
                        else if (decision.Action == TradeAction.Sell)
                        {
                            await TryExitLongAsync(symbol, stoppingToken);
                        }
                        else if (decision.Action == TradeAction.Hold && decision.LastClose.HasValue && decision.EmaShort.HasValue && decision.EmaLong.HasValue)
                        {
                            logger.LogInformation("HOLD {Symbol}: price={P} emaS={S} emaL={L} reason={Reason}", symbol, decision.LastClose.Value, decision.EmaShort.Value, decision.EmaLong.Value, decision.Reason ?? "");
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    // benign cancellation due to HTTP timeouts or shutdown
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in cycle");
                }
                finally
                {
                    logger.LogInformation("Cycle end (duration {Dur} ms)", (int)(clock.UtcNow - cycleStart).TotalMilliseconds);
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
                    logger.LogInformation("CD {Symbol} until {Until}", symbol, until);
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
            logger.LogWarning("CD start {Symbol} -> {Until}. reason={Reason}", symbol, until, reason);
        }

        private async Task TryEnterLongAsync(string symbol, TradeDecision decision, CancellationToken ct)
        {
            var opts = options.Value;

            if (IsOnCooldown(symbol)) return;

            var hasPosition = await trading.HasOpenPositionAsync(symbol, ct);
            var hasOpenOrders = await trading.HasOpenOrdersAsync(symbol, ct);
            if (hasPosition || hasOpenOrders)
            {
                logger.LogInformation("Skip BUY {Symbol}: pos={Pos} ord={Ord}", symbol, hasPosition, hasOpenOrders);
                return;
            }

            var account = await trading.GetAccountAsync(ct);

            var target = Math.Max(account.Equity * (decimal)opts.PositionNotionalFraction, (decimal)opts.MinNotionalUsd);
            var notional = Math.Min(account.BuyingPower, target);

            var last = decision.ReferencePrice ?? barCache.GetLastClose(symbol) ?? 0m;
            if (account.BuyingPower < (decimal)opts.MinNotionalUsd || last <= 0)
            {
                logger.LogInformation("Skip BUY {Symbol}: bp={BP} min={Min} price={Price}", symbol, account.BuyingPower, (decimal)opts.MinNotionalUsd, last);
                if (account.BuyingPower < (decimal)opts.MinNotionalUsd) StartCooldown(symbol, "Insufficient buying power");
                return;
            }

            try
            {
                await trading.SubmitBracketOrderNotionalAsync(new BracketOrderRequest
                {
                    Symbol = symbol,
                    Side = OrderSide.Buy,
                    NotionalUsd = decimal.Round(notional, 2),
                    TakeProfitLimitPrice = 0, // ignored in simple order
                    StopLossStopPrice = 0,    // ignored in simple order
                    TimeInForce = "day"
                }, ct);

                // Try to fetch fresh position snapshot after order (may still be pending)
                var pos = await trading.GetPositionAsync(symbol, ct);
                var entry = pos?.AvgEntryPrice > 0 ? pos!.AvgEntryPrice : last;
                logger.LogInformation("BUY {Symbol}: entry={Entry} notional={Notional}", symbol, entry, notional);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BUY FAIL {Symbol}", symbol);
                StartCooldown(symbol, "Order submission failure");
            }
        }

        private async Task TryExitLongAsync(string symbol, CancellationToken ct)
        {
            var pos = await trading.GetPositionAsync(symbol, ct);
            if (pos is null || pos.Quantity <= 0)
            {
                logger.LogInformation("Skip SELL {Symbol}: no position", symbol);
                return;
            }

            await trading.ClosePositionAsync(symbol, ct);

            // After close, we may not immediately know fill price; log with current and entry to estimate
            var exitPrice = pos.CurrentPrice;
            var entryPrice = pos.AvgEntryPrice;
            var pnlUsd = (exitPrice - entryPrice) * pos.Quantity;
            var pnlPct = entryPrice != 0 ? (exitPrice - entryPrice) / entryPrice * 100m : 0m;

            logger.LogInformation("SELL {Symbol}: entry={Entry} exit?{Exit} qty={Qty} PnL={PnlUsd} USD ({PnlPct:F2}%)", symbol, entryPrice, exitPrice, pos.Quantity, decimal.Round(pnlUsd, 2), pnlPct);
        }
    }
}
