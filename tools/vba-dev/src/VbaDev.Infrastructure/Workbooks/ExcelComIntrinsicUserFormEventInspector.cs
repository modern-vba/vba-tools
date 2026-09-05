using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;
using VbaTools.Syntax;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed class UserFormEventInspectionStateUntrustedException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);

internal static class ExcelComIntrinsicUserFormEventInspector
{
    private const int FormComponentType = 3;
    private const int ProcedureKind = 0;

    public static UserFormEventInspectionResult Inspect(
        ExcelComWorkbookSession.ExcelComHostObjects host,
        object workbook,
        UserFormEventComponentDescriptor descriptor)
    {
        var processId = host.StrongExcelProcess?.ProcessId
            ?? throw new InvalidOperationException(
                "Intrinsic Host Event inspection requires an exactly owned Excel process.");
        object? projectObject = null;
        object? componentsObject = null;
        object? componentObject = null;
        object? codeModuleObject = null;
        object? codePaneObject = null;
        object? vbeObject = null;
        object? mainWindowObject = null;
        object? codeWindowObject = null;
        object? runtimeHostObject = null;
        string? originalCode = null;
        var originalLineCount = 0;
        var snapshotEstablished = false;
        var mainWindowHandle = nint.Zero;
        UserFormEventInspectionResult? result = null;
        var phase = InspectionPhase.EventSourceName;
        try
        {
            dynamic openedWorkbook = workbook;
            projectObject = openedWorkbook.VBProject;
            dynamic project = projectObject;
            componentsObject = project.VBComponents;
            dynamic components = componentsObject;
            componentObject = components.Item(descriptor.Ordinal);
            dynamic component = componentObject;
            ValidateReacquiredComponent(component, descriptor.Identity);
            runtimeHostObject = component.Designer;
            codeModuleObject = component.CodeModule;
            dynamic codeModule = codeModuleObject;
            originalLineCount = (int)codeModule.CountOfLines;
            originalCode = originalLineCount == 0
                ? string.Empty
                : (string)codeModule.Lines(1, originalLineCount);
            snapshotEstablished = true;
            if (originalLineCount != 0)
            {
                codeModule.DeleteLines(1, originalLineCount);
            }

            var controlNames = ReadFormControlNames(runtimeHostObject);
            codePaneObject = codeModule.CodePane;
            dynamic codePane = codePaneObject;
            vbeObject = codeModule.VBE;
            dynamic vbe = vbeObject;
            mainWindowObject = vbe.MainWindow;
            dynamic mainWindow = mainWindowObject;
            mainWindowHandle = new nint(Convert.ToInt64(mainWindow.HWnd));
            if (mainWindowHandle == nint.Zero)
            {
                mainWindow.Left = -32_000;
                mainWindow.Top = -32_000;
            }
            else
            {
                VbeCodeWindowNavigation.PrepareOffscreen(mainWindowHandle, processId);
            }

            component.Activate();
            codePane.Show();
            vbe.ActiveCodePane = codePaneObject;
            codeWindowObject = codePane.Window;
            dynamic codeWindow = codeWindowObject;
            _ = Convert.ToInt64(codeWindow.HWnd);
            mainWindowHandle = new nint(Convert.ToInt64(mainWindow.HWnd));
            VbeCodeWindowNavigation.PrepareOffscreen(mainWindowHandle, processId);
            var (navigation, objectItems) = WaitForIntrinsicObjectItems(
                mainWindowHandle,
                processId);
            var intrinsicCandidates = objectItems
                .Select((name, index) => (Name: name, Index: index))
                .Skip(objectItems.Count > 1 ? 1 : 0)
                .Where(candidate => !controlNames.Contains(candidate.Name))
                .ToArray();
            if (intrinsicCandidates.Length != 1 ||
                !IsIntrinsicSourceName(intrinsicCandidates[0].Name))
            {
                var objectItemSummary = string.Join(
                    ", ",
                    objectItems.Select(item => $"'{item}'"));
                var controlSummary = string.Join(
                    ", ",
                    controlNames.Select(item => $"'{item}'"));
                throw new InvalidOperationException(
                    $"VBE exposed {intrinsicCandidates.Length} intrinsic Object-box " +
                    $"candidates instead of one (items: [{objectItemSummary}]; " +
                    $"controls: [{controlSummary}]).");
            }

            var intrinsicSourceName = intrinsicCandidates[0].Name;
            VbeCodeWindowNavigation.SelectObject(
                navigation,
                intrinsicCandidates[0].Index);
            navigation = VbeCodeWindowNavigation.DiscoverActiveCodeWindow(
                mainWindowHandle,
                processId);
            phase = InspectionPhase.EventEnumeration;
            if (!UserFormEventTypeLibSurfaceReader.TryRead(
                    runtimeHostObject,
                    out var typeLibSurface) ||
                typeLibSurface.Events.Count == 0)
            {
                throw new InvalidOperationException(
                    "The complete structural TypeLib Event surface could not be read.");
            }

            var authoringEventNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var seedEventName in typeLibSurface.Events.Keys.Where(
                         eventName => CanAuthorEvent(intrinsicSourceName, eventName)))
            {
                try
                {
                    _ = (int)codeModule.CreateEventProc(
                        seedEventName,
                        intrinsicSourceName);
                }
                catch (COMException exception) when (IsUnavailableAuthoringEvent(exception))
                {
                    if ((int)codeModule.CountOfLines != 0)
                    {
                        throw new UserFormEventInspectionStateUntrustedException(
                            $"Unavailable authoring probe '{seedEventName}' changed the CodeModule.");
                    }

                    continue;
                }

                try
                {
                    navigation = VbeCodeWindowNavigation.DiscoverActiveCodeWindow(
                        mainWindowHandle,
                        processId);
                    var seededObjectItems = VbeCodeWindowNavigation.ReadObjectItems(navigation);
                    var intrinsicObjectIndex = seededObjectItems
                        .Select((name, index) => (Name: name, Index: index))
                        .Where(candidate => candidate.Name.Equals(
                            intrinsicSourceName,
                            StringComparison.OrdinalIgnoreCase))
                        .Select(candidate => candidate.Index)
                        .Single();
                    VbeCodeWindowNavigation.SelectObject(
                        navigation,
                        intrinsicObjectIndex);
                    navigation = VbeCodeWindowNavigation.DiscoverActiveCodeWindow(
                        mainWindowHandle,
                        processId);
                    foreach (var candidate in VbeCodeWindowNavigation
                                 .ReadProcedureItems(navigation)
                                 .Where(candidate => CanAuthorEvent(
                                     intrinsicSourceName,
                                     candidate)))
                    {
                        authoringEventNames.Add(candidate);
                    }

                    authoringEventNames.Add(seedEventName);
                }
                finally
                {
                    var seededLineCount = (int)codeModule.CountOfLines;
                    if (seededLineCount != 0)
                    {
                        codeModule.DeleteLines(1, seededLineCount);
                    }
                    if ((int)codeModule.CountOfLines != 0)
                    {
                        throw new UserFormEventInspectionStateUntrustedException(
                            $"The authoring-list seed '{seedEventName}' was not removed exactly.");
                    }
                }

                break;
            }

            var structuralEventNames = authoringEventNames
                .Concat(typeLibSurface.Events.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var events = new List<UserFormEventObservation>(structuralEventNames.Length);
            foreach (var eventName in structuralEventNames)
            {
                phase = InspectionPhase.Signature;
                if (!CanAuthorEvent(intrinsicSourceName, eventName))
                {
                    var structuralOnlyEvent = typeLibSurface.Events[eventName];
                    events.Add(new UserFormEventObservation(
                        eventName,
                        structuralOnlyEvent.Parameters,
                        structuralOnlyEvent.Documentation,
                        AuthoringAvailable: false,
                        ExistingHandlerRecognizable: false));
                    continue;
                }

                var beforeLineCount = (int)codeModule.CountOfLines;
                var procedureName = $"{intrinsicSourceName}_{eventName}";
                string probeSource;
                UserFormEventObservation signature;
                try
                {
                    var bodyLine = (int)codeModule.CreateEventProc(
                        eventName,
                        intrinsicSourceName);
                    var procedureStart = (int)codeModule.ProcStartLine(
                        procedureName,
                        ProcedureKind);
                    var procedureLineCount = (int)codeModule.ProcCountLines(
                        procedureName,
                        ProcedureKind);
                    if (procedureStart <= 0 ||
                        bodyLine < procedureStart ||
                        procedureLineCount <= 0)
                    {
                        throw new InvalidOperationException(
                            $"VBE returned inconsistent bounds for generated Event '{eventName}'.");
                    }

                    probeSource = (string)codeModule.Lines(
                        procedureStart,
                        procedureLineCount);
                    signature = UserFormEventEvidenceMerger.Merge(
                        UserFormEventGeneratedSignatureParser.Parse(
                            eventName,
                            procedureName,
                            probeSource,
                            authoringAvailable: true,
                            existingHandlerRecognizable: false),
                        typeLibSurface);
                    codeModule.DeleteLines(procedureStart, procedureLineCount);
                    if ((int)codeModule.CountOfLines != beforeLineCount)
                    {
                        throw new UserFormEventInspectionStateUntrustedException(
                            $"The generated '{eventName}' Event procedure was not removed exactly.");
                    }
                }
                catch (COMException exception) when (
                    IsUnavailableAuthoringEvent(exception) &&
                    !authoringEventNames.Contains(eventName) &&
                    typeLibSurface.Events.ContainsKey(eventName))
                {
                    if ((int)codeModule.CountOfLines != beforeLineCount)
                    {
                        throw new UserFormEventInspectionStateUntrustedException(
                            $"Unavailable Event '{eventName}' changed the CodeModule during authoring inspection.");
                    }

                    var structuralEvent = typeLibSurface.Events[eventName];
                    probeSource = RenderStructuralEventProcedure(
                        intrinsicSourceName,
                        structuralEvent);
                    signature = new UserFormEventObservation(
                        eventName,
                        structuralEvent.Parameters,
                        structuralEvent.Documentation,
                        AuthoringAvailable: false,
                        ExistingHandlerRecognizable: false);
                }
                catch (Exception exception) when (IsInspectableFailure(exception))
                {
                    throw new InvalidOperationException(
                        $"VBE could not generate structural Event '{eventName}' " +
                        $"(HRESULT 0x{exception.HResult:X8}).",
                        exception);
                }

                phase = InspectionPhase.Availability;
                var recognizable = ProbeExistingHandlerRecognition(
                    codeModuleObject,
                    codePaneObject,
                    mainWindowHandle,
                    processId,
                    intrinsicSourceName,
                    eventName,
                    probeSource);
                events.Add(signature with
                {
                    ExistingHandlerRecognizable = recognizable
                });
            }

            result = new ResolvedUserFormEventInspection(
                descriptor.Identity,
                intrinsicSourceName,
                events,
                typeLibSurface.BaseType);
        }
        catch (UserFormEventInspectionStateUntrustedException)
        {
            throw;
        }
        catch (UserFormEventObservationConflictException exception)
        {
            result = new UnverifiedUserFormEventInspection(
                descriptor.Identity,
                exception.Reason,
                exception.Message);
        }
        catch (Exception exception) when (IsInspectableFailure(exception))
        {
            result = new UnverifiedUserFormEventInspection(
                descriptor.Identity,
                phase switch
                {
                    InspectionPhase.EventSourceName =>
                        UserFormEventInspectionFailureReason.IntrinsicEventSourceNameReadFailure,
                    InspectionPhase.EventEnumeration =>
                        UserFormEventInspectionFailureReason.EventEnumerationFailure,
                    InspectionPhase.Signature =>
                        UserFormEventInspectionFailureReason.SignatureReadFailure,
                    InspectionPhase.Availability =>
                        UserFormEventInspectionFailureReason.AvailabilityReadFailure,
                    _ => UserFormEventInspectionFailureReason.InspectionFailure
                },
                $"Intrinsic host Event inspection failed during {Describe(phase)}: {exception.Message}");
        }
        finally
        {
            Exception? restoreError = null;
            if (snapshotEstablished && codeModuleObject is not null)
            {
                try
                {
                    dynamic codeModule = codeModuleObject;
                    var currentLineCount = (int)codeModule.CountOfLines;
                    if (currentLineCount != 0)
                    {
                        codeModule.DeleteLines(1, currentLineCount);
                    }

                    if (originalLineCount != 0)
                    {
                        codeModule.InsertLines(1, originalCode!);
                    }

                    var restoredLineCount = (int)codeModule.CountOfLines;
                    var restoredCode = restoredLineCount == 0
                        ? string.Empty
                        : (string)codeModule.Lines(1, restoredLineCount);
                    if (restoredLineCount != originalLineCount ||
                        !restoredCode.Equals(originalCode, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The intrinsic class CodeModule did not match its " +
                            "invocation-start snapshot after inspection.");
                    }
                }
                catch (Exception exception)
                {
                    restoreError = exception;
                }
            }

            ComObjectReleaser.Release(codeWindowObject);
            if (mainWindowHandle != nint.Zero)
            {
                try
                {
                    VbeCodeWindowNavigation.HideOffscreen(mainWindowHandle, processId);
                }
                catch (Exception exception)
                {
                    restoreError = restoreError is null
                        ? exception
                        : new AggregateException(restoreError, exception);
                }
            }

            ComObjectReleaser.Release(mainWindowObject);
            ComObjectReleaser.Release(vbeObject);
            ComObjectReleaser.Release(codePaneObject);
            ComObjectReleaser.Release(codeModuleObject);
            if (!ReferenceEquals(runtimeHostObject, workbook))
            {
                ComObjectReleaser.Release(runtimeHostObject);
            }
            ComObjectReleaser.Release(componentObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
            if (restoreError is not null)
            {
                throw new UserFormEventInspectionStateUntrustedException(
                    "The intrinsic Host Event inspection state became untrustworthy " +
                    "because CodeModule rollback was not exact.",
                    restoreError);
            }
        }

        return result!;
    }

    private static bool ProbeExistingHandlerRecognition(
        object codeModuleObject,
        object codePaneObject,
        nint mainWindowHandle,
        int processId,
        string intrinsicSourceName,
        string eventName,
        string targetSource)
    {
        const string sentinelName = "VbaDevHostProbeSentinel";
        var targetProcedureName = $"{intrinsicSourceName}_{eventName}";
        var sentinelSource =
            $"Private Sub {sentinelName}()\r\n\r\nEnd Sub\r\n";
        dynamic codeModule = codeModuleObject;
        dynamic codePane = codePaneObject;
        var beforeLineCount = (int)codeModule.CountOfLines;
        if (beforeLineCount != 0)
        {
            throw new UserFormEventInspectionStateUntrustedException(
                $"The CodeModule was not empty before probing Event '{eventName}'.");
        }

        try
        {
            codeModule.InsertLines(1, sentinelSource + targetSource);
            var sentinelBodyLine = (int)codeModule.ProcBodyLine(
                sentinelName,
                ProcedureKind);
            var targetBodyLine = (int)codeModule.ProcBodyLine(
                targetProcedureName,
                ProcedureKind);
            codePane.SetSelection(
                sentinelBodyLine,
                1,
                sentinelBodyLine,
                1);
            if (!WaitForProcedureAssociation(
                    mainWindowHandle,
                    processId,
                    (currentObject, currentProcedure) =>
                        !currentObject.Equals(
                            intrinsicSourceName,
                            StringComparison.OrdinalIgnoreCase) &&
                        currentProcedure.Equals(
                            sentinelName,
                            StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"VBE did not settle on the ordinary-procedure sentinel before probing Event '{eventName}'.");
            }

            codePane.SetSelection(
                targetBodyLine,
                1,
                targetBodyLine,
                1);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            do
            {
                var navigation = VbeCodeWindowNavigation.DiscoverActiveCodeWindow(
                    mainWindowHandle,
                    processId);
                var currentObject = VbeCodeWindowNavigation.ReadCurrentObject(navigation);
                var currentProcedure = VbeCodeWindowNavigation.ReadCurrentProcedure(navigation);
                if (currentObject.Equals(
                        intrinsicSourceName,
                        StringComparison.OrdinalIgnoreCase) &&
                    currentProcedure.Equals(
                        eventName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!currentObject.Equals(
                        intrinsicSourceName,
                        StringComparison.OrdinalIgnoreCase) &&
                    currentProcedure.Equals(
                        targetProcedureName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Thread.Sleep(20);
            }
            while (DateTime.UtcNow < deadline);

            throw new InvalidOperationException(
                $"VBE did not establish whether existing handler '{targetProcedureName}' is recognizable.");
        }
        finally
        {
            var probeLineCount = (int)codeModule.CountOfLines;
            if (probeLineCount != 0)
            {
                codeModule.DeleteLines(1, probeLineCount);
            }

            if ((int)codeModule.CountOfLines != beforeLineCount)
            {
                throw new UserFormEventInspectionStateUntrustedException(
                    $"The recognition probe for Event '{eventName}' was not removed exactly.");
            }
        }
    }

    private static bool WaitForProcedureAssociation(
        nint mainWindowHandle,
        int processId,
        Func<string, string, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        do
        {
            var navigation = VbeCodeWindowNavigation.DiscoverActiveCodeWindow(
                mainWindowHandle,
                processId);
            var currentObject = VbeCodeWindowNavigation.ReadCurrentObject(navigation);
            var currentProcedure = VbeCodeWindowNavigation.ReadCurrentProcedure(navigation);
            if (predicate(currentObject, currentProcedure))
            {
                return true;
            }

            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static string RenderStructuralEventProcedure(
        string intrinsicSourceName,
        UserFormTypeLibEvent structuralEvent)
    {
        var procedureName = $"{intrinsicSourceName}_{structuralEvent.Name}";
        if (!IsSafeVbaIdentifier(procedureName))
        {
            throw new InvalidOperationException(
                "The structural Event probe name is not a safe VBA identifier.");
        }

        if (structuralEvent.Parameters.Count == 0)
        {
            return $"Private Sub {procedureName}()\r\n\r\nEnd Sub\r\n";
        }

        var parameters = structuralEvent.Parameters
            .Select((parameter, index) =>
                $"    {RenderStructuralEventParameter(parameter, index)}" +
                (index < structuralEvent.Parameters.Count - 1 ? ", _" : ")"));
        return $"Private Sub {procedureName}( _\r\n" +
            string.Join("\r\n", parameters) +
            "\r\n" +
            "\r\n" +
            "End Sub\r\n";
    }

    private static string RenderStructuralEventParameter(
        ObservedHostEventParameter parameter,
        int index)
    {
        if (parameter.Optional)
        {
            throw new InvalidOperationException(
                "A structural Optional Event parameter cannot be rendered without its observed default value.");
        }

        var parameterName = $"arg{index + 1}";
        if (parameter.ParamArray)
        {
            if (parameter.ArrayShape != ObservedHostEventArrayShape.Array ||
                parameter.Type is not ObservedIntrinsicHostEventTypeReference
                {
                    Name: var name
                } ||
                !name.Equals("Variant", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "A structural ParamArray Event parameter must be a Variant array.");
            }

            return $"ParamArray {parameterName}() As Variant";
        }

        if (parameter.ArrayShape == ObservedHostEventArrayShape.Array &&
            parameter.Passing != ObservedHostEventPassingMechanism.ByRef)
        {
            throw new InvalidOperationException(
                "A structural Event array probe must use ByRef passing.");
        }

        var passing = parameter.ParamArray
            ? string.Empty
            : parameter.Passing == ObservedHostEventPassingMechanism.ByRef
                ? "ByRef "
                : "ByVal ";
        var array = parameter.ArrayShape == ObservedHostEventArrayShape.Array
            ? "()"
            : string.Empty;
        return $"{passing}{parameterName}{array} As {RenderProbeType(parameter.Type)}";
    }

    private static string RenderProbeType(ObservedHostEventTypeReference type)
    {
        return type switch
        {
            ObservedIntrinsicHostEventTypeReference intrinsic
                when VbaLanguageVocabulary.TryGetCanonicalTypeName(
                    intrinsic.Name,
                    out var canonicalName) => canonicalName,
            ObservedTypeLibHostEventTypeReference typeLib =>
                RenderTypeLibProbeTypeName(typeLib.Name),
            ObservedUnresolvedHostEventTypeReference => throw new InvalidOperationException(
                "An unresolved TypeLib Event type cannot be rendered for recognition probing."),
            _ => throw new InvalidOperationException(
                $"Unsupported host Event type reference '{type.GetType().Name}'.")
        };
    }

    internal static string RenderTypeLibProbeTypeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Contains('\r') || name.Contains('\n'))
        {
            throw new InvalidOperationException(
                "A TypeLib probe type requires a nonempty, line-local unrestricted name.");
        }

        return VbaIdentifier.IsIdentifier(name) ? name : $"[{name}]";
    }

    private static bool IsSafeVbaIdentifier(string value)
        => value.Length is > 0 and <= 255
            && VbaIdentifier.IsIdentifier(value);

    internal static bool IsIntrinsicSourceName(string? value)
        => value is not null
            && VbaIdentifier.IsIdentifier(value)
            && value.EnumerateRunes().Count() <= 31;

    internal static bool IsAuthoringEventName(string value)
        => value.EnumerateRunes().Count() is > 0 and <= 255
            && VbaIdentifier.IsLexIdentifier(value);

    internal static bool CanAuthorEvent(string intrinsicSourceName, string eventName)
        => IsIntrinsicSourceName(intrinsicSourceName)
            && IsAuthoringEventName(eventName)
            && IsSafeVbaIdentifier($"{intrinsicSourceName}_{eventName}");

    private static void ValidateReacquiredComponent(
        dynamic component,
        UserFormEventComponentIdentity identity)
    {
        var actualName = (string)component.Name;
        var actualType = (int)component.Type;
        if (!actualName.Equals(identity.Name, StringComparison.OrdinalIgnoreCase) ||
            actualType != FormComponentType)
        {
            throw new UserFormEventInspectionStateUntrustedException(
                $"The generated UserForm ordinal changed from '{identity.Name}' before Event inspection.");
        }
    }

    private static (
        VbeCodeWindowNavigationPair Navigation,
        IReadOnlyList<string> Items) WaitForIntrinsicObjectItems(
            nint mainWindow,
            int processId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        VbeCodeWindowNavigationPair? navigation = null;
        IReadOnlyList<string> items = [];
        do
        {
            navigation = VbeCodeWindowNavigation.DiscoverActiveCodeWindow(
                mainWindow,
                processId);
            items = VbeCodeWindowNavigation.ReadObjectItems(navigation);
            if (items.Count > 1)
            {
                break;
            }

            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < deadline);

        return (navigation!, items);
    }

    private static HashSet<string> ReadFormControlNames(object designerObject)
    {
        object? controlsObject = null;
        try
        {
            dynamic designer = designerObject;
            controlsObject = designer.Controls;
            dynamic controls = controlsObject;
            if ((int)controls.Count != 0)
            {
                throw new InvalidOperationException(
                    "The catalog UserForm must remain empty during Event inspection.");
            }

            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            ComObjectReleaser.Release(controlsObject);
        }
    }

    private static bool IsInspectableFailure(Exception exception)
        => exception is COMException or RuntimeBinderException or InvalidCastException or
            ArgumentException or InvalidOperationException or OverflowException or
            TargetParameterCountException;

    private static bool IsUnavailableAuthoringEvent(COMException exception)
        => exception.HResult == unchecked((int)0x800A01B8);

    private static string Describe(InspectionPhase phase)
        => phase switch
        {
            InspectionPhase.EventSourceName => "intrinsic Event source-name discovery",
            InspectionPhase.EventEnumeration => "Event enumeration",
            InspectionPhase.Signature => "Event signature capture",
            InspectionPhase.Availability => "Event availability inspection",
            _ => "Host Event inspection"
        };

    private enum InspectionPhase
    {
        EventSourceName,
        EventEnumeration,
        Signature,
        Availability
    }
}
