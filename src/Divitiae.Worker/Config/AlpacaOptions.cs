namespace Divitiae.Worker.Config
{
    public class AlpacaOptions
    {
        public string ApiKeyId { get; set; } = string.Empty;
        public string ApiSecretKey { get; set; } = string.Empty;
        public string TradingApiBaseUrl { get; set; } = string.Empty;
        public string MarketDataApiBaseUrl { get; set; } = string.Empty;
        public string[] Symbols { get; set; } = [];
        public int EmaShortPeriod { get; set; } = 5;
        public int EmaLongPeriod { get; set; } = 20;
        public double PositionNotionalFraction { get; set; } = 0.10;
        public double MinNotionalUsd { get; set; } = 1.0;
        public double TakeProfitPercent { get; set; } = 0.02;
        public double StopLossPercent { get; set; } = 0.01;
        public int PollingIntervalSeconds { get; set; } = 15;
        public int BarsSeed { get; set; } = 100;
        public string TimeInForce { get; set; } = "gtc";
    }
}
