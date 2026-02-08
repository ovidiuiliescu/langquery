namespace Sample.App;

public sealed class Greeter
{
    private static readonly string[] ToneSegments =
    {
        "steady",
        "measured",
        "confident"
    };

    public void Greet(ScenarioSummary summary)
    {
        var intro = BuildIntro(summary);
        var details = BuildDetails(summary);
        var footer = BuildFooter(summary);

        System.Console.WriteLine(intro);
        System.Console.WriteLine(details);
        System.Console.WriteLine(footer);
    }

    private static string BuildIntro(ScenarioSummary summary)
    {
        var tone = ResolveTone(summary.ConsistencyScore);
        return $"Forecast ready ({tone})";
    }

    private static string BuildDetails(ScenarioSummary summary)
    {
        var segments = new[]
        {
            $"inputs={summary.SumOfInputs}",
            $"net={summary.NetForecast}",
            $"load={summary.InputLoad}",
            $"consistency={summary.ConsistencyScore}"
        };

        return string.Join(" | ", segments);
    }

    private static string BuildFooter(ScenarioSummary summary)
    {
        var threshold = summary.InputLoad + summary.ConsistencyScore;
        var signal = summary.NetForecast >= threshold ? "green" : "amber";
        return $"signal={signal}";
    }

    private static string ResolveTone(int consistencyScore)
    {
        if (consistencyScore <= 80)
        {
            return ToneSegments[0];
        }

        if (consistencyScore <= 140)
        {
            return ToneSegments[1];
        }

        return ToneSegments[2];
    }
}
