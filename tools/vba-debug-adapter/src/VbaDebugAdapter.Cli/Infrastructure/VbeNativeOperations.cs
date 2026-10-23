using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using VbaDebugAdapter.Debugging;
using VbaTools.Syntax;

namespace VbaDebugAdapter.Infrastructure;

/// <summary>Borrowed native context; the concrete session retains all lifetime policy.</summary>
internal interface IVbeNativeOperationAdapter
{
    object Application { get; }
    object GetCurrentWorkbook();
    void AllowForeground();
    void BringVbeToForeground(nint handle);
    void ReleaseScope(Exception? failure, params (string Resource, object? Value)[] references);
}

/// <summary>Owns exact breakpoint transfer, not Excel or COM lifetime.</summary>
internal sealed class VbeNativeOperations(IVbeNativeOperationAdapter adapter)
{
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
                ExecuteToggle(workbook, projectObject, componentsObject,
                    module.Component, module.Code, breakpoint, references, execute, token);
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

    private object GetWorkbook(VbeBreakpoint breakpoint, object? expected = null)
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

    private void ExecuteToggle(object workbook, object projectObject, object componentsObject,
        object componentObject, object codeObject, VbeBreakpoint breakpoint,
        List<(string Resource, object? Value)> moduleReferences, bool execute, CancellationToken token)
    {
        var references = new List<(string Resource, object? Value)>();
        Exception? failure = null;
        try
        {
            GetWorkbook(breakpoint, workbook);
            adapter.AllowForeground();
            dynamic component = componentObject;
            component.Activate();
            dynamic code = codeObject;
            object paneObject = code.CodePane;
            references.Insert(0, ("breakpoint code pane", paneObject));
            dynamic pane = paneObject;
            dynamic excel = adapter.Application;
            object vbeObject = excel.VBE;
            references.Insert(0, ("breakpoint VBE", vbeObject));
            dynamic vbe = vbeObject;
            object windowObject = vbe.MainWindow;
            references.Insert(0, ("breakpoint VBE main window", windowObject));
            dynamic window = windowObject;
            window.Visible = true;
            pane.Show();
            vbe.ActiveCodePane = paneObject;
            pane.SetSelection(breakpoint.VbideLine, 1, breakpoint.VbideLine, 1);
            window.SetFocus();
            object codeWindowObject = pane.Window;
            references.Insert(0, ("breakpoint code window", codeWindowObject));
            dynamic codeWindow = codeWindowObject;
            codeWindow.SetFocus();
            adapter.BringVbeToForeground(new nint(Convert.ToInt64(window.HWnd)));
            CheckPane(vbeObject, paneObject, breakpoint, references);
            object barsObject = vbe.CommandBars;
            references.Insert(0, ("breakpoint command bars", barsObject));
            dynamic bars = barsObject;
            object? commandObject = bars.FindControl(1,
                VbeNativeCommandContract.ToggleBreakpointCommandId, Type.Missing, false);
            if (commandObject is null)
                throw Failure(VbeNativeFailureReason.CommandMissing, breakpoint,
                    "The native VBE Toggle Breakpoint command (ID 51) was not found.");
            references.Insert(0, ("native Toggle Breakpoint command", commandObject));
            CheckCommand(commandObject, breakpoint);

            // Reacquired aliases belong to the outer scope: a Doctor final-release
            // must not invalidate cached component/code RCWs between commands.
            dynamic components = componentsObject;
            object currentComponent = components.Item(breakpoint.ModuleName);
            moduleReferences.Insert(0, ("current breakpoint component", currentComponent));
            dynamic current = currentComponent;
            object currentCode = current.CodeModule;
            moduleReferences.Insert(0, ("current breakpoint code module", currentCode));
            if (!ReferenceEquals(currentComponent, componentObject) || !ReferenceEquals(currentCode, codeObject))
                throw Failure(VbeNativeFailureReason.SourceMapMismatch, breakpoint,
                    "The exact breakpoint component or code module was replaced.");
            VerifyContents(currentComponent, currentCode, breakpoint.SourceMap, breakpoint);
            RequireDesignMode((dynamic)projectObject, breakpoint);
            CheckPane(vbeObject, paneObject, breakpoint, references);
            CheckCommand(commandObject, breakpoint);
            GetWorkbook(breakpoint, workbook);
            token.ThrowIfCancellationRequested();
            if (execute)
            {
                dynamic command = commandObject;
                try { command.Execute(); }
                catch (Exception error) when (error is COMException or RuntimeBinderException
                    or InvalidCastException or ArgumentException or System.Reflection.TargetParameterCountException)
                {
                    throw Failure(VbeNativeFailureReason.InvocationFailed, breakpoint,
                        "The native VBE Toggle Breakpoint command (ID 51) was found and enabled, but its invocation did not complete. Resolve any visible VBE dialog and retry.", error);
                }
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally { adapter.ReleaseScope(failure, references.ToArray()); }
    }

    private static void CheckPane(object vbeObject, object paneObject, VbeBreakpoint breakpoint,
        List<(string Resource, object? Value)> references)
    {
        dynamic vbe = vbeObject;
        object active = vbe.ActiveCodePane;
        references.Insert(0, ("active breakpoint code pane", active));
        if (!ReferenceEquals(active, paneObject))
            throw Failure(VbeNativeFailureReason.WrongPane, breakpoint,
                "The intended VBE code pane is not active in the breakpoint context.");
        dynamic pane = paneObject;
        int startLine = 0, startColumn = 0, endLine = 0, endColumn = 0;
        pane.GetSelection(ref startLine, ref startColumn, ref endLine, ref endColumn);
        if (startLine != breakpoint.VbideLine || endLine != breakpoint.VbideLine
            || startColumn != 1 || endColumn != 1)
            throw Failure(VbeNativeFailureReason.WrongSelection, breakpoint,
                "The exact VBE line selection was not retained in the breakpoint context.");
    }

    private static void CheckCommand(object commandObject, VbeBreakpoint breakpoint)
    {
        dynamic command = commandObject;
        if ((int)command.Id != VbeNativeCommandContract.ToggleBreakpointCommandId || !(bool)command.BuiltIn)
            throw Failure(VbeNativeFailureReason.WrongCommand, breakpoint,
                "The resolved VBE command is not the built-in Toggle Breakpoint command (ID 51).");
        if (!(bool)command.Enabled)
            throw Failure(VbeNativeFailureReason.CommandDisabled, breakpoint,
                "The native VBE Toggle Breakpoint command (ID 51) is disabled in the actual workbook compilation context, so the mapped line is inactive or non-executable. The breakpoint was not relocated.");
    }

    private static DebugSetupException Failure(VbeNativeFailureReason reason,
        VbeBreakpoint? breakpoint, string message, Exception? cause = null)
    {
        var prefix = breakpoint is null ? "" :
            $"Invalid breakpoint at '{breakpoint.Source.SourceUri}:{breakpoint.Source.EditorLine + 1}' mapped to '{breakpoint.ModuleName}:{breakpoint.VbideLine}': ";
        var failure = cause is null ? new DebugSetupException(prefix + message)
            : new DebugSetupException(prefix + message, cause);
        return failure.WithNativeFailure(new(reason, breakpoint is null ? "Verify source map" : "Toggle Breakpoint",
            breakpoint?.Source.SourceUri, breakpoint?.Source.EditorLine,
            breakpoint?.ModuleName, breakpoint?.VbideLine));
    }
}
