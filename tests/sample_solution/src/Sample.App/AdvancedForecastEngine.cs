namespace Sample.App;

public interface IScenarioTelemetry : ITraceableComputation
{
    string LastScenario { get; }
}

public interface IAdvancedTelemetry : IScenarioTelemetry
{
    int LastChecksum { get; }
}

public enum ForecastSensitivity
{
    Low,
    Medium,
    High
}

public readonly record struct ForecastSlice(int North, int South, int West);

public readonly struct AdjustmentBucket : IComparable<AdjustmentBucket>
{
    public AdjustmentBucket(int value)
    {
        Value = value;
    }

    public int Value { get; }

    public int CompareTo(AdjustmentBucket other)
    {
        return Value.CompareTo(other.Value);
    }
}

public sealed class AdvancedForecastEngine : ComputationBase, IAdvancedTelemetry
{
    private readonly List<int> _history = [];
    private readonly string _regionCode;

    public AdvancedForecastEngine(string regionCode)
    {
        _regionCode = string.IsNullOrWhiteSpace(regionCode) ? "global" : regionCode;
    }

    public event EventHandler<int>? ForecastComputed;

    public int LastResult { get; private set; }

    public string LastScenario { get; private set; } = "none";

    public int LastChecksum { get; private set; }

    public int this[int index] => _history[index];

    public int BuildAdvancedForecast(
        string scenarioName,
        ForecastSlice slice,
        ForecastSensitivity sensitivity,
        IEnumerable<int> externalAdjustments)
    {
        LastScenario = scenarioName;

        int ClampFloor(int value)
        {
            return value < 0 ? 0 : value;
        }

        Func<int, int, int> combine = (left, right) => left + right;
        Predicate<int> isHealthy = delegate (int candidate)
        {
            return candidate >= 0;
        };

        var baseSignal = combine(slice.North, slice.South) + slice.West;
        var sensitivityBoost = sensitivity switch
        {
            ForecastSensitivity.Low => 2,
            ForecastSensitivity.Medium => 6,
            ForecastSensitivity.High => 11,
            _ => 0
        };

        var adjustment = Fold(externalAdjustments, 0, static (state, current) => state + current);
        var projected = baseSignal + sensitivityBoost + adjustment;

        if (!isHealthy(projected))
        {
            projected = ClampFloor(projected);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(scenarioName))
            {
                throw new InvalidOperationException("Scenario name cannot be empty.");
            }
        }
        catch (InvalidOperationException ex)
        {
            projected += ex.Message.Length;
        }

        foreach (var bonus in externalAdjustments)
        {
            projected += bonus % 2;
        }

        LastResult = ApplyRounding(projected, NormalizeToHundreds(baseSignal));
        LastChecksum = NormalizeChecksum(_regionCode, LastScenario, LastResult);

        _history.Add(LastResult);
        RegisterMetric(nameof(baseSignal), baseSignal);
        RegisterMetric(nameof(projected), projected);
        RegisterMetric(nameof(LastChecksum), LastChecksum);

        ForecastComputed?.Invoke(this, LastResult);
        return LastResult;
    }

    public string BuildTrace(string scenarioName)
    {
        var snapshot = LastResult + LastChecksum + scenarioName.Length;
        return $"scenario={scenarioName}; last={LastResult}; checksum={LastChecksum}; snapshot={snapshot}";
    }

    private static int Fold<TInput>(IEnumerable<TInput> source, int seed, Func<int, TInput, int> step)
    {
        var accumulator = seed;
        foreach (var item in source)
        {
            accumulator = step(accumulator, item);
        }

        return accumulator;
    }

    private static int NormalizeChecksum(string regionCode, string scenarioName, int value)
    {
        var raw = regionCode.Length + scenarioName.Length + value;
        return FileScopedSignal.Compute(raw);
    }
}
