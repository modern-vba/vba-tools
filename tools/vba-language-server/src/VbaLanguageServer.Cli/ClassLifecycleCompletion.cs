namespace VbaLanguageServer.SourceModel;

internal static class VbaClassLifecycleCompletion
{
    public static VbaContractPrefixCompletionOrigin Origin { get; } = new(
        "Class_",
        VbaContractCompletionDomain.ClassLifecycle,
        IsConditionalPrefix: false,
        [
            CreateMember("Initialize",
                "Runs when a class instance is created, before its reference is returned to the caller."),
            CreateMember("Terminate",
                "Runs when a class instance is about to be destroyed after it becomes inaccessible. "
                + "Termination is not guaranteed when the host ends abnormally, including an End statement.")
        ]);

    private static VbaContractMemberCompletionOrigin CreateMember(
        string name,
        string documentation)
        => new(
            name,
            VbaContractCompletionDomain.ClassLifecycle,
            IsConditionalContract: false,
            Signature: new VbaCallableSignature(
                "Sub Class_" + name + "()",
                [],
                documentation,
                CallableKind: VbaCallableKind.Sub),
            CanonicalLabel: "Class_" + name);
}
