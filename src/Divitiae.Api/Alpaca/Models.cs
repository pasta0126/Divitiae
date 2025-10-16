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
}
