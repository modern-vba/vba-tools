using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using VbaDebugAdapter.Debugging;
using VbaTools.Syntax;

namespace VbaDebugAdapter.Infrastructure;

/// <summary>Borrowed native context; the concrete session retains all lifetime policy.</summary>
internal interface IVbeNativeRunPreparation
{
    void Begin();
    void VerifyObservation();
}

internal interface IVbeNativeOperationAdapter
{
    object Application { get; }
    object GetCurrentWorkbook();
    void AllowForeground();
    void BringVbeToForeground(nint handle);
    void ReleaseScope(Exception? failure, params (string Resource, object? Value)[] references);
}

/// <summary>Owns exact native breakpoint, Run and inspection proof without acquiring Excel or COM lifetime.</summary>
internal sealed class VbeNativeOperations(IVbeNativeOperationAdapter adapter)
{
    private sealed record NativeContext(object Workbook, object Project, object Components,
        object Component, object Code, int Line, int Mode,
        VbeBreakpoint? Breakpoint = null, DebugTargetProcedure? Target = null)
    {
        public int CommandId => Breakpoint is null ? VbeNativeCommandContract.RunOrContinueCommandId
            : VbeNativeCommandContract.ToggleBreakpointCommandId;
        public string CommandName => Breakpoint is null ? "Run Sub/UserForm" : "Toggle Breakpoint";
        public string ContextName => Breakpoint is not null ? "breakpoint"
            : Mode == 1 ? "break mode" : "target code";
    }

    public void RunTarget(DebugTargetProcedure target, IVbeNativeRunPreparation preparation,
        CancellationToken token)
        => ProcessTarget(target, 2, execute: true, preparation, token);

    public void ContinueTarget(DebugTargetProcedure target, CancellationToken token)
        => ProcessTarget(target, 1, execute: true, preparation: null, token);

    public void InspectTarget(DebugTargetProcedure target, CancellationToken token)
        => ProcessTarget(target, 2, execute: false, preparation: null, token);

    private void ProcessTarget(DebugTargetProcedure target, int mode, bool execute,
        IVbeNativeRunPreparation? preparation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var references = new List<(string Resource, object? Value)>();
        Exception? failure = null;
        int? selectedLine = null;
        try
        {
            var workbook = GetWorkbook(null);
            dynamic book = workbook;
            object projectObject = book.VBProject;
            references.Insert(0, ("target VBA project", projectObject));
            dynamic project = projectObject;
            if ((int)project.Mode != mode)
                throw new DebugSetupException(mode == 2
                    ? "The workbook VBA project is not in design mode."
                    : "The workbook VBA project is not in break mode.");
            object componentsObject = project.VBComponents;
            references.Insert(0, ("target VBA components", componentsObject));
            dynamic components = componentsObject;
            object componentObject = components.Item(target.ModuleName);
            references.Insert(0, ("target component", componentObject));
            dynamic component = componentObject;
            object codeObject = component.CodeModule;
            references.Insert(0, ("target code module", codeObject));
            dynamic code = codeObject;
            var line = (int)code.ProcBodyLine(target.ProcedureName, 0);
            selectedLine = line;
            var context = new NativeContext(workbook, projectObject, componentsObject,
                componentObject, codeObject, line, mode, Target: target);
            VerifyTarget(context);
            ExecuteCommand(context, references, execute, preparation, token);
        }
        catch (Exception error)
        {
            if (error is DebugSetupException setup)
                setup.WithNativeFailure(new(setup.NativeFailure?.Reason ?? VbeNativeFailureReason.BindingChanged,
                    "Run Sub/UserForm", null, null, target.ModuleName, selectedLine));
            failure = error;
            throw;
        }
        finally { adapter.ReleaseScope(failure, references.ToArray()); }
    }

    public void TransferBreakpoints(IReadOnlyList<VbeBreakpoint> breakpoints, CancellationToken token)
        => ProcessBreakpoints(breakpoints, execute: true, token);

    public void InspectBreakpoints(IReadOnlyList<VbeBreakpoint> breakpoints, CancellationToken token)
        => ProcessBreakpoints(breakpoints, execute: false, token);

    private void ProcessBreakpoints(IReadOnlyList<VbeBreakpoint> breakpoints, bool execute,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (breakpoints.Count == 0) return;
        var references = new List<(string Resource, object? Value)>();
        Exception? failure = null;
        try
        {
            var maps = CollectMaps(breakpoints);
            var workbook = GetWorkbook(breakpoints[0]);
            dynamic book = workbook;
            object projectObject = book.VBProject;
            references.Insert(0, ("breakpoint VBA project", projectObject));
            dynamic project = projectObject;
            RequireDesignMode(project, breakpoints[0]);
            object componentsObject = project.VBComponents;
            references.Insert(0, ("breakpoint VBA components", componentsObject));
            dynamic components = componentsObject;
            var modules = new Dictionary<string, (object Component, object Code)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var breakpoint in breakpoints)
            {
                token.ThrowIfCancellationRequested();
                if (modules.ContainsKey(breakpoint.ModuleName)) continue;
                var map = maps[breakpoint.ModuleName];
                object componentObject = components.Item(map.ModuleName);
                references.Insert(0, ($"breakpoint component {map.ModuleName}", componentObject));
                dynamic component = componentObject;
                object codeObject = component.CodeModule;
                references.Insert(0, ($"breakpoint code module {map.ModuleName}", codeObject));
                VerifyContents(componentObject, codeObject, map, breakpoint);
                modules.Add(map.ModuleName, (componentObject, codeObject));
            }
            // Every map has passed before any toggle. Preserve the request order.
            foreach (var breakpoint in breakpoints)
            {
                token.ThrowIfCancellationRequested();
                var module = modules[breakpoint.ModuleName];
                ExecuteCommand(new NativeContext(workbook, projectObject, componentsObject,
                    module.Component, module.Code, breakpoint.VbideLine, 2, Breakpoint: breakpoint),
                    references, execute, preparation: null, token);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally { adapter.ReleaseScope(failure, references.ToArray()); }
    }

    private static Dictionary<string, VbeCodeModuleSourceMap> CollectMaps(
        IReadOnlyList<VbeBreakpoint> breakpoints)
    {
        var maps = new Dictionary<string, VbeCodeModuleSourceMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var breakpoint in breakpoints)
        {
            var map = breakpoint.SourceMap;
            if (string.IsNullOrEmpty(map.ModuleName) || !VbaIdentifier.IsIdentifier(map.ModuleName)
                || map.ModuleName.EnumerateRunes().Take(32).Count() > 31)
                throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                    "The native VBE breakpoint source map has no valid module identity.");
            if (map.CodeLines.IsDefault)
                throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                    "The breakpoint source map has no code-module contents.");
            if (breakpoint.VbideLine <= 0 || breakpoint.VbideLine > map.CodeLines.Length)
                throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                    "The native VBE breakpoint line is outside the exact code-module source map.");
            if (map.ModuleKind is not (VbaModuleKind.StandardModule or VbaModuleKind.ClassModule
                or VbaModuleKind.FormModule))
                throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                    $"The breakpoint source map has unsupported module kind '{map.ModuleKind}'.");
            if (maps.TryGetValue(map.ModuleName, out var existing))
            {
                if (!existing.ModuleName.Equals(map.ModuleName, StringComparison.Ordinal)
                    || existing.ModuleKind != map.ModuleKind
                    || !existing.CodeLines.SequenceEqual(map.CodeLines, StringComparer.Ordinal))
                    throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                        $"Conflicting whole code module source maps were supplied for '{map.ModuleName}'.");
            }
            else maps.Add(map.ModuleName, map);
        }
        return maps;
    }

    internal static void VerifyContents(object componentObject, object codeObject,
        VbeCodeModuleSourceMap map, VbeBreakpoint? breakpoint = null)
    {
        dynamic component = componentObject;
        dynamic code = codeObject;
        if (!string.Equals((string)component.Name, map.ModuleName, StringComparison.Ordinal))
            throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                $"The workbook component identity does not exactly match breakpoint module '{map.ModuleName}'.");
        var expectedType = map.ModuleKind switch
        {
            VbaModuleKind.StandardModule => 1,
            VbaModuleKind.ClassModule => 2,
            VbaModuleKind.FormModule => 3,
            _ => throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                $"The source map has unsupported component kind '{map.ModuleKind}'.")
        };
        if ((int)component.Type != expectedType)
            throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                $"The workbook component kind for '{map.ModuleName}' does not exactly match its source map.");
        var count = (int)code.CountOfLines;
        if (count != map.CodeLines.Length)
            throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                $"The whole code module '{map.ModuleName}' does not exactly match its source map (line count {count}, expected {map.CodeLines.Length}).");
        for (var line = 1; line <= count; line++)
            if (!string.Equals((string)code.Lines(line, 1), map.CodeLines[line - 1], StringComparison.Ordinal))
                throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                    $"The whole code module '{map.ModuleName}' does not exactly match its source map at line {line}.");
    }

    private object GetWorkbook(VbeBreakpoint? breakpoint, object? expected = null)
    {
        object workbook;
        try { workbook = adapter.GetCurrentWorkbook(); }
        catch (Exception error) when (error is DebugSetupException or COMException or RuntimeBinderException)
        {
            throw Failure(VbeNativeFailureReason.BindingChanged, breakpoint,
                "The exact workbook binding is no longer current.", error);
        }
        if (expected is not null && !ReferenceEquals(workbook, expected))
            throw Failure(VbeNativeFailureReason.BindingChanged, breakpoint,
                "The exact workbook binding changed before native execution.");
        return workbook;
    }

    private static void RequireDesignMode(dynamic project, VbeBreakpoint breakpoint)
    {
        if ((int)project.Mode != 2)
            throw Failure(VbeNativeFailureReason.BindingChanged, breakpoint,
                "The exact workbook VBA project is not in design mode.");
    }

    private void ExecuteCommand(NativeContext context,
        List<(string Resource, object? Value)> moduleReferences, bool execute,
        IVbeNativeRunPreparation? preparation, CancellationToken token)
    {
        var references = new List<(string Resource, object? Value)>();
        Exception? failure = null;
        try
        {
            GetWorkbook(context.Breakpoint, context.Workbook);
            adapter.AllowForeground();
            dynamic component = context.Component;
            component.Activate();
            dynamic code = context.Code;
            object paneObject = code.CodePane;
            references.Insert(0, ("native code pane", paneObject));
            dynamic pane = paneObject;
            dynamic excel = adapter.Application;
            object vbeObject = excel.VBE;
            references.Insert(0, ("native VBE", vbeObject));
            dynamic vbe = vbeObject;
            object windowObject = vbe.MainWindow;
            references.Insert(0, ("VBE main window", windowObject));
            dynamic window = windowObject;
            window.Visible = true;
            pane.Show();
            vbe.ActiveCodePane = paneObject;
            pane.SetSelection(context.Line, 1, context.Line, 1);
            window.SetFocus();
            object codeWindowObject = pane.Window;
            references.Insert(0, ("VBE code window", codeWindowObject));
            dynamic codeWindow = codeWindowObject;
            codeWindow.SetFocus();
            adapter.BringVbeToForeground(new nint(Convert.ToInt64(window.HWnd)));
            CheckPane(vbeObject, paneObject, context, references);
            object barsObject = vbe.CommandBars;
            references.Insert(0, ("VBE command bars", barsObject));
            dynamic bars = barsObject;
            object? commandObject = bars.FindControl(1, context.CommandId, Type.Missing, false);
            if (commandObject is null)
                throw Failure(VbeNativeFailureReason.CommandMissing, context.Breakpoint,
                    $"The native VBE {context.CommandName} command (ID {context.CommandId}) was not found.",
                    context: context);
            references.Insert(0, ($"native {context.CommandName} command", commandObject));
            CheckCommand(commandObject, context);
            RevalidateModule(context, moduleReferences);
            token.ThrowIfCancellationRequested();
            if (preparation is not null)
            {
                preparation.Begin();
                preparation.VerifyObservation();
                RevalidateModule(context, moduleReferences);
            }
            dynamic project = context.Project;
            if ((int)project.Mode != context.Mode)
                throw Failure(VbeNativeFailureReason.BindingChanged, context.Breakpoint,
                    $"The exact project mode changed in the {context.ContextName} context.", context: context);
            CheckPane(vbeObject, paneObject, context, references);
            CheckCommand(commandObject, context);
            preparation?.VerifyObservation();
            GetWorkbook(context.Breakpoint, context.Workbook);
            token.ThrowIfCancellationRequested();
            if (execute)
            {
                dynamic command = commandObject;
                try { command.Execute(); }
                catch (Exception error) when (error is COMException or RuntimeBinderException
                    or InvalidCastException or ArgumentException or System.Reflection.TargetParameterCountException)
                {
                    throw Failure(VbeNativeFailureReason.InvocationFailed, context.Breakpoint,
                        $"The native VBE {context.CommandName} command (ID {context.CommandId}) was found and enabled, but its invocation did not complete. Resolve any visible VBE dialog and retry.",
                        error, context);
                }
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally { adapter.ReleaseScope(failure, references.ToArray()); }
    }

    private static void RevalidateModule(NativeContext context,
        List<(string Resource, object? Value)> references)
    {
        dynamic components = context.Components;
        var name = context.Breakpoint?.ModuleName ?? context.Target!.ModuleName;
        object currentComponent = components.Item(name);
        references.Insert(0, ("current native component", currentComponent));
        dynamic current = currentComponent;
        object currentCode = current.CodeModule;
        references.Insert(0, ("current native code module", currentCode));
        if (!ReferenceEquals(currentComponent, context.Component) || !ReferenceEquals(currentCode, context.Code))
            throw Failure(VbeNativeFailureReason.SourceMapMismatch, context.Breakpoint,
                "The exact component or code module was replaced.", context: context);
        if (context.Breakpoint is { } breakpoint)
            VerifyContents(currentComponent, currentCode, breakpoint.SourceMap, breakpoint);
        else VerifyTarget(context);
    }

    private static void VerifyTarget(NativeContext context)
    {
        dynamic component = context.Component;
        dynamic code = context.Code;
        var target = context.Target!;
        if ((int)component.Type != 1)
            throw Failure(VbeNativeFailureReason.SourceMapMismatch, null,
                $"The debug target module '{target.ModuleName}' is not a standard module.", context: context);
        if (!string.Equals((string)component.Name, target.ModuleName, StringComparison.Ordinal))
            throw Failure(VbeNativeFailureReason.SourceMapMismatch, null,
                "The exact target component identity changed.", context: context);
        if (context.Line <= 0 || context.Line > (int)code.CountOfLines
            || (int)code.ProcBodyLine(target.ProcedureName, 0) != context.Line)
            throw Failure(VbeNativeFailureReason.SourceMapMismatch, null,
                $"The debug target procedure '{target.ModuleName}.{target.ProcedureName}' could not be resolved at the exact selected line.",
                context: context);
    }

    private static void CheckPane(object vbeObject, object paneObject, NativeContext context,
        List<(string Resource, object? Value)> references)
    {
        dynamic vbe = vbeObject;
        object active = vbe.ActiveCodePane;
        references.Insert(0, ("active native code pane", active));
        if (!ReferenceEquals(active, paneObject))
            throw Failure(VbeNativeFailureReason.WrongPane, context.Breakpoint,
                $"The intended VBE code pane is not active in the {context.ContextName} context.", context: context);
        dynamic pane = paneObject;
        int startLine = 0, startColumn = 0, endLine = 0, endColumn = 0;
        pane.GetSelection(ref startLine, ref startColumn, ref endLine, ref endColumn);
        if (startLine != context.Line || endLine != context.Line || startColumn != 1 || endColumn != 1)
            throw Failure(VbeNativeFailureReason.WrongSelection, context.Breakpoint,
                $"The exact VBE line selection was not retained in the {context.ContextName} context.", context: context);
    }

    private static void CheckCommand(object commandObject, NativeContext context)
    {
        dynamic command = commandObject;
        if ((int)command.Id != context.CommandId || !(bool)command.BuiltIn)
            throw Failure(VbeNativeFailureReason.WrongCommand, context.Breakpoint,
                $"The resolved VBE command is not the built-in {context.CommandName} command (ID {context.CommandId}).",
                context: context);
        if (!(bool)command.Enabled)
            throw Failure(VbeNativeFailureReason.CommandDisabled, context.Breakpoint,
                context.Breakpoint is not null
                    ? "The native VBE Toggle Breakpoint command (ID 51) is disabled in the actual workbook compilation context, so the mapped line is inactive or non-executable. The breakpoint was not relocated."
                    : $"The native VBE {context.CommandName} command (ID {context.CommandId}) is disabled in the {context.ContextName} context.",
                context: context);
    }

    private static DebugSetupException Failure(VbeNativeFailureReason reason,
        VbeBreakpoint? breakpoint, string message, Exception? cause = null, NativeContext? context = null)
    {
        var prefix = breakpoint is null
            ? context?.Target is { } target ? $"Invalid target '{target.ModuleName}.{target.ProcedureName}' at VBE line {context.Line}: " : ""
            :
            $"Invalid breakpoint at '{breakpoint.Source.SourceUri}:{breakpoint.Source.EditorLine + 1}' mapped to '{breakpoint.ModuleName}:{breakpoint.VbideLine}': ";
        var failure = cause is null ? new DebugSetupException(prefix + message)
            : new DebugSetupException(prefix + message, cause);
        return failure.WithNativeFailure(new(reason, context?.CommandName ?? (breakpoint is null ? "Verify source map" : "Toggle Breakpoint"),
            breakpoint?.Source.SourceUri, breakpoint?.Source.EditorLine,
            breakpoint?.ModuleName ?? context?.Target?.ModuleName, breakpoint?.VbideLine ?? context?.Line));
    }
}
