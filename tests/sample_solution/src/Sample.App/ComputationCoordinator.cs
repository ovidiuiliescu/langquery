namespace Sample.App;

public sealed class ComputationCoordinator
{
    private readonly RevenueCalculator _revenue = new();
    private readonly RegionalRevenueCalculator _regional = new();
    private readonly ExpenseCalculator _expense = new();

    public int BuildNetForecast(
        int januaryRevenue,
        int februaryRevenue,
        int marchRevenue,
        int trendBonus,
        int fixedCost,
        int variableCost,
        int tax)
    {
        var quarterRevenue = _revenue.CalculateQuarterRevenue(januaryRevenue, februaryRevenue, marchRevenue, trendBonus);
        var regionalRevenue = _regional.CalculateRegionalForecast(januaryRevenue, februaryRevenue, marchRevenue, trendBonus);
        var monthlyExpense = _expense.CalculateMonthlyExpense(fixedCost, variableCost, tax, new[] { 2, 3, 1 });

        int sumOfBalances = quarterRevenue + regionalRevenue + monthlyExpense;
        var riskScore = ComputeRisk(sumOfBalances, quarterRevenue, monthlyExpense);
        var netForecast = sumOfBalances + riskScore + trendBonus;

        if (netForecast < 0)
        {
            return 0;
        }

        return netForecast;
    }

    private static int ComputeRisk(int sumOfBalances, int quarterRevenue, int monthlyExpense)
    {
        var spread = quarterRevenue - monthlyExpense;
        if (spread < 0)
        {
            spread = -spread;
        }

        var normalized = spread + sumOfBalances + monthlyExpense;
        return normalized / 9;
    }
}
