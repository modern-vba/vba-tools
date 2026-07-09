namespace VbaDev.Domain;

public sealed record CommandDefaults(TestCommandDefaults? Test = null);

public sealed record TestCommandDefaults(string? Format = null);
