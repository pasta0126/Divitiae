using Divitiae.Worker.Trading;

namespace Divitiae.Worker.Alpaca
{
    public interface IBarCache
    {
        void Seed(string symbol, IReadOnlyList<Bar> bars);
        void Add(string symbol, Bar bar);
        IReadOnlyList<Bar> Get(string symbol);
        decimal? GetLastClose(string symbol);
    }

    public class BarCache : IBarCache
    {
        private readonly Dictionary<string, LinkedList<Bar>> _bars = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxBarsPerSymbol = 2000;

        public void Seed(string symbol, IReadOnlyList<Bar> bars)
        {
            var list = GetList(symbol);
            list.Clear();
            foreach (var b in bars.OrderBy(b => b.Time))
                list.AddLast(b);
            Trim(list);
        }

        public void Add(string symbol, Bar bar)
        {
            var list = GetList(symbol);
            if (list.Last?.Value.Time != bar.Time)
            {
                list.AddLast(bar);
                Trim(list);
            }
        }

        public IReadOnlyList<Bar> Get(string symbol)
        {
            var list = GetList(symbol);
            return list.ToList();
        }

        public decimal? GetLastClose(string symbol)
        {
            var list = GetList(symbol);
            return list.LastOrDefault()?.Close;
        }

        private LinkedList<Bar> GetList(string symbol)
        {
            if (!_bars.TryGetValue(symbol, out var list))
            {
                list = new LinkedList<Bar>();
                _bars[symbol] = list;
            }
            return list;
        }

        private static void Trim(LinkedList<Bar> list)
        {
            while (list.Count > MaxBarsPerSymbol)
                list.RemoveFirst();
        }
    }
}
