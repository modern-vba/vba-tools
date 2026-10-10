using System.Globalization;
using System.Text;
using VbaDev.App.Export;
using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;
using VbaTools.Syntax;

namespace VbaDev.App.Build;

/// <summary>
/// Captures and restores source-workbook VBA state without persistence or Excel lifetime authority.
/// The caller retains this capsule and its staging through its intent-specific finalization.
/// </summary>
internal sealed class SourceWorkbookReplacement
{
    private readonly VbeImportSourceSet sourceSet;
    private readonly WorkbookMaterializationNamePreflightReport sourcePreflight;
    private readonly WorkbookExportStaging recovery;
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly TimeSpan recoveryBudget;
    private readonly SourceWorkbookReplacementDescription description;
    private readonly WorkbookMaterializationNamePreflight namePreflight = new();
    private ISourceWorkbookReplacementSession? capturedSession;
    private IReadOnlyList<WorkbookModule> originalModules = [];
    private IReadOnlyList<WorkbookReference> originalReferences = [];
    private IReadOnlyList<WorkbookModule> replaceableModules = [];

    internal SourceWorkbookReplacement(
        VbeImportSourceSet sourceSet,
        WorkbookMaterializationNamePreflightReport sourcePreflight,
        WorkbookExportStaging recovery,
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        TimeSpan? recoveryBudgetOverride = null,
        SourceWorkbookReplacementDescription? description = null)
    {
        this.sourceSet = sourceSet;
        this.sourcePreflight = sourcePreflight;
        this.recovery = recovery;
        this.ownershipFactory = ownershipFactory;
        recoveryBudget = recoveryBudgetOverride switch
        {
            null => TimeSpan.FromMinutes(10),
            { } value when value > TimeSpan.Zero
                && value <= TimeSpan.FromMilliseconds(uint.MaxValue - 1) => value,
            _ => throw new ArgumentOutOfRangeException(nameof(recoveryBudgetOverride),
                "The source workbook recovery budget must be finite and positive.")
        };
        this.description = description ?? new("Build", "Save", "already-open workbook");
    }

    internal bool ReplacementStarted { get; private set; }

    internal bool RetainRecovery { get; private set; }

    internal WorkbookExportStaging? RecoveryVerification { get; private set; }

    /// <summary>Completes recoverable exports before the caller permits any replacement.</summary>
    internal async Task CaptureAsync(
        ISourceWorkbookReplacementSession session,
        IReadOnlyList<WorkbookModule> modules,
        IReadOnlyList<WorkbookReference> references,
        CancellationToken cancellationToken)
    {
        if (capturedSession is not null)
            throw new InvalidOperationException("Source workbook recovery has already been captured.");
        originalModules = modules.ToArray();
        originalReferences = references.ToArray();
        replaceableModules = originalModules.Where(module => module.Kind.IsImportable()).ToArray();
        foreach (var module in replaceableModules)
        {
            await recovery.WriteModuleAsync(module.Name + SourceExtension(module.Kind),
                path => session.ExportModuleAsync(module.Name, path, cancellationToken)).ConfigureAwait(false);
        }
        recovery.CompleteProduction();
        recovery.ProveUnchanged();
        foreach (var form in replaceableModules.Where(module => module.Kind == WorkbookModuleKind.Form))
            ValidateCapturedFormRecovery(form.Name, recovery.Path, sourceSet.ActiveCodePage);
        foreach (var reference in originalReferences.Where(reference => reference.IsRemovable))
        {
            if (string.IsNullOrWhiteSpace(reference.Guid)
                || reference.Major is null || reference.Minor is null)
                throw new BuildCommandException(
                    $"{description.OperationName} cannot replace workbook references because the existing reference '{reference.Name}' lacks a recoverable GUID/version identity.");
        }
        capturedSession = session;
    }

    /// <summary>
    /// Replaces captured VBA state after the caller's confirmation/readiness continuation.
    /// Reference normalization and analyzed authority, if applicable, remain intent-owned.
    /// </summary>
    internal async Task<SourceWorkbookReplacementResult> ReplaceAndVerifyAsync(
        ISourceWorkbookReplacementSession session,
        Func<CancellationToken, Task<IReadOnlyList<string>>> normalizeReferences,
        Action<string> verifyProjectAuthority,
        Action<IReadOnlyList<WorkbookReference>> verifyReferenceAuthority,
        CancellationToken cancellationToken)
    {
        RequireCapturedSession(session);
        if (ReplacementStarted)
            throw new InvalidOperationException("Source workbook replacement has already started.");
        cancellationToken.ThrowIfCancellationRequested();
        ReplacementStarted = true;
        foreach (var module in replaceableModules)
            await session.RemoveModuleAsync(module.Name, cancellationToken).ConfigureAwait(false);
        var warnings = await normalizeReferences(cancellationToken).ConfigureAwait(false);

        var finalProjectName = await session.GetProjectNameAsync(cancellationToken).ConfigureAwait(false);
        verifyProjectAuthority(finalProjectName);
        var finalModules = await session.GetModulesAsync(cancellationToken).ConfigureAwait(false);
        var finalReferences = await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        verifyReferenceAuthority(finalReferences);
        namePreflight.ThrowIfFailed(sourcePreflight,
            namePreflight.InspectLivePhase(sourceSet.SourceFiles, finalModules,
                finalProjectName, finalReferences));

        foreach (var sourceFile in sourceSet.SourceFiles)
            await session.ImportModuleAsync(sourceFile, cancellationToken).ConfigureAwait(false);
        var verification = await session.VerifyAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new WorkbookVerificationReportMissingException();
        var committedProjectName = await session.GetProjectNameAsync(cancellationToken).ConfigureAwait(false);
        verifyProjectAuthority(committedProjectName);
        var committedModules = await session.GetModulesAsync(cancellationToken).ConfigureAwait(false);
        var importedNames = sourceSet.SourceFiles
            .Select(sourceFile => sourceFile.ImportVerification.ComponentName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var committedRetainedModules = committedModules
            .Where(module => !importedNames.Contains(module.Name)).ToArray();
        var committedReferences = await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        verifyReferenceAuthority(committedReferences);
        namePreflight.ThrowIfFailed(sourcePreflight,
            namePreflight.InspectLivePhase(sourceSet.SourceFiles,
                committedRetainedModules, committedProjectName, committedReferences));
        return new SourceWorkbookReplacementResult(warnings, verification);
    }

    /// <summary>
    /// Attempts bounded restoration independently of the failed operation's cancellation.
    /// The intent owner must first prove that its persistence/completion boundary was not crossed.
    /// </summary>
    internal async Task RestoreAsync(
        ISourceWorkbookReplacementSession session,
        Exception operationFailure)
    {
        RequireCapturedSession(session);
        if (!ReplacementStarted) return;
        using var recoveryDeadline = new CancellationTokenSource(recoveryBudget);
        var recoveryToken = recoveryDeadline.Token;
        try
        {
            var currentModules = await session.GetModulesAsync(recoveryToken).ConfigureAwait(false);
            foreach (var module in currentModules.Where(module => module.Kind.IsImportable()))
            {
                recoveryToken.ThrowIfCancellationRequested();
                await session.RemoveModuleAsync(module.Name, recoveryToken).ConfigureAwait(false);
            }
            recoveryToken.ThrowIfCancellationRequested();
            await RestoreReferencesAsync(session, originalReferences, recoveryToken).ConfigureAwait(false);
            foreach (var module in replaceableModules)
            {
                recoveryToken.ThrowIfCancellationRequested();
                var sourcePath = Path.Combine(recovery.Path, module.Name + SourceExtension(module.Kind));
                await session.ImportModuleAsync(new VbeImportSourceFile(
                    sourcePath, SourceKind(module.Kind),
                    module.Kind == WorkbookModuleKind.Form &&
                    File.Exists(Path.ChangeExtension(sourcePath, ".frx"))
                        ? Path.ChangeExtension(sourcePath, ".frx") : null,
                    new VbeImportVerification(module.Name, SourceKind(module.Kind),
                        [], "recovery")), recoveryToken).ConfigureAwait(false);
            }
            recoveryToken.ThrowIfCancellationRequested();
            RecoveryVerification = WorkbookExportStaging.Create(ownershipFactory);
            await VerifyRestoredWorkbookAsync(session, originalModules, originalReferences,
                replaceableModules, recovery, RecoveryVerification, recoveryToken).ConfigureAwait(false);
        }
        catch (Exception recoveryFailure)
        {
            RetainRecovery = true;
            var deadline = recoveryDeadline.IsCancellationRequested
                ? $" The {recoveryBudget.TotalMinutes:0.###}-minute recovery deadline expired."
                : string.Empty;
            throw new BuildCommandException(
                $"{description.OperationName} failed before {description.CompletionBoundary} and workbook recovery was incomplete.{deadline} The {description.WorkbookDescription} may be partially changed. Recovery files: {recovery.Path}. Restore modules manually from that directory, then inspect workbook references. Original failure: {operationFailure.Message} Recovery failure: {recoveryFailure.Message}",
                new AggregateException(operationFailure, recoveryFailure));
        }
    }

    private void RequireCapturedSession(ISourceWorkbookReplacementSession session)
    {
        if (!ReferenceEquals(capturedSession, session))
            throw new InvalidOperationException(
                "Source workbook replacement requires the same session whose recovery state was captured.");
    }

    private async Task VerifyRestoredWorkbookAsync(
        ISourceWorkbookReplacementSession session,
        IReadOnlyList<WorkbookModule> originalModules,
        IReadOnlyList<WorkbookReference> originalReferences,
        IReadOnlyList<WorkbookModule> replaceableModules,
        WorkbookExportStaging originalExport,
        WorkbookExportStaging restoredExport,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restoredModules = await session.GetModulesAsync(cancellationToken)
            .ConfigureAwait(false);
        static string[] ModuleKeys(IReadOnlyList<WorkbookModule> modules)
            => modules.Select(module => $"{(int)module.Kind}:{module.Name}")
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!ModuleKeys(originalModules).SequenceEqual(
                ModuleKeys(restoredModules), StringComparer.OrdinalIgnoreCase))
            throw new BuildCommandException(
                $"The restored workbook module identities or kinds differ from the pre-{description.OperationName} capture.");

        cancellationToken.ThrowIfCancellationRequested();
        var restoredReferences = await session.GetReferencesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (restoredReferences.Count != originalReferences.Count ||
            originalReferences.Where((before, index) =>
                !ReferenceIdentityEquals(before, restoredReferences[index])).Any())
            throw new BuildCommandException(
                $"The restored workbook reference identities or priority order differ from the pre-{description.OperationName} capture.");

        foreach (var module in replaceableModules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = module.Name + SourceExtension(module.Kind);
            await restoredExport.WriteModuleAsync(fileName,
                path => session.ExportModuleAsync(module.Name, path, cancellationToken))
                .ConfigureAwait(false);
        }
        restoredExport.CompleteProduction();
        restoredExport.ProveUnchanged();
        foreach (var module in replaceableModules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = module.Name + SourceExtension(module.Kind);
            var before = Path.Combine(originalExport.Path, fileName);
            var after = Path.Combine(restoredExport.Path, fileName);
            if (!File.ReadAllBytes(before).AsSpan().SequenceEqual(File.ReadAllBytes(after)))
                throw new BuildCommandException(
                    $"The restored workbook module '{module.Name}' differs from the pre-{description.OperationName} capture.");
            if (module.Kind != WorkbookModuleKind.Form) continue;
            var beforeSidecar = Path.ChangeExtension(before, ".frx");
            var afterSidecar = Path.ChangeExtension(after, ".frx");
            var beforeExists = File.Exists(beforeSidecar);
            var afterExists = File.Exists(afterSidecar);
            if (beforeExists != afterExists || beforeExists &&
                !File.ReadAllBytes(beforeSidecar).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(afterSidecar)))
                throw new BuildCommandException(
                    $"The restored UserForm '{module.Name}' sidecar differs from the pre-{description.OperationName} capture.");
        }
    }

    private static string SourceExtension(WorkbookModuleKind kind)
        => kind switch
        {
            WorkbookModuleKind.StandardModule => ".bas",
            WorkbookModuleKind.ClassModule => ".cls",
            WorkbookModuleKind.Form => ".frm",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static VbaSourceKind SourceKind(WorkbookModuleKind kind)
        => kind switch
        {
            WorkbookModuleKind.StandardModule => VbaSourceKind.StandardModule,
            WorkbookModuleKind.ClassModule => VbaSourceKind.ClassModule,
            WorkbookModuleKind.Form => VbaSourceKind.Form,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static async Task RestoreReferencesAsync(
        ISourceWorkbookReplacementSession session,
        IReadOnlyList<WorkbookReference> original,
        CancellationToken cancellationToken)
    {
        var current = await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var reference in current)
        {
            if (original.Any(before => ReferenceIdentityEquals(before, reference))) continue;
            if (!reference.IsRemovable
                || !await session.RemoveReferenceAsync(reference.Name, cancellationToken).ConfigureAwait(false))
                throw new BuildCommandException(
                    $"Workbook reference '{reference.Name}' could not be removed during recovery.");
        }
        var remaining = await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var reference in original)
        {
            if (remaining.Any(after => ReferenceIdentityEquals(reference, after))) continue;
            if (string.IsNullOrWhiteSpace(reference.Guid)
                || reference.Major is null || reference.Minor is null)
                throw new BuildCommandException(
                    $"Workbook reference '{reference.Name}' has no recoverable GUID/version identity.");
            await session.AddReferenceAsync(new ResolvedVbaProjectReference(
                reference.Name, reference.Guid, reference.Major.Value, reference.Minor.Value),
                cancellationToken).ConfigureAwait(false);
        }
        var restored = await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        if (restored.Count != original.Count || original.Where((before, index) =>
                !ReferenceIdentityEquals(before, restored[index])).Any())
            throw new BuildCommandException(
                "Workbook reference identities or priority order could not be verified after recovery.");
    }

    private static bool ReferenceIdentityEquals(WorkbookReference left, WorkbookReference right)
        => left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Guid, right.Guid, StringComparison.OrdinalIgnoreCase)
            && left.Major == right.Major
            && left.Minor == right.Minor
            && string.Equals(left.NamespaceName, right.NamespaceName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);

    private static void ValidateCapturedFormRecovery(string moduleName, string recoveryPath, int codePage)
    {
        var formPath = Path.Combine(recoveryPath, moduleName + ".frm");
        var sidecarPath = Path.Combine(recoveryPath, moduleName + ".frx");
        string formText;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(codePage,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            formText = File.ReadAllText(formPath, encoding);
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException)
        {
            throw new BuildCommandException(
                $"The existing UserForm '{moduleName}' could not be captured for recovery: {formPath}", error);
        }

        var designer = VbaSyntaxTree.ParseModule(new Uri(formPath).AbsoluteUri, formText)
            .Module.FormDesignerBlock;
        if (designer is null || designer.EvidenceProblems.Count > 0)
            throw new BuildCommandException(
                $"The existing UserForm '{moduleName}' has incomplete designer recovery evidence: {formPath}");
        foreach (var resource in designer.ResourceReferences)
        {
            if (!resource.FileName.Equals(moduleName + ".frx", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(sidecarPath))
                throw new BuildCommandException(
                    $"The existing UserForm '{moduleName}' declares a required recovery sidecar that was not captured: {sidecarPath}");
            if (!long.TryParse(resource.Offset, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var offset)
                || offset >= new FileInfo(sidecarPath).Length)
                throw new BuildCommandException(
                    $"The captured UserForm sidecar is incomplete at offset {resource.Offset}: {sidecarPath}");
        }
    }
}

internal sealed record SourceWorkbookReplacementDescription(
    string OperationName,
    string CompletionBoundary,
    string WorkbookDescription);

internal sealed record SourceWorkbookReplacementResult(
    IReadOnlyList<string> Warnings,
    VbeImportVerificationReport Verification);
