using Spectre.Console;

namespace Divitiae.Worker.ConsoleUi
{
    internal static class ConsoleFormat
    {
        public static string Dec(decimal value)
        {
            var rounded = Math.Round(value, 5, MidpointRounding.AwayFromZero);
            return TrimZeros(rounded);
        }

        public static string? Dec(decimal? value)
            => value.HasValue ? Dec(value.Value) : null;

        public static string SignedDec(decimal value)
        {
            var s = Dec(value);
            if (!s.StartsWith("-") && value != 0) s = "+" + s;
            return s;
        }

        public static string? SignedDec(decimal? value)
            => value.HasValue ? SignedDec(value.Value) : null;

        public static string Pct(decimal value)
        {
            // value is already the percent number, e.g. 1.23 means 1.23%
            var rounded = Math.Round(value, 5, MidpointRounding.AwayFromZero);
            var s = TrimZeros(rounded);
            if (!s.StartsWith("-") && value != 0) s = "+" + s;
            return s + "%";
        }

        public static string? Pct(decimal? value)
            => value.HasValue ? Pct(value.Value) : null;

        private static string TrimZeros(decimal value)
        {
            var s = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (s.Contains('.'))
            {
                s = s.TrimEnd('0').TrimEnd('.');
            }
            return s;
        }
    }

    public interface IConsoleRenderer
    {
        void RenderBanner(string appName, string environment, string[] symbols);
        void RenderCycleStart(DateTime utcNow);
        void RenderMarketState(bool isOpen, DateTime utcNow);
        void BeginSymbolsTable();
        void AddSymbolRow(string symbol, decimal close, decimal? changeAbs, decimal? changePct, string decisionLabel, string? notes = null);
        void RenderSymbolsTable();
        void RenderBuy(string symbol, decimal entry, decimal notional);
        void RenderSell(string symbol, decimal entry, decimal exitApprox, decimal qty, decimal pnlUsd, decimal pnlPct);
        void RenderSkipBuy(string symbol, bool hasPos, bool hasOrd);
        void RenderHold(string symbol, string? reason);
        void RenderCycleEnd(TimeSpan duration);
    }

    public class SpectreConsoleRenderer : IConsoleRenderer
    {
        private Table? _table;

        public void RenderBanner(string appName, string environment, string[] symbols)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new FigletText(appName).Color(Color.Cyan1));
            AnsiConsole.MarkupLine("[grey]Environment:[/] [bold]{0}[/]", environment);
            if (symbols.Length > 0)
                AnsiConsole.MarkupLine("[grey]Symbols:[/] [yellow]{0}[/]", string.Join(", ", symbols));
            AnsiConsole.WriteLine();
        }

        public void RenderCycleStart(DateTime utcNow)
        {
            AnsiConsole.MarkupLine("\n[bold yellow]Iteration start[/] [grey]UTC {0:HH:mm:ss}[/]\n", utcNow);
        }

        public void RenderMarketState(bool isOpen, DateTime utcNow)
        {
            var state = isOpen ? "[green]MARKET OPEN[/]" : "[red]MARKET CLOSED[/]";
            AnsiConsole.MarkupLine("{0} [grey]{1:HH:mm:ss} UTC[/]", state, utcNow);
        }

        public void BeginSymbolsTable()
        {
            _table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .Title(new TableTitle("[bold]Analyzed symbols[/]"));

            _table.AddColumn(new TableColumn("[grey]Symbol[/]").Centered());
            _table.AddColumn(new TableColumn("[grey]Close[/]").Centered());
            _table.AddColumn(new TableColumn("[grey]Δ (USD/%)[/]").Centered());
            _table.AddColumn(new TableColumn("[grey]Decision[/]").Centered());
            _table.AddColumn(new TableColumn("[grey]Notes[/]").Centered());
        }

        public void AddSymbolRow(string symbol, decimal close, decimal? changeAbs, decimal? changePct, string decisionLabel, string? notes = null)
        {
            if (_table is null) return;
            var changeAbsTxt = changeAbs.HasValue ? ConsoleFormat.SignedDec(changeAbs.Value) : "n/a";
            var changePctTxt = changePct.HasValue ? ConsoleFormat.Pct(changePct.Value) : "n/a";
            var color = changePct.HasValue ? (changePct.Value >= 0 ? "green" : "red") : "grey";
            var delta = $"[{color}]{changeAbsTxt} ({changePctTxt})[/]";
            _table.AddRow(
                $"[bold]{symbol.ToUpperInvariant()}[/]",
                $"[cyan]{ConsoleFormat.Dec(close)}[/]",
                delta,
                decisionLabel,
                string.IsNullOrWhiteSpace(notes) ? "" : $"[dim]{notes}[/]");
        }

        public void RenderSymbolsTable()
        {
            if (_table is null) return;
            AnsiConsole.Write(_table);
            AnsiConsole.WriteLine();
            _table = null;
        }

        public void RenderHold(string symbol, string? reason) { }
        public void RenderBuy(string symbol, decimal entry, decimal notional) { }
        public void RenderSell(string symbol, decimal entry, decimal exitApprox, decimal qty, decimal pnlUsd, decimal pnlPct) { }
        public void RenderSkipBuy(string symbol, bool hasPos, bool hasOrd) { }

        public void RenderCycleEnd(TimeSpan duration)
        {
            var rule = new Rule($"Cycle end ({(int)duration.TotalMilliseconds} ms)")
            {
                Style = Style.Plain.Foreground(Color.Grey),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(rule);
            AnsiConsole.WriteLine();
        }
    }
}
