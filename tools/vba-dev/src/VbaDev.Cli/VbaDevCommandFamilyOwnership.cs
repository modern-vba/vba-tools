using System.CommandLine;

namespace VbaDev.Cli;

/// <summary>
/// Proves that each actionable leaf belongs to one sealed internal family.
/// </summary>
internal sealed class VbaDevCommandFamilyOwnership
{
    private readonly List<VbaDevCommandFamilyOwnershipRegistration> registrations = [];

    internal void Register(object family, Command command)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(command);
        registrations.Add(new VbaDevCommandFamilyOwnershipRegistration(
            command,
            family.GetType()));
    }

    internal IReadOnlyList<VbaDevCommandFamilyOwnershipRegistration> Complete(
        RootCommand rootCommand)
    {
        ArgumentNullException.ThrowIfNull(rootCommand);
        IEqualityComparer<Command> commandComparer = ReferenceEqualityComparer.Instance;
        var leaves = EnumerateCommands(rootCommand)
            .Where(entry => entry.Command.Subcommands.Count == 0)
            .ToDictionary(entry => entry.Command, entry => entry.CommandPath, commandComparer);
        var ownedCommands = new HashSet<Command>(commandComparer);

        foreach (var registration in registrations)
        {
            if (!registration.FamilyType.IsSealed || registration.FamilyType.IsVisible)
            {
                throw new InvalidOperationException(
                    $"Command family '{registration.FamilyType.FullName}' must be sealed and " +
                    "internal.");
            }

            if (!leaves.TryGetValue(registration.Command, out var commandPath) ||
                registration.Command.Action is null)
            {
                throw new InvalidOperationException(
                    $"A command owned by '{registration.FamilyType.FullName}' is not an " +
                    "actionable leaf of the completed graph.");
            }

            if (!ownedCommands.Add(registration.Command))
            {
                throw new InvalidOperationException(
                    $"Command '{commandPath}' has more than one family owner.");
            }
        }

        var unowned = leaves
            .Where(entry => !ownedCommands.Contains(entry.Key))
            .Select(entry => entry.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unowned.Length > 0)
        {
            throw new InvalidOperationException(
                $"Completed command leaves have no family owner: {string.Join(", ", unowned)}.");
        }

        return Array.AsReadOnly(registrations.ToArray());
    }

    private static IEnumerable<(Command Command, string CommandPath)> EnumerateCommands(
        Command parent,
        string parentPath = "")
    {
        foreach (var command in parent.Subcommands)
        {
            var commandPath = string.IsNullOrEmpty(parentPath)
                ? command.Name
                : $"{parentPath} {command.Name}";
            yield return (command, commandPath);
            foreach (var descendant in EnumerateCommands(command, commandPath))
            {
                yield return descendant;
            }
        }
    }
}

internal sealed record VbaDevCommandFamilyOwnershipRegistration(
    Command Command,
    Type FamilyType);
