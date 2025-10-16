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

            _ui.RenderBanner("Divitiae Worker", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown", opts.Symbols);

            foreach (var symbol in opts.Symbols)
            {
                var seedBars = await marketData.GetMinuteBarsAsync(symbol, opts.BarsSeed, stoppingToken);
                barCache.Seed(symbol, seedBars);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                _ui.RenderCycleStart(clock.UtcNow);

                var cycleStart = clock.UtcNow;
                try
                {
                    var marketOpen = await trading.IsMarketOpenAsync(stoppingToken);
                    _ui.RenderMarketState(marketOpen, cycleStart);
                    logger.LogInformation("Market {State} at {Time}", marketOpen ? "OPEN" : "CLOSED", cycleStart);
                    if (!marketOpen)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(opts.PollingIntervalSeconds), stoppingToken);
                        _ui.RenderCycleEnd(clock.UtcNow - cycleStart);
                        logger.LogInformation("Cycle end (duration {Dur} ms)\n", (int)(clock.UtcNow - cycleStart).TotalMilliseconds);
                        continue;
                    }

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

                        var decisionLabel = decision.Action switch
                        {
                            TradeAction.Buy => "[green]BUY[/]",
                            TradeAction.Sell => "[red]SELL[/]",
                            _ => "[yellow]HOLD[/]"
                        };
                        var notes = decision.Action == TradeAction.Hold ? decision.Reason : null;

                        _ui.AddSymbolRow(symbol, bar.Close, changeAbs, changePct, decisionLabel, notes);

                        // Actions and file logs
                        if (decision.Action == TradeAction.Buy)
                        {
                            await TryEnterLongAsync(symbol, decision, stoppingToken);
                        }
                        else if (decision.Action == TradeAction.Sell)
                        {
                            await TryExitLongAsync(symbol, stoppingToken);
                        }
                        // No HOLD logs
                    }

                    _ui.RenderSymbolsTable();
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

                // Status spinner between cycles
                await AnsiConsole.Status()
                    .StartAsync("Waiting for next cycle...", async ctx =>
                    {
                        ctx.Spinner(Spinner.Known.Dots12);
                        ctx.Status("Waiting interval");
                        await Task.Delay(TimeSpan.FromSeconds(opts.PollingIntervalSeconds), stoppingToken);
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

        private async Task TryEnterLongAsync(string symbol, TradeDecision decision, CancellationToken ct)
        {
            var opts = options.Value;

            if (IsOnCooldown(symbol)) return;

            var hasPosition = await trading.HasOpenPositionAsync(symbol, ct);
            var hasOpenOrders = await trading.HasOpenOrdersAsync(symbol, ct);
            if (hasPosition || hasOpenOrders)
            {
                // Skip silently
                return;
            }

            var account = await trading.GetAccountAsync(ct);

            var target = Math.Max(account.Equity * (decimal)opts.PositionNotionalFraction, (decimal)opts.MinNotionalUsd);
            var notional = Math.Min(account.BuyingPower, target);

            var last = decision.ReferencePrice ?? barCache.GetLastClose(symbol) ?? 0m;
            if (account.BuyingPower < (decimal)opts.MinNotionalUsd || last <= 0)
            {
                // Skip silently but start cooldown if funds are insufficient
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
                    TakeProfitLimitPrice = 0,
                    StopLossStopPrice = 0,
                    TimeInForce = "day"
                }, ct);

                var pos = await trading.GetPositionAsync(symbol, ct);
                var entry = pos?.AvgEntryPrice > 0 ? pos!.AvgEntryPrice : last;
                _ui.RenderBuy(symbol, entry, notional);
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
                // Skip silently
                return;
            }

            await trading.ClosePositionAsync(symbol, ct);

            var exitPrice = pos.CurrentPrice;
            var entryPrice = pos.AvgEntryPrice;
            var pnlUsd = (exitPrice - entryPrice) * pos.Quantity;
            var pnlPct = entryPrice != 0 ? (exitPrice - entryPrice) / entryPrice * 100m : 0m;

            _ui.RenderSell(symbol, entryPrice, exitPrice, pos.Quantity, decimal.Round(pnlUsd, 5), decimal.Round(pnlPct, 5));
            logger.LogInformation("SELL {Symbol}: entry={Entry} exit≈{Exit} qty={Qty} PnL={PnlUsd} USD ({PnlPct:F2}%)", symbol, entryPrice, exitPrice, pos.Quantity, decimal.Round(pnlUsd, 2), pnlPct);
        }
    }
}
