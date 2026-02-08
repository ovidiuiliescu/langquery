namespace Sample.App;

public interface ITraceableComputation
{
    int LastResult { get; }

    string BuildTrace(string scenarioName);
}
