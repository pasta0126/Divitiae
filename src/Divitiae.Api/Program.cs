using Serilog;
using Divitiae.Api.Alpaca;
using Divitiae.Api.Controllers;

var builder = WebApplication.CreateBuilder(args);

// Serilog bootstrap
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, dispose: true);

// Add services to the container.
builder.Services.AddControllers();
// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Options
builder.Services.Configure<AlpacaOptions>(builder.Configuration.GetSection("Alpaca"));
builder.Services.Configure<SymbolGroupsOptions>(builder.Configuration.GetSection("SymbolGroups"));

// Alpaca Http clients
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

// Clients
builder.Services.AddSingleton<IAlpacaAssetClient, AlpacaAssetClient>();
builder.Services.AddSingleton<IAlpacaMarketDataClient, AlpacaMarketDataClient>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
