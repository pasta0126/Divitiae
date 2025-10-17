namespace Divitiae.Worker.Trading
{
    using System.Text.Json.Serialization;

    public enum OrderSide { Buy, Sell }

    public record BracketOrderRequest
    {
        public required string Symbol { get; init; }
        public required OrderSide Side { get; init; }
        public required decimal NotionalUsd { get; init; }
        public required decimal TakeProfitLimitPrice { get; init; }
        public required decimal StopLossStopPrice { get; init; }
        public string TimeInForce { get; init; } = "gtc";
    }

    public record Account
    {
        [JsonPropertyName("buying_power")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public decimal BuyingPower { get; init; }

        [JsonPropertyName("equity")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public decimal Equity { get; init; }

        [JsonPropertyName("currency")]
        public string Currency { get; init; } = "USD";
    }

    public record Position
    {
        [JsonPropertyName("symbol")] public string Symbol { get; init; } = string.Empty;
        [JsonPropertyName("qty")] [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal Quantity { get; init; }
        [JsonPropertyName("avg_entry_price")] [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal AvgEntryPrice { get; init; }
        [JsonPropertyName("current_price")] [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal CurrentPrice { get; init; }
        [JsonPropertyName("market_value")] [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal MarketValue { get; init; }
        [JsonPropertyName("side")] public string Side { get; init; } = string.Empty;
    }

    public record Bar
    {
        public DateTime Time { get; init; }
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public long Volume { get; init; }
    }

    public record MarketClock
    {
        public bool IsOpen { get; init; }
        public DateTime? NextOpen { get; init; }
        public DateTime? NextClose { get; init; }
    }

    public interface IAlpacaTradingClient
    {
        Task<Account> GetAccountAsync(CancellationToken ct);
        Task<bool> HasOpenPositionAsync(string symbol, CancellationToken ct);
        Task<bool> HasOpenOrdersAsync(string symbol, CancellationToken ct);
        Task SubmitBracketOrderNotionalAsync(BracketOrderRequest request, CancellationToken ct);
        Task ClosePositionAsync(string symbol, CancellationToken ct);
        Task<bool> IsMarketOpenAsync(CancellationToken ct);
        Task<Position?> GetPositionAsync(string symbol, CancellationToken ct);
        Task<MarketClock> GetClockAsync(CancellationToken ct);
    }

    public interface IAlpacaMarketDataClient
    {
        Task<IReadOnlyList<Bar>> GetMinuteBarsAsync(string symbol, int limit, CancellationToken ct);
        Task<Bar?> GetLatestMinuteBarAsync(string symbol, CancellationToken ct);
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
