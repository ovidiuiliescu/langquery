namespace Sample.App;

public sealed class Calculator
{
    private readonly ComputationCoordinator _coordinator = new();

    public ScenarioSummary CalculateScenarioSummary(
        int januaryRevenue,
        int februaryRevenue,
        int marchRevenue,
        int trendBonus,
        int fixedCost,
        int variableCost,
        int tax)
    {
        var values = new[]
        {
            januaryRevenue,
            februaryRevenue,
            marchRevenue,
            trendBonus,
            fixedCost,
            variableCost,
            tax
        };

        int sumOfInputs = 0;
        foreach (var value in values)
        {
            sumOfInputs += value;
        }

        var netForecast = _coordinator.BuildNetForecast(
            januaryRevenue,
            februaryRevenue,
            marchRevenue,
            trendBonus,
            fixedCost,
            variableCost,
            tax);

        var normalizedInputLoad = NormalizeInputLoad(sumOfInputs, values.Length);
        var consistencyScore = BuildConsistencyScore(netForecast, normalizedInputLoad, sumOfInputs);

        return new ScenarioSummary(
            sumOfInputs,
            netForecast,
            normalizedInputLoad,
            consistencyScore);
    }

    private static int NormalizeInputLoad(int sumOfInputs, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        var average = sumOfInputs / count;
        var guardRail = count * 3;
        return average + guardRail;
    }

    private static int BuildConsistencyScore(int netForecast, int normalizedInputLoad, int sumOfInputs)
    {
        var delta = netForecast - sumOfInputs;
        if (delta < 0)
        {
            delta = -delta;
        }

        var weighted = delta + normalizedInputLoad + sumOfInputs;
        return weighted / 3;
    }
}

public sealed record ScenarioSummary(int SumOfInputs, int NetForecast, int InputLoad, int ConsistencyScore);
