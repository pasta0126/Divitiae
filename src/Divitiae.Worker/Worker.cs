using Divitiae.Worker.Alpaca;
using Divitiae.Worker.Config;
using Divitiae.Worker.Strategy;
using Divitiae.Worker.Trading;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Divitiae.Worker.ConsoleUi;

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
        private readonly IConsoleRenderer _ui = new SpectreConsoleRenderer();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opts = options.Value;

            _ui.RenderBanner("Divitiae Worker", opts.Symbols);

            foreach (var symbol in opts.Symbols)
            {
                var seedBars = await marketData.GetMinuteBarsAsync(symbol, opts.BarsSeed, stoppingToken);
                barCache.Seed(symbol, seedBars);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                _ui.RenderCycleStart(clock.UtcNow);

                var cycleStart = clock.UtcNow;
                TimeSpan nextDelay = TimeSpan.FromSeconds(opts.PollingIntervalSeconds);
                string nextDelayMessage = "Waiting for next cycle...";
                try
                {
                    // Consult clock for precise scheduling
                    var clockInfo = await trading.GetClockAsync(stoppingToken);
                    var marketOpen = clockInfo.IsOpen;
                    _ui.RenderMarketState(marketOpen, cycleStart);
                    logger.LogInformation("Market {State} at {Time}", marketOpen ? "OPEN" : "CLOSED", cycleStart);

                    if (!marketOpen)
                    {
                        // Market closed: wait until next official open if available; fallback to 15 minutes
                        if (clockInfo.NextOpen.HasValue)
                        {
                            var waitUntil = clockInfo.NextOpen.Value.ToUniversalTime();
                            var delay = waitUntil - clock.UtcNow;
                            if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);
                            nextDelay = delay;
                            nextDelayMessage = $"Market closed. Waiting until next open at {waitUntil:u} ({delay:c})...";
                        }
                        else
                        {
                            nextDelay = TimeSpan.FromMinutes(15);
                            nextDelayMessage = "Market closed. Waiting 15 minutes...";
                        }
                    }
                    else
                    {
                        // Symbols table
                        _ui.BeginSymbolsTable();

                        foreach (var symbol in opts.Symbols)
                        {
                            var bar = await marketData.GetLatestMinuteBarAsync(symbol, stoppingToken);
                            if (bar is null) continue;
                            barCache.Add(symbol, bar);

                            var decision = strategy.Evaluate(symbol, barCache.Get(symbol));

                            var prevClose = barCache.Get(symbol).Count > 1 ? barCache.Get(symbol)[^2].Close : (decimal?)null;
                            decimal? changeAbs = prevClose.HasValue ? bar.Close - prevClose.Value : null;
                            decimal? changePct = prevClose.HasValue && prevClose.Value != 0 ? (bar.Close - prevClose.Value) / prevClose.Value * 100m : null;

                            string decisionLabel = "[yellow]HOLD[/]";
                            string? notes = null;

                            if (decision.Action == TradeAction.Buy)
                            {
                                var executed = await TryEnterLongAsync(symbol, decision, stoppingToken);
                                decisionLabel = executed ? "[green]BUY[/]" : "[yellow]HOLD[/]";
                            }
                            else if (decision.Action == TradeAction.Sell)
                            {
                                var executed = await TryExitLongAsync(symbol, stoppingToken);
                                decisionLabel = executed ? "[red]SELL[/]" : "[yellow]HOLD[/]";
                            }
                            else
                            {
                                // Strategy decided to hold
                                notes = decision.Reason;
                            }

                            _ui.AddSymbolRow(symbol, bar.Close, changeAbs, changePct, decisionLabel, notes);
                        }

                        _ui.RenderSymbolsTable();
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in cycle");
                }
                finally
                {
                    _ui.RenderCycleEnd(clock.UtcNow - cycleStart);
                    logger.LogInformation("Cycle end (duration {Dur} ms)\n", (int)(clock.UtcNow - cycleStart).TotalMilliseconds);
                }

                // Unified wait using dynamic delay
                await AnsiConsole.Status()
                    .StartAsync(nextDelayMessage, async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots12);
                        ctx.Status(nextDelayMessage);
                        await Task.Delay(nextDelay, stoppingToken);
                    });
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

        private async Task<bool> TryEnterLongAsync(string symbol, TradeDecision decision, CancellationToken ct)
        {
            var opts = options.Value;

            if (IsOnCooldown(symbol)) return false;

            var hasPosition = await trading.HasOpenPositionAsync(symbol, ct);
            var hasOpenOrders = await trading.HasOpenOrdersAsync(symbol, ct);
            if (hasPosition || hasOpenOrders)
            {
                // Skip silently
                return false;
            }

            var account = await trading.GetAccountAsync(ct);

            var target = Math.Max(account.Equity * (decimal)opts.PositionNotionalFraction, (decimal)opts.MinNotionalUsd);
            var notional = Math.Min(account.BuyingPower, target);

            var last = decision.ReferencePrice ?? barCache.GetLastClose(symbol) ?? 0m;
            if (account.BuyingPower < (decimal)opts.MinNotionalUsd || last <= 0)
            {
                if (account.BuyingPower < (decimal)opts.MinNotionalUsd) StartCooldown(symbol, "Insufficient buying power");
                return false;
            }

            try
            {
                await trading.SubmitBracketOrderNotionalAsync(new BracketOrderRequest
                {
                    Symbol = symbol,
                    Side = OrderSide.Buy,
                    NotionalUsd = decimal.Round(notional, 2),
                    TakeProfitLimitPrice = 0,
                    StopLossStopPrice = 0,
                    TimeInForce = "day"
                }, ct);

                var pos = await trading.GetPositionAsync(symbol, ct);
                var entry = pos?.AvgEntryPrice > 0 ? pos!.AvgEntryPrice : last;
                _ui.RenderBuy(symbol, entry, notional);
                logger.LogInformation("BUY {Symbol}: entry={Entry} notional={Notional}", symbol, entry, notional);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "BUY FAIL {Symbol}", symbol);
                StartCooldown(symbol, "Order submission failure");
                return false;
            }
        }

        private async Task<bool> TryExitLongAsync(string symbol, CancellationToken ct)
        {
            var pos = await trading.GetPositionAsync(symbol, ct);
            if (pos is null || pos.Quantity <= 0)
            {
                // Skip silently
                return false;
            }

            await trading.ClosePositionAsync(symbol, ct);

            var exitPrice = pos.CurrentPrice;
            var entryPrice = pos.AvgEntryPrice;
            var pnlUsd = (exitPrice - entryPrice) * pos.Quantity;
            var pnlPct = entryPrice != 0 ? (exitPrice - entryPrice) / entryPrice * 100m : 0m;

            _ui.RenderSell(symbol, entryPrice, exitPrice, pos.Quantity, decimal.Round(pnlUsd, 5), decimal.Round(pnlPct, 5));
            logger.LogInformation("SELL {Symbol}: entry={Entry} exit≈{Exit} qty={Qty} PnL={PnlUsd} USD ({PnlPct:F2}%)", symbol, entryPrice, exitPrice, pos.Quantity, decimal.Round(pnlUsd, 2), pnlPct);
            return true;
        }
    }
}
