namespace Sample.App;

public sealed class RegionalRevenueCalculator : RevenueCalculator
{
    public int CalculateRegionalForecast(int north, int south, int west, int correction)
    {
        var quarterRevenue = CalculateQuarterRevenue(north, south, west, correction);
        int sumOfRegional = quarterRevenue + north + south;
        var offset = ComputeStabilityDelta(sumOfRegional, quarterRevenue, west);
        var regionalForecast = sumOfRegional + offset + correction;

        RegisterMetric(nameof(sumOfRegional), sumOfRegional);
        RegisterMetric(nameof(regionalForecast), regionalForecast);

        return ApplyRounding(regionalForecast, quarterRevenue);
    }

    protected override int ResolveMultiplier(int baseline, int trendBonus, int march)
    {
        var inherited = base.ResolveMultiplier(baseline, trendBonus, march);
        var uplift = (baseline + march) / 20;
        return inherited + uplift;
    }
}
