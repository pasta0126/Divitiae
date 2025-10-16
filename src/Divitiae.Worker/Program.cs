using Divitiae.Worker;
using Divitiae.Worker.Alpaca;
using Divitiae.Worker.Config;
using Divitiae.Worker.Strategy;
using Divitiae.Worker.Trading;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Serilog bootstrap (reads from configuration including environment-specific files)
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

// Bind configuration
builder.Services.Configure<AlpacaOptions>(builder.Configuration.GetSection("Alpaca"));

// Http clients for Alpaca endpoints
builder.Services.AddHttpClient("alpaca-trading", (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlpacaOptions>>().Value;
    client.BaseAddress = new Uri(opts.TradingApiBaseUrl.TrimEnd('/') + "/v2/");
    client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", opts.ApiKeyId);
    client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", opts.ApiSecretKey);
});

builder.Services.AddHttpClient("alpaca-marketdata", (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AlpacaOptions>>().Value;
    client.BaseAddress = new Uri(opts.MarketDataApiBaseUrl.TrimEnd('/') + "/v2/");
    client.DefaultRequestHeaders.Add("APCA-API-KEY-ID", opts.ApiKeyId);
    client.DefaultRequestHeaders.Add("APCA-API-SECRET-KEY", opts.ApiSecretKey);
});

// Services and strategy
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IAlpacaTradingClient, AlpacaTradingClient>();
builder.Services.AddSingleton<IAlpacaMarketDataClient, AlpacaMarketDataClient>();
builder.Services.AddSingleton<IBarCache, BarCache>();
builder.Services.AddSingleton<IStrategy, EmaCrossoverStrategy>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

try
{
    Log.Information("Starting host - Environment: {Env}", builder.Environment.EnvironmentName);
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
