namespace Sample.App;

public sealed class ExpenseCalculator : ComputationBase, ITraceableComputation
{
    public int LastResult { get; private set; }

    public int CalculateMonthlyExpense(int fixedCost, int variableCost, int tax, int[] monthFactors)
    {
        int sumOfExpenses = fixedCost + variableCost + tax;
        var baseline = NormalizeToHundreds(sumOfExpenses);
        var reserve = ComputeStabilityDelta(sumOfExpenses, baseline, tax);
        var seasonalPenalty = ResolveSeasonalPenalty(monthFactors, reserve);
        var constrainedExpense = sumOfExpenses + reserve + seasonalPenalty;

        LastResult = ApplyRounding(constrainedExpense, baseline);
        RegisterMetric(nameof(sumOfExpenses), sumOfExpenses);
        RegisterMetric(nameof(constrainedExpense), constrainedExpense);

        return LastResult;
    }

    public string BuildTrace(string scenarioName)
    {
        var snapshot = LastResult + scenarioName.Length;
        return $"scenario={scenarioName}; last={LastResult}; snapshot={snapshot}";
    }

    private static int ResolveSeasonalPenalty(int[] monthFactors, int reserve)
    {
        var penalty = reserve / 8;

        foreach (var factor in monthFactors)
        {
            penalty += factor;
        }

        return penalty;
    }
}
