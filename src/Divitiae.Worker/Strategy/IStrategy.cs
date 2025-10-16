using Divitiae.Worker.Trading;

namespace Divitiae.Worker.Strategy
{
    public enum TradeAction { Hold, Buy, Sell }

    public record TradeDecision
    {
        public TradeAction Action { get; init; } = TradeAction.Hold;
        public decimal? ReferencePrice { get; init; }
        public string? Reason { get; init; }
    }

    public interface IStrategy
    {
        TradeDecision Evaluate(string symbol, IReadOnlyList<Bar> bars);
    }
}
