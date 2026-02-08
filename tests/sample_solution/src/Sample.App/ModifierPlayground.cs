namespace Sample.App;

public abstract class ModifierPlayground
{
    protected internal abstract int Project(int value);

    private protected int Clamp(int value)
    {
        return value < 0 ? 0 : value;
    }

    protected int Apply(int value)
    {
        return Clamp(value) + 1;
    }

    public sealed class NestedScope
    {
        public int Execute(int input)
        {
            return input + 10;
        }
    }
}

public sealed class ModifierPlaygroundRunner : ModifierPlayground
{
    protected internal override int Project(int value)
    {
        return Apply(value) * 2;
    }

    public int Run(int value)
    {
        return Project(value);
    }
}
