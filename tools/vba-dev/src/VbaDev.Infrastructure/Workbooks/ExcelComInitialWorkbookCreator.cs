using VbaDev.Infrastructure.FileSystem;
using System.Runtime.ExceptionServices;
using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed record InitialWorksheetIdentity(
    string TabName,
    string DocumentModuleName);

internal sealed record InitialWorkbookBaselineSnapshot(
    int SheetCount,
    IReadOnlyList<InitialWorksheetIdentity> Worksheets,
    string WorkbookDocumentModuleName,
    string VbaProjectName,
    int ComponentCount,
    IReadOnlyList<string> DocumentModuleNames,
    IReadOnlyList<string> ReferenceNames);

internal interface IExcelComInitialWorkbookSession
{
    InitialWorkbookBaselineSnapshot EstablishAndReadBaseline();

    void Save(
        string workbookPath,
        int fileFormat);

    InitialWorkbookBaselineSnapshot ReadBaseline();
}

internal interface IExcelComInitialWorkbookLifecycle
{
    object Start(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken);

    IExcelComInitialWorkbookSession CreateWorkbook(object host, int template);

    void DisposeHost(object host, TimeSpan cleanupGrace);

    void DisposeSession(
        IExcelComInitialWorkbookSession session,
        TimeSpan cleanupGrace);
}

/// <summary>
/// Creates initial macro-enabled workbooks through an exactly owned Excel process.
/// </summary>
public sealed class ExcelComInitialWorkbookCreator : IReceiptInitialWorkbookCreator
{
    private readonly AutomationExcelProcessRuntime runtime;
    private readonly IExcelComInitialWorkbookLifecycle lifecycle;
    private readonly WorkbookAutomationTimeouts timeouts;
    private readonly IInitialWorkbookArtifactGuard artifactGuard;

    /// <summary>
    /// Creates the production initial-workbook automation adapter.
    /// </summary>
    public ExcelComInitialWorkbookCreator()
        : this(
            new StaComDispatcherFactory(),
            new ExcelComInitialWorkbookLifecycle(),
            WorkbookAutomationTimeouts.Default,
            new InitialWorkbookArtifactGuard())
    {
    }

    internal ExcelComInitialWorkbookCreator(WorkbookAutomationTimeouts timeouts)
        : this(
            new StaComDispatcherFactory(),
            new ExcelComInitialWorkbookLifecycle(),
            timeouts,
            new InitialWorkbookArtifactGuard())
    {
    }

    internal ExcelComInitialWorkbookCreator(
        IStaComDispatcherFactory dispatcherFactory,
        IExcelComInitialWorkbookLifecycle lifecycle,
        WorkbookAutomationTimeouts timeouts)
        : this(
            dispatcherFactory,
            lifecycle,
            timeouts,
            new InitialWorkbookArtifactGuard())
    {
    }

    internal ExcelComInitialWorkbookCreator(
        IStaComDispatcherFactory dispatcherFactory,
        IExcelComInitialWorkbookLifecycle lifecycle,
        WorkbookAutomationTimeouts timeouts,
        IInitialWorkbookArtifactGuard artifactGuard)
    {
        runtime = new AutomationExcelProcessRuntime(dispatcherFactory);
        this.lifecycle = lifecycle;
        this.timeouts = timeouts;
        this.artifactGuard = artifactGuard;
    }

    /// <inheritdoc />
    public InitialWorkbookCreationResult CreateInitialWorkbook(string workbookPath)
        => CreateInitialWorkbookAsync(workbookPath, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc />
    public async Task<InitialWorkbookCreationResult> CreateInitialWorkbookAsync(
        string workbookPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        cancellationToken.ThrowIfCancellationRequested();
        var absoluteWorkbookPath = Path.GetFullPath(workbookPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absoluteWorkbookPath)!);
        using var ownership = new WindowsExactFileSystemObjectOwnershipFactory().Open();
        var result = await CreateInitialWorkbookAsync(
            absoluteWorkbookPath,
            ownership,
            cancellationToken).ConfigureAwait(false);
        return result with { OwnedArtifactReceipt = null };
    }

    Task<InitialWorkbookCreationResult> IReceiptInitialWorkbookCreator.CreateInitialWorkbookAsync(
        string workbookPath,
        ExactFileSystemObjectOwnership ownership,
        CancellationToken cancellationToken)
        => CreateInitialWorkbookAsync(workbookPath, ownership, cancellationToken);

    internal async Task<InitialWorkbookCreationResult> CreateInitialWorkbookAsync(
        string workbookPath,
        ExactFileSystemObjectOwnership ownership,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentNullException.ThrowIfNull(ownership);
        cancellationToken.ThrowIfCancellationRequested();

        var absoluteWorkbookPath = Path.GetFullPath(workbookPath);
        using var staging = artifactGuard.CreateStagingArtifact();

        InitialWorkbookArtifactEvidence? stagingEvidence = null;
        var outcome = await runtime.RunInitialWorkbookAsync(
            Path.GetFileName(absoluteWorkbookPath),
            lifecycle,
            timeouts,
            async (session, token) =>
            {
                var beforeSave = await session.EstablishAndReadBaselineAsync(token).ConfigureAwait(false);
                var references = InitialWorkbookBaselineContract.ValidateExact(beforeSave);
                var afterSave = await session.SaveAndReadBaselineAsync(
                    staging.WorkbookPath,
                    () => stagingEvidence = artifactGuard.Capture(staging),
                    token).ConfigureAwait(false);
                InitialWorkbookBaselineContract.ValidateExact(afterSave);
                InitialWorkbookBaselineContract.ValidateUnchanged(beforeSave, afterSave);
                return references;
            },
            cancellationToken).ConfigureAwait(false);
        var evidence = outcome.Evidence;
        var operationError = evidence.OperationFailure;
        var cleanupError = evidence.CleanupFailure;
        if (!evidence.DispatcherRetired)
        {
            cleanupError = new WorkbookAutomationCleanupException(
                "The initial-workbook STA dispatcher retirement could not be proved.",
                CombineErrors(cleanupError, evidence.DispatcherFailure));
        }

        if (evidence.ProcessReleaseVerified && evidence.DispatcherRetired)
        {
            try
            {
                // Failed or cancelled work may still grant cleanup authority,
                // but only after both native process and STA release are proved.
                artifactGuard.CompleteCapture(staging);
            }
            catch (Exception exception)
            {
                operationError = CombineErrors(operationError, exception);
            }
        }

        if (operationError is not null || cleanupError is not null)
        {
            var artifactCleanup = TryDeleteStaging(staging);

            if (!artifactCleanup.RemovedOrAbsent)
            {
                var terminalError = CombineErrors(operationError, cleanupError)
                    ?? new InvalidOperationException(
                        "Initial workbook creation failed after staging was allocated.");
                var artifactError = artifactCleanup.Failure
                    ?? new InvalidOperationException(
                        "The staging workbook no longer names the exact object and bytes created by Excel.");
                throw new InitialWorkbookArtifactRetainedException(
                    staging.WorkbookPath,
                    stagingEvidence,
                    artifactCleanup.TargetChanged,
                    new AggregateException(terminalError, artifactError));
            }
        }

        if (cleanupError is not null)
        {
            var combinedError = operationError is null
                ? cleanupError
                : new AggregateException(operationError, cleanupError);
            if (WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(
                cleanupError))
            {
                throw new WorkbookAutomationCleanupException(
                    "The initial workbook could not prove release of its exactly owned Excel process.",
                    combinedError);
            }

            throw new WorkbookAutomationReleasedProcessCleanupException(
                "The initial-workbook Excel process was released, but cooperative cleanup or automation isolation failed.",
                combinedError);
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        var selectableReferences = outcome.GetReleasedResult();
        InitialWorkbookMaterializedArtifact? finalArtifact = null;
        Exception? materializationError = null;
        try
        {
            finalArtifact = artifactGuard.MaterializeCreateOnly(
                staging,
                absoluteWorkbookPath,
                ownership,
                cancellationToken);
        }
        catch (Exception exception)
        {
            materializationError = exception;
        }

        var stagingCleanup = TryDeleteStaging(staging);
        if (!stagingCleanup.RemovedOrAbsent)
        {
            var stagingFailure = stagingCleanup.Failure
                ?? new InvalidOperationException(
                    "The staging workbook path changed before it could be removed.");
            var stagingError = new InitialWorkbookArtifactRetainedException(
                staging.WorkbookPath,
                stagingEvidence,
                stagingCleanup.TargetChanged,
                CombineErrors(materializationError, stagingFailure)!);
            if (finalArtifact is not null)
            {
                var finalCleanup = TryDeleteArtifact(
                    ownership,
                    finalArtifact.Receipt);
                if (!finalCleanup.RemovedOrAbsent)
                {
                    throw new InitialWorkbookArtifactRetainedException(
                        absoluteWorkbookPath,
                        finalArtifact.Evidence,
                        finalCleanup.TargetChanged,
                        new AggregateException(
                            stagingError,
                            finalCleanup.Failure ?? new InvalidOperationException(
                                "The final workbook no longer names the exact materialized object and bytes.")));
                }
            }

            throw stagingError;
        }

        if (materializationError is not null)
        {
            ExceptionDispatchInfo.Capture(materializationError).Throw();
        }

        return new InitialWorkbookCreationResult(
            selectableReferences!,
            finalArtifact!.Evidence)
        {
            OwnedArtifactReceipt = finalArtifact.Receipt
        };
    }

    private InitialWorkbookArtifactCleanupResult TryDeleteStaging(
        InitialWorkbookStagingArtifact staging)
    {
        try
        {
            return artifactGuard.TryDeleteStaging(staging);
        }
        catch (Exception exception)
        {
            return InitialWorkbookArtifactCleanupResult.Failed(exception);
        }
    }

    private InitialWorkbookArtifactCleanupResult TryDeleteArtifact(
        ExactFileSystemObjectOwnership ownership,
        ExactFileSystemObjectOwnership.FileReceipt receipt)
    {
        try
        {
            return artifactGuard.TryDeleteFinalArtifact(ownership, receipt);
        }
        catch (Exception exception)
        {
            return InitialWorkbookArtifactCleanupResult.Failed(exception);
        }
    }

    private static Exception? CombineErrors(Exception? first, Exception? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : new AggregateException(first, second);
    }

    private sealed class ExcelComInitialWorkbookLifecycle
        : IExcelComInitialWorkbookLifecycle
    {
        public object Start(
            OwnedExcelTerminationController terminationController,
            CancellationToken cancellationToken)
            => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                cancellationToken);

        public IExcelComInitialWorkbookSession CreateWorkbook(object host, int template)
            => new ExcelComInitialWorkbookSession(
                ExcelComWorkbookSession.CreateOwnedForGeneration(
                    (ExcelComWorkbookSession.ExcelComHostObjects)host,
                    template));

        public void DisposeHost(object host, TimeSpan cleanupGrace)
            => ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                (ExcelComWorkbookSession.ExcelComHostObjects)host,
                cleanupGrace);

        public void DisposeSession(
            IExcelComInitialWorkbookSession session,
            TimeSpan cleanupGrace)
            => ((ExcelComInitialWorkbookSession)session)
                .DisposeOwnedGeneration(cleanupGrace);
    }
}

internal static class InitialWorkbookBaselineContract
{
    public static IReadOnlyList<string> ValidateExact(
        InitialWorkbookBaselineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SheetCount != 1 || snapshot.Worksheets.Count != 1)
        {
            throw new InvalidOperationException(
                "The initial workbook must contain exactly one worksheet and no other sheets.");
        }

        var worksheet = snapshot.Worksheets[0];
        if (!string.Equals(worksheet.TabName, "Sheet1", StringComparison.Ordinal) ||
            !string.Equals(
                worksheet.DocumentModuleName,
                "Sheet1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial workbook worksheet tab and document module must both be named 'Sheet1'.");
        }

        if (!string.Equals(
                snapshot.WorkbookDocumentModuleName,
                "ThisWorkbook",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial workbook document module must be named 'ThisWorkbook'.");
        }

        if (!string.Equals(
                snapshot.VbaProjectName,
                "VBAProject",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial workbook VBProject must be named 'VBAProject'.");
        }

        if (snapshot.ComponentCount != 2 ||
            snapshot.DocumentModuleNames.Count != 2 ||
            !snapshot.DocumentModuleNames.Contains("Sheet1", StringComparer.Ordinal) ||
            !snapshot.DocumentModuleNames.Contains("ThisWorkbook", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial workbook must contain only the 'Sheet1' and 'ThisWorkbook' document modules.");
        }

        if (snapshot.ReferenceNames.Any(string.IsNullOrWhiteSpace) ||
            snapshot.ReferenceNames.Distinct(VbaProjectReferenceName.Comparer).Count() !=
            snapshot.ReferenceNames.Count ||
            snapshot.ReferenceNames.Count(VbaProjectReferenceName.IsStandardLibrary) != 1)
        {
            throw new InvalidOperationException(
                "The initial workbook reference baseline is incomplete or ambiguous.");
        }

        return snapshot.ReferenceNames
            .Where(referenceName =>
                !VbaProjectReferenceName.IsStandardLibrary(referenceName))
            .ToArray();
    }

    public static void ValidateUnchanged(
        InitialWorkbookBaselineSnapshot beforeSave,
        InitialWorkbookBaselineSnapshot afterSave)
    {
        ArgumentNullException.ThrowIfNull(beforeSave);
        ArgumentNullException.ThrowIfNull(afterSave);
        var unchanged = beforeSave.SheetCount == afterSave.SheetCount &&
            beforeSave.Worksheets.SequenceEqual(afterSave.Worksheets) &&
            string.Equals(
                beforeSave.WorkbookDocumentModuleName,
                afterSave.WorkbookDocumentModuleName,
                StringComparison.Ordinal) &&
            string.Equals(
                beforeSave.VbaProjectName,
                afterSave.VbaProjectName,
                StringComparison.Ordinal) &&
            beforeSave.ComponentCount == afterSave.ComponentCount &&
            beforeSave.DocumentModuleNames.ToHashSet(StringComparer.Ordinal)
                .SetEquals(afterSave.DocumentModuleNames) &&
            beforeSave.ReferenceNames.SequenceEqual(
                afterSave.ReferenceNames,
                StringComparer.Ordinal);
        if (!unchanged)
        {
            throw new InvalidOperationException(
                "The saved initial workbook no longer matches its verified Excel baseline.");
        }
    }
}

internal sealed class ExcelComInitialWorkbookSession(
    ExcelComWorkbookSession session) : IExcelComInitialWorkbookSession
{
    private const int VbextCtDocument = 100;

    public InitialWorkbookBaselineSnapshot EstablishAndReadBaseline()
    {
        EstablishExactIdentities();
        return ReadBaselineCore();
    }

    public void Save(
        string workbookPath,
        int fileFormat)
    {
        dynamic workbook = session.WorkbookObject;
        workbook.SaveAs(workbookPath, fileFormat);
    }

    public InitialWorkbookBaselineSnapshot ReadBaseline()
        => ReadBaselineCore();

    public void DisposeOwnedGeneration(TimeSpan cleanupGrace)
        => session.DisposeOwnedGeneration(cleanupGrace);

    private void EstablishExactIdentities()
    {
        object? sheetsObject = null;
        object? worksheetsObject = null;
        object? worksheetObject = null;
        object? vbProjectObject = null;
        object? componentsObject = null;
        try
        {
            dynamic workbook = session.WorkbookObject;
            sheetsObject = workbook.Sheets;
            worksheetsObject = workbook.Worksheets;
            dynamic sheets = sheetsObject;
            dynamic worksheets = worksheetsObject;
            if (Convert.ToInt32(sheets.Count) != 1 ||
                Convert.ToInt32(worksheets.Count) != 1)
            {
                throw new InvalidOperationException(
                    "Excel did not create the requested one-worksheet workbook baseline.");
            }

            worksheetObject = worksheets.Item(1);
            dynamic worksheet = worksheetObject;
            worksheet.Name = "Sheet1";

            vbProjectObject = workbook.VBProject;
            dynamic vbProject = vbProjectObject;
            vbProject.Name = "VBAProject";
            componentsObject = vbProject.VBComponents;

            RenameDocumentComponent(
                componentsObject,
                Convert.ToString(worksheet.CodeName),
                "Sheet1");
            RenameDocumentComponent(
                componentsObject,
                Convert.ToString(workbook.CodeName),
                "ThisWorkbook");
        }
        finally
        {
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(vbProjectObject);
            ComObjectReleaser.Release(worksheetObject);
            ComObjectReleaser.Release(worksheetsObject);
            ComObjectReleaser.Release(sheetsObject);
        }
    }

    private InitialWorkbookBaselineSnapshot ReadBaselineCore()
    {
        object? sheetsObject = null;
        object? worksheetsObject = null;
        object? worksheetObject = null;
        object? vbProjectObject = null;
        object? componentsObject = null;
        object? referencesObject = null;
        try
        {
            dynamic workbook = session.WorkbookObject;
            sheetsObject = workbook.Sheets;
            worksheetsObject = workbook.Worksheets;
            dynamic sheets = sheetsObject;
            dynamic worksheets = worksheetsObject;
            var sheetCount = Convert.ToInt32(sheets.Count);
            var worksheetCount = Convert.ToInt32(worksheets.Count);
            var worksheetIdentities = new List<InitialWorksheetIdentity>(worksheetCount);
            for (var index = 1; index <= worksheetCount; index++)
            {
                worksheetObject = worksheets.Item(index);
                try
                {
                    dynamic worksheet = worksheetObject;
                    worksheetIdentities.Add(new InitialWorksheetIdentity(
                        Convert.ToString(worksheet.Name) ?? string.Empty,
                        Convert.ToString(worksheet.CodeName) ?? string.Empty));
                }
                finally
                {
                    ComObjectReleaser.Release(worksheetObject);
                    worksheetObject = null;
                }
            }

            vbProjectObject = workbook.VBProject;
            dynamic vbProject = vbProjectObject;
            var projectName = Convert.ToString(vbProject.Name) ?? string.Empty;
            componentsObject = vbProject.VBComponents;
            dynamic components = componentsObject;
            var componentCount = Convert.ToInt32(components.Count);
            var documentModuleNames = new List<string>();
            for (var index = 1; index <= componentCount; index++)
            {
                object? componentObject = null;
                try
                {
                    componentObject = components.Item(index);
                    dynamic component = componentObject;
                    if (Convert.ToInt32(component.Type) == VbextCtDocument)
                    {
                        documentModuleNames.Add(
                            Convert.ToString(component.Name) ?? string.Empty);
                    }
                }
                finally
                {
                    ComObjectReleaser.Release(componentObject);
                }
            }

            referencesObject = vbProject.References;
            dynamic references = referencesObject;
            var referenceCount = Convert.ToInt32(references.Count);
            var referenceNames = new List<string>(referenceCount);
            for (var index = 1; index <= referenceCount; index++)
            {
                object? referenceObject = null;
                try
                {
                    referenceObject = references.Item(index);
                    dynamic reference = referenceObject;
                    referenceNames.Add(
                        Convert.ToString(reference.Description) ?? string.Empty);
                }
                finally
                {
                    ComObjectReleaser.Release(referenceObject);
                }
            }

            return new InitialWorkbookBaselineSnapshot(
                sheetCount,
                worksheetIdentities,
                Convert.ToString(workbook.CodeName) ?? string.Empty,
                projectName,
                componentCount,
                documentModuleNames,
                referenceNames);
        }
        finally
        {
            ComObjectReleaser.Release(referencesObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(vbProjectObject);
            ComObjectReleaser.Release(worksheetObject);
            ComObjectReleaser.Release(worksheetsObject);
            ComObjectReleaser.Release(sheetsObject);
        }
    }

    private static void RenameDocumentComponent(
        object componentsObject,
        string? currentName,
        string requiredName)
    {
        if (string.IsNullOrEmpty(currentName))
        {
            throw new InvalidOperationException(
                $"Excel did not expose the document module required for '{requiredName}'.");
        }

        if (string.Equals(currentName, requiredName, StringComparison.Ordinal))
        {
            return;
        }

        object? componentObject = null;
        try
        {
            dynamic components = componentsObject;
            componentObject = components.Item(currentName);
            dynamic component = componentObject;
            component.Name = requiredName;
        }
        finally
        {
            ComObjectReleaser.Release(componentObject);
        }
    }
}
