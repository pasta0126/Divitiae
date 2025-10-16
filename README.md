# Divitiae

Divitiae is an automated trading application that analyzes the market and executes buy and sell operations to seek profits.

## Worker Flow

The `Worker` is a background service (.NET 8 `BackgroundService`) that:

- Preloads 1-minute bars for each configured symbol.
- In a periodic loop, gets the latest market bar, updates the `BarCache` and evaluates an `IStrategy`.
- Based on the decision (`Hold`, `Buy`, `Sell`), sends a bracket buy order or closes the position.

```mermaid
flowchart TD
    subgraph Components[Components]
        Opts[AlpacaOptions]
        M[AlpacaMarketDataClient]
        T[AlpacaTradingClient]
        Cache[BarCache]
        Strat["EmaCrossoverStrategy<br/>(IStrategy)"]
        Clk[IClock]
    end

    A[Worker Start] --> B{Read configuration}
    B --> Opts
    A --> C["Preload bars<br/>per symbol (BarsSeed)"]

    C --> D[GetMinuteBarsAsync]
    D --> M
    M --> E[Historical bars]
    E --> Cache

    C --> F["Main loop<br/>(PollingIntervalSeconds)"]

    F --> G["For each symbol:<br/>GetLatestMinuteBarAsync"]
    G --> H{New bar?}
    H -->|Yes| I[Update BarCache]
    H -->|No| F

    I --> J["Strategy.Evaluate<br/>(symbol, bars)"]
    J --> K{Decision}
    K -->|Hold| F
    K -->|Buy| L[TryEnterLong]
    K -->|Sell| N[TryExitLong]

    L --> M1{"HasOpenPosition /<br/>HasOpenOrders?"}
    M1 -->|Yes| F
    M1 -->|No| M2["GetAccount and<br/>calculate notional"]
    M2 --> M3[SubmitBracketOrderNotionalAsync]
    M3 --> F

    N --> N1[ClosePositionAsync]
    N1 --> F

    %% Component relationships
    D -.-> M
    I -.-> Cache
    J -.-> Strat
    M3 -.-> T
    N1 -.-> T
    F -.-> Clk
```

### Relevant Configuration Parameters (`AlpacaOptions`)

- `Symbols`: list of symbols to scan/trade.
- `BarsSeed`: number of initial bars for preload.
- `PollingIntervalSeconds`: interval of the polling loop.
- `MarketDataFeed`: data feed for market data.
- `PositionNotionalFraction` and `MinNotionalUsd`: entry notional calculation.
- `TakeProfitPercent` and `StopLossPercent`: bracket levels.
- `TimeInForce`: TIF for orders.

### Key Components

- `Worker` (BackgroundService): orchestrates the flow.
- `AlpacaMarketDataClient`: gets 1m bars and latest bars.
- `BarCache`: stores and trims bars by symbol.
- `IStrategy` (`EmaCrossoverStrategy`): decides `Hold/Buy/Sell`.
- `AlpacaTradingClient`: queries account, positions/orders and sends bracket/close orders.
- `IClock`: temporal reference for loop timing.
