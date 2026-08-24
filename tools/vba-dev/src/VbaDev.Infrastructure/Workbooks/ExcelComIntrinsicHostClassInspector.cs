using System.Runtime.InteropServices;
using System.Reflection;
using Microsoft.CSharp.RuntimeBinder;
using VbaDev.App.HostClasses;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed class HostClassInspectionStateUntrustedException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);

internal static class ExcelComIntrinsicHostClassInspector
{
    private const int FormComponentType = 3;
    private const int DocumentComponentType = 100;
    private const int ProcedureKind = 0;

    public static HostClassInspectionEntry Inspect(
        ExcelComWorkbookSession.ExcelComHostObjects host,
        object workbook,
        HostClassComponentDescriptor descriptor)
    {
        var processId = host.StrongExcelProcess?.ProcessId
            ?? throw new InvalidOperationException(
                "Intrinsic host-class inspection requires an exactly owned Excel process.");
        object? projectObject = null;
        object? componentsObject = null;
        object? componentObject = null;
        object? codeModuleObject = null;
        object? codePaneObject = null;
        object? vbeObject = null;
        object? mainWindowObject = null;
        object? codeWindowObject = null;
        object? runtimeHostObject = null;
        object? sheetsObject = null;
        string? originalCode = null;
        var originalLineCount = 0;
        var snapshotEstablished = false;
        var mainWindowHandle = nint.Zero;
        HostClassInspectionEntry? result = null;
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
            runtimeHostObject = BindRuntimeHostObject(
                openedWorkbook,
                component,
                descriptor.Identity,
                out sheetsObject);
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

            HashSet<string> controlNames = descriptor.Identity.Kind == HostClassComponentKind.Form
                ? ReadFormControlNames(runtimeHostObject)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                string.IsNullOrWhiteSpace(intrinsicCandidates[0].Name))
            {
                throw new InvalidOperationException(
                    $"VBE exposed {intrinsicCandidates.Length} intrinsic Object-box candidates instead of one (items: [{string.Join(", ", objectItems.Select(item => $"'{item}'"))}]; controls: [{string.Join(", ", controlNames.Select(item => $"'{item}'"))}]).");
            }

            var intrinsicSourceName = intrinsicCandidates[0].Name;
            VbeCodeWindowNavigation.SelectObject(
                navigation,
                intrinsicCandidates[0].Index);
            navigation = VbeCodeWindowNavigation.DiscoverActiveCodeWindow(
                mainWindowHandle,
                processId);
            phase = InspectionPhase.EventEnumeration;
            if (!HostClassTypeLibEventSurfaceReader.TryRead(
                    runtimeHostObject,
                    out var typeLibSurface) ||
                typeLibSurface.Events.Count == 0)
            {
                throw new InvalidOperationException(
                    "The complete structural TypeLib Event surface could not be read.");
            }

            var authoringEventNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var seedEventName in typeLibSurface.Events.Keys)
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
                        throw new HostClassInspectionStateUntrustedException(
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
                                 .Where(IsSafeVbaIdentifier))
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
                        throw new HostClassInspectionStateUntrustedException(
                            $"The authoring-list seed '{seedEventName}' was not removed exactly.");
                    }
                }

                break;
            }

            var structuralEventNames = authoringEventNames
                .Concat(typeLibSurface.Events.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var events = new List<HostEventSignature>(structuralEventNames.Length);
            foreach (var eventName in structuralEventNames)
            {
                phase = InspectionPhase.Signature;
                var beforeLineCount = (int)codeModule.CountOfLines;
                var procedureName = $"{intrinsicSourceName}_{eventName}";
                string probeSource;
                HostEventSignature signature;
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
                    signature = HostClassTypeLibEventEvidenceMerger.Merge(
                        HostClassGeneratedSignatureParser.Parse(
                            eventName,
                            procedureName,
                            probeSource,
                            authoringAvailable: true,
                            existingHandlerRecognizable: false),
                        typeLibSurface);
                    codeModule.DeleteLines(procedureStart, procedureLineCount);
                    if ((int)codeModule.CountOfLines != beforeLineCount)
                    {
                        throw new HostClassInspectionStateUntrustedException(
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
                        throw new HostClassInspectionStateUntrustedException(
                            $"Unavailable Event '{eventName}' changed the CodeModule during authoring inspection.");
                    }

                    var structuralEvent = typeLibSurface.Events[eventName];
                    probeSource = RenderStructuralEventProcedure(
                        intrinsicSourceName,
                        structuralEvent);
                    signature = new HostEventSignature(
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

            result = new ResolvedHostClassInspectionEntry(
                descriptor.Identity,
                intrinsicSourceName,
                events,
                typeLibSurface.BaseType);
        }
        catch (HostClassInspectionStateUntrustedException)
        {
            throw;
        }
        catch (HostClassEventObservationConflictException exception)
        {
            result = new UnverifiedHostClassInspectionEntry(
                descriptor.Identity,
                exception.Reason,
                exception.Message);
        }
        catch (Exception exception) when (IsInspectableFailure(exception))
        {
            result = new UnverifiedHostClassInspectionEntry(
                descriptor.Identity,
                phase switch
                {
                    InspectionPhase.EventSourceName =>
                        HostClassInspectionFailureReason.IntrinsicEventSourceNameReadFailure,
                    InspectionPhase.EventEnumeration =>
                        HostClassInspectionFailureReason.EventEnumerationFailure,
                    InspectionPhase.Signature =>
                        HostClassInspectionFailureReason.SignatureReadFailure,
                    InspectionPhase.Availability =>
                        HostClassInspectionFailureReason.AvailabilityReadFailure,
                    _ => HostClassInspectionFailureReason.InspectionFailure
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
                            "The intrinsic class CodeModule did not match its invocation-start snapshot after inspection.");
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
            ComObjectReleaser.Release(sheetsObject);
            ComObjectReleaser.Release(componentObject);
            ComObjectReleaser.Release(componentsObject);
            ComObjectReleaser.Release(projectObject);
            if (restoreError is not null)
            {
                throw new HostClassInspectionStateUntrustedException(
                    "The intrinsic host-class inspection state became untrustworthy because CodeModule rollback was not exact.",
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
            throw new HostClassInspectionStateUntrustedException(
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
                throw new HostClassInspectionStateUntrustedException(
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
        HostClassTypeLibEvent structuralEvent)
    {
        if (!IsSafeVbaIdentifier(intrinsicSourceName) ||
            !IsSafeVbaIdentifier(structuralEvent.Name))
        {
            throw new InvalidOperationException(
                "The structural Event probe name is not a safe VBA identifier.");
        }

        var procedureName = $"{intrinsicSourceName}_{structuralEvent.Name}";
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
        HostEventParameter parameter,
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
            if (parameter.ArrayShape != HostEventArrayShape.Array ||
                parameter.Type is not IntrinsicHostEventTypeReference
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

        if (parameter.ArrayShape == HostEventArrayShape.Array &&
            parameter.Passing != HostEventPassingMechanism.ByRef)
        {
            throw new InvalidOperationException(
                "A structural Event array probe must use ByRef passing.");
        }

        var passing = parameter.ParamArray
            ? string.Empty
            : parameter.Passing == HostEventPassingMechanism.ByRef
                ? "ByRef "
                : "ByVal ";
        var array = parameter.ArrayShape == HostEventArrayShape.Array
            ? "()"
            : string.Empty;
        return $"{passing}{parameterName}{array} As {RenderProbeType(parameter.Type)}";
    }

    private static string RenderProbeType(HostEventTypeReference type)
    {
        var name = type switch
        {
            IntrinsicHostEventTypeReference intrinsic => intrinsic.Name,
            TypeLibHostEventTypeReference typeLib => typeLib.Name,
            UnresolvedHostEventTypeReference => throw new InvalidOperationException(
                "An unresolved TypeLib Event type cannot be rendered for recognition probing."),
            _ => throw new InvalidOperationException(
                $"Unsupported host Event type reference '{type.GetType().Name}'.")
        };

        return IsSafeVbaIdentifier(name)
            ? name
            : throw new InvalidOperationException(
                $"Structural Event probe type '{name}' is not a safe VBA identifier.");
    }

    private static bool IsSafeVbaIdentifier(string value)
    {
        if (value.Length is 0 or > 255 || !IsAsciiIdentifierStart(value[0]))
        {
            return false;
        }

        return value.Skip(1).All(character =>
            IsAsciiIdentifierStart(character) || char.IsAsciiDigit(character));
    }

    private static bool IsAsciiIdentifierStart(char character)
        => character is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static object BindRuntimeHostObject(
        dynamic workbook,
        dynamic component,
        HostClassIdentity identity,
        out object? sheetsObject)
    {
        sheetsObject = null;
        if (identity.Kind == HostClassComponentKind.Form)
        {
            return component.Designer;
        }

        var workbookCodeName = (string)workbook.CodeName;
        if (workbookCodeName.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
        {
            return workbook;
        }

        sheetsObject = workbook.Sheets;
        dynamic sheets = sheetsObject;
        object? match = null;
        var count = (int)sheets.Count;
        for (var index = 1; index <= count; index++)
        {
            object? candidateObject = null;
            try
            {
                candidateObject = sheets.Item(index);
                dynamic candidate = candidateObject;
                var codeName = (string)candidate.CodeName;
                if (!codeName.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (match is not null)
                {
                    ComObjectReleaser.Release(match);
                    match = null;
                    throw new HostClassInspectionStateUntrustedException(
                        $"More than one workbook host object has CodeName '{identity.Name}'.");
                }

                match = candidateObject;
                candidateObject = null;
            }
            finally
            {
                ComObjectReleaser.Release(candidateObject);
            }
        }

        return match ?? throw new InvalidOperationException(
            $"No workbook host object has CodeName '{identity.Name}'.");
    }

    private static void ValidateReacquiredComponent(
        dynamic component,
        HostClassIdentity identity)
    {
        var actualName = (string)component.Name;
        var actualType = (int)component.Type;
        var expectedType = identity.Kind switch
        {
            HostClassComponentKind.Form => FormComponentType,
            HostClassComponentKind.Document => DocumentComponentType,
            _ => throw new ArgumentOutOfRangeException(nameof(identity), identity.Kind, null)
        };
        if (!actualName.Equals(identity.Name, StringComparison.OrdinalIgnoreCase) ||
            actualType != expectedType)
        {
            throw new HostClassInspectionStateUntrustedException(
                $"VBComponent ordinal changed from '{identity.Kind} {identity.Name}' before class inspection.");
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
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic designer = designerObject;
            controlsObject = designer.Controls;
            ReadControlNames(controlsObject, names);
            return names;
        }
        finally
        {
            ComObjectReleaser.Release(controlsObject);
        }
    }

    private static void ReadControlNames(object controlsObject, HashSet<string> names)
    {
        dynamic controls = controlsObject;
        var count = (int)controls.Count;
        for (var index = 0; index < count; index++)
        {
            object? controlObject = null;
            object? childControlsObject = null;
            try
            {
                controlObject = controls.Item(index);
                dynamic control = controlObject;
                var name = Convert.ToString(control.Name);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }

                try
                {
                    childControlsObject = control.Controls;
                    ReadControlNames(childControlsObject, names);
                }
                catch (Exception exception) when (IsMissingMember(exception))
                {
                }
            }
            finally
            {
                ComObjectReleaser.Release(childControlsObject);
                ComObjectReleaser.Release(controlObject);
            }
        }
    }

    private static bool IsInspectableFailure(Exception exception)
        => exception is COMException or RuntimeBinderException or InvalidCastException or
            ArgumentException or InvalidOperationException or OverflowException or
            TargetParameterCountException;

    private static bool IsUnavailableAuthoringEvent(COMException exception)
        => exception.HResult == unchecked((int)0x800A01B8);

    private static bool IsMissingMember(Exception exception)
        => exception is COMException or RuntimeBinderException or MissingMemberException;

    private static string Describe(InspectionPhase phase)
        => phase switch
        {
            InspectionPhase.EventSourceName => "intrinsic Event source-name discovery",
            InspectionPhase.EventEnumeration => "Event enumeration",
            InspectionPhase.Signature => "Event signature capture",
            InspectionPhase.Availability => "Event availability inspection",
            _ => "host-class inspection"
        };

    private enum InspectionPhase
    {
        EventSourceName,
        EventEnumeration,
        Signature,
        Availability
    }
}
