namespace Divitiae.Worker.Config
{
    public class WorkerOptions
    {
        // Cooldown duration in minutes after insufficient funds or order submission failure
        public int InsufficientFundsCooldownMinutes { get; set; } = 5;
    }
}
