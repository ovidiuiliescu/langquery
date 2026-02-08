namespace Sample.App;

file sealed class FileScopedSignal
{
    internal static int Compute(int value)
    {
        return value < 0 ? 0 : value;
    }
}
