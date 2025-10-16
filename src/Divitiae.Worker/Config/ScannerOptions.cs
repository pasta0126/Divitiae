namespace Divitiae.Worker.Config
{
    public class ScannerOptions
    {
        public bool Enabled { get; set; } = true;
        // Local time window [StartHourLocal, EndHourLocal)
        public int StartHourLocal { get; set; } = 8;
        public int EndHourLocal { get; set; } = 23;
        // Interval between scans in minutes (default 60 = 1h)
        public int IntervalMinutes { get; set; } = 60;
        // Consider symbols under this USD price
        public decimal PriceThresholdUsd { get; set; } = 50m;
        // Optional explicit symbols for scanning, fallback to AlpacaOptions.Symbols if null or empty
        public string[]? Symbols { get; set; }
    }
}
