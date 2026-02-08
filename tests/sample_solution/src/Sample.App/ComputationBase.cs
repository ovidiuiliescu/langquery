namespace Sample.App;

public abstract class ComputationBase
{
    private readonly Dictionary<string, int> _metrics = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> Metrics => _metrics;

    protected int NormalizeToHundreds(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        var remainder = value % 100;
        if (remainder == 0)
        {
            return value;
        }

        return value + (100 - remainder);
    }

    protected int ComputeStabilityDelta(int current, int baseline, int modifier)
    {
        var drift = current - baseline;
        if (drift < 0)
        {
            drift = -drift;
        }

        var weighted = drift + modifier + baseline;
        return weighted / 3;
    }

    protected int ApplyRounding(int raw, int baseline)
    {
        if (baseline == 0)
        {
            return raw;
        }

        var rounded = raw;
        var offset = raw % 5;
        if (offset != 0)
        {
            rounded += 5 - offset;
        }

        return rounded + (baseline / 20);
    }

    protected void RegisterMetric(string metricName, int value)
    {
        _metrics[metricName] = value;
    }
}
