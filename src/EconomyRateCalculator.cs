using System.Globalization;

namespace SeraphCivilizationSimulation;

public static class EconomyRateCalculator
{
    // Policy values must round-trip through the network and editor without rounding.
    public static string FormatExact(decimal rate) => rate.ToString("0.############################", CultureInfo.InvariantCulture);
    // Slot quantities stay readable, including cross-rates as small as 10^-12.
    public static string FormatQuantity(decimal rate) => rate.ToString("G8", CultureInfo.InvariantCulture).Replace('E', 'e');

    public static List<(string Tribe, decimal Rate)> Convert(IEnumerable<EconomyRateView> rates, string from, string to)
    {
        var result = new List<(string, decimal)>();
        foreach (var group in rates.GroupBy(r => r.TribeId))
        {
            string reference = group.First().ReferenceKey;
            if (reference.Length == 0) continue;
            decimal Value(string coin) => coin == reference ? 1 : EconomyState.TryRate(group.FirstOrDefault(r => r.CoinKey == coin)?.Rate,
                out decimal n) ? n : 0;
            decimal a = Value(from), b = Value(to);
            if (a > 0 && b > 0) result.Add((group.First().Tribe, a / b));
        }
        return result;
    }
}
