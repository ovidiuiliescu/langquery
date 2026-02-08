namespace Sample.App;

public static class Program
{
    public static void Main()
    {
        var calculator = new Calculator();
        var summary = calculator.CalculateScenarioSummary(
            januaryRevenue: 120,
            februaryRevenue: 145,
            marchRevenue: 161,
            trendBonus: 14,
            fixedCost: 72,
            variableCost: 45,
            tax: 23);

        var greeter = new Greeter();
        greeter.Greet(summary);
    }
}
