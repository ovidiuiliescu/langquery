namespace Sample.App;

public class RevenueCalculator : ComputationBase
{
    public virtual int CalculateQuarterRevenue(int january, int february, int march, int trendBonus)
    {
        int BlendSignals(int left, int right)
        {
            return (left + right) / 2;
        }

        Func<int, int, int> combineSignals = (left, right) => left + right;
        Predicate<int> isSignalHealthy = delegate (int candidate)
        {
            return candidate >= 0;
        };

        int sumOfRevenue = january + february + march;
        var baseline = NormalizeToHundreds(sumOfRevenue);
        var adjustment = ComputeStabilityDelta(sumOfRevenue, baseline, trendBonus);
        var multiplier = ResolveMultiplier(baseline, trendBonus, march);
        var blendedSignal = BlendSignals(combineSignals(sumOfRevenue, trendBonus), march);
        if (!isSignalHealthy(blendedSignal))
        {
            blendedSignal = 0;
        }

        var projectedRevenue = sumOfRevenue + adjustment + multiplier + blendedSignal;

        RegisterMetric(nameof(sumOfRevenue), sumOfRevenue);
        RegisterMetric(nameof(projectedRevenue), projectedRevenue);

        return ApplyRounding(projectedRevenue, baseline);
    }

    protected virtual int ResolveMultiplier(int baseline, int trendBonus, int march)
    {
        int sumOfSignals = baseline + trendBonus + march;
        var floor = sumOfSignals / 12;
        return floor + (trendBonus / 3);
    }
}
