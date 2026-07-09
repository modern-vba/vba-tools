namespace VbaDev.Domain;

public readonly record struct ToolingCommandName(string Value)
{
    public override string ToString() => Value;
}
