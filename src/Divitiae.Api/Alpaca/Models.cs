using System.Text.Json.Serialization;

namespace Divitiae.Api.Alpaca
{
    public class AlpacaOptions
    {
        public string TradingApiBaseUrl { get; set; } = "https://paper-api.alpaca.markets";
        public string MarketDataApiBaseUrl { get; set; } = "https://data.alpaca.markets";
        public string MarketDataFeed { get; set; } = "iex";
        public string ApiKeyId { get; set; } = string.Empty;
        public string ApiSecretKey { get; set; } = string.Empty;
    }

    public interface IAlpacaAssetClient
    {
        Task<IReadOnlyList<Asset>> GetAssetsAsync(CancellationToken ct);
        Task<decimal?> GetLastTradePriceAsync(string symbol, CancellationToken ct);
    }

    public record Asset
    {
        public required string Symbol { get; init; }
        public required string Name { get; init; }
        public string Exchange { get; init; } = string.Empty;
        public bool Tradable { get; init; }
        public bool Marginable { get; init; }
        public string Class { get; init; } = "us_equity";
        public string[] Attributes { get; init; } = Array.Empty<string>();
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
        [JsonPropertyName("unrealized_pl")] [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal UnrealizedPl { get; init; }
        [JsonPropertyName("side")] public string Side { get; init; } = string.Empty;
    }
}
