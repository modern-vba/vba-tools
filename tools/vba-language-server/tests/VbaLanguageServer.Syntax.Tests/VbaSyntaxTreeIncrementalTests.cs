using System.Diagnostics;
using VbaLanguageServer.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaSyntaxTreeIncrementalTests
{
    private readonly ITestOutputHelper output;

    public VbaSyntaxTreeIncrementalTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public void ParserReadsSafeCallableBodyFromDirectSourceWindow()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var original = string.Join('\n', [
            "Attribute VB_Name = \"ActualWorker\"",
            "Option Explicit",
            "",
            "'* Builds a value.",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var updated = original.Replace("\"old\"", "\"new\"", StringComparison.Ordinal);
        var previous = VbaSyntaxTree.ParseModule(uri, original);
        var expectedWindowStart = updated.IndexOf("'* Builds", StringComparison.Ordinal);
        var expectedWindowEnd = updated.IndexOf("End Function", StringComparison.Ordinal)
            + "End Function".Length;
        var expectedMemberStart = updated.IndexOf("Public Function", StringComparison.Ordinal);

        var result = VbaSyntaxTree.ParseOrUpdate(
            uri,
            updated,
            previous,
            out var observation);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
        Assert.Equal(
            VbaIncrementalParseRoute.ModuleMemberSourceWindow,
            observation.Route);
        Assert.Equal(VbaIncrementalParseFallbackReason.None, observation.FallbackReason);
        Assert.Equal(updated.Length, observation.DocumentUtf16Length);
        Assert.Equal(expectedWindowEnd - expectedWindowStart, observation.ParseWindowUtf16Length);
        Assert.Equal(expectedWindowStart, observation.WindowStartOffset);
        Assert.Equal(3, observation.WindowStartLine);
        Assert.Equal(expectedWindowEnd - expectedMemberStart, observation.MemberUtf16Length);
        Assert.Equal(VbaModuleKind.StandardModule, observation.ModuleKind);
        Assert.Equal("ActualWorker", observation.ModuleIdentity);
        Assert.True(observation.ParseWindowUtf16Length < observation.DocumentUtf16Length);
    }

    [Fact]
    public void ParserReportsModuleMemberUpdateForSafeCallableBodyEdit()
    {
        var original = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var updated = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"new\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var previous = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", original);

        var result = VbaSyntaxTree.ParseOrUpdate("file:///C:/work/Worker.bas", updated, previous);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
        Assert.NotNull(result.MemberUpdate);
        Assert.Equal("BuildValue", result.MemberUpdate.PreviousMember.Name);
        Assert.Equal("BuildValue", result.MemberUpdate.CurrentMember.Name);
        Assert.Equal(2, result.MemberUpdate.PreviousStartLine);
        Assert.Equal(2, result.MemberUpdate.CurrentStartLine);
        Assert.Contains(result.SyntaxTree.Module.CallableDeclarations, declaration => declaration.Name == "BuildValue");
        Assert.Contains(result.SyntaxTree.Module.CallableDeclarations, declaration => declaration.Name == "Run");
        Assert.Same(
            previous.Module.Members.Single(member => member.Name == "Run"),
            result.SyntaxTree.Module.Members.Single(member => member.Name == "Run"));
    }

    [Fact]
    public void ParserProjectsSuffixRangesAfterMemberGrows()
    {
        var original = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var updated = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    Dim value As String",
            "    value = \"new and longer\"",
            "    BuildValue = value",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var previous = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", original);

        var result = VbaSyntaxTree.ParseOrUpdate("file:///C:/work/Worker.bas", updated, previous);
        var fullParse = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", updated);
        var incrementalRun = result.SyntaxTree.Module.CallableDeclarations.Single(member => member.Name == "Run");
        var fullRun = fullParse.Module.CallableDeclarations.Single(member => member.Name == "Run");

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
        Assert.Equal(fullRun.Range, incrementalRun.Range);
        Assert.Equal(fullRun.BlockRange, incrementalRun.BlockRange);
        Assert.Equal(
            fullParse.TokenStream.Tokens.Select(token => (token.Kind, token.Text, token.Range)),
            result.SyntaxTree.TokenStream.Tokens.Select(token => (token.Kind, token.Text, token.Range)));
    }

    [Fact]
    public void ParserKeepsShiftedSuffixSyntaxInLazySegmentsAfterMemberGrows()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var original = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var updated = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    Dim value As String",
            "    value = \"new\"",
            "    BuildValue = value",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var previous = VbaSyntaxTree.ParseModule(uri, original);

        var result = VbaSyntaxTree.ParseOrUpdate(uri, updated, previous);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.TokenStream.Tokens);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Members);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Declarations);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.CallableDeclarations);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Statements);
        AssertSegmentedWithoutEagerProjection(result.SyntaxTree.Module.Expressions);
        AssertSegmentedWithoutEagerProjection(result.SyntaxTree.Module.ArgumentLists);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Blocks);
    }

    [Theory]
    [InlineData("\n", "\n")]
    [InlineData("\r\n", "\r\n")]
    [InlineData("\r", "\r")]
    [InlineData("\r\n", "\n")]
    public void IncrementalAndFullParsingPreserveTheSameCoordinates(
        string firstNewLine,
        string secondNewLine)
    {
        const string uri = "file:///C:/work/Worker.bas";
        var original = JoinWithAlternatingNewlines(
            firstNewLine,
            secondNewLine,
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    Example Arg1:=\"以前\"",
            "End Sub");
        var updated = JoinWithAlternatingNewlines(
            firstNewLine,
            secondNewLine,
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    Dim value As String",
            "    value = \"新しい😀\"",
            "    BuildValue = value",
            "End Function",
            "",
            "Public Sub Run()",
            "    Example Arg1:=\"以前\"",
            "End Sub");
        var previous = VbaSyntaxTree.ParseModule(uri, original);

        var incremental = VbaSyntaxTree.ParseOrUpdate(uri, updated, previous);
        var full = VbaSyntaxTree.ParseModule(uri, updated);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, incremental.UpdateKind);
        Assert.Equal(full.Module.Range, incremental.SyntaxTree.Module.Range);
        Assert.Equal(
            full.TokenStream.Tokens.Select(token => (token.Kind, token.Text, token.Range)),
            incremental.SyntaxTree.TokenStream.Tokens.Select(token => (token.Kind, token.Text, token.Range)));
        Assert.Equal(
            full.Module.Declarations.Select(declaration => (declaration.Name, declaration.Range)),
            incremental.SyntaxTree.Module.Declarations.Select(declaration => (declaration.Name, declaration.Range)));
        Assert.Equal(
            full.Module.Statements.Select(statement => (statement.Kind, statement.Range)),
            incremental.SyntaxTree.Module.Statements.Select(statement => (statement.Kind, statement.Range)));
        Assert.Equal(
            full.Module.ArgumentLists.Select(arguments => (arguments.Callee, arguments.Range)),
            incremental.SyntaxTree.Module.ArgumentLists.Select(arguments => (arguments.Callee, arguments.Range)));
    }

    [Fact]
    public void ParserFallsBackWhenAnUnchangedSuffixLineEndingWidthChanges()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string original =
            "Attribute VB_Name = \"Worker\"\r\n" +
            "Public Function BuildValue() As String\r\n" +
            "    BuildValue = \"old\"\r\n" +
            "End Function\r\n" +
            "\r\n" +
            "Public Sub Run()\r\n" +
            "    BuildValue\r\n" +
            "End Sub";
        const string updated =
            "Attribute VB_Name = \"Worker\"\r\n" +
            "Public Function BuildValue() As String\r\n" +
            "    BuildValue = \"new\"\r\n" +
            "End Function\r\n" +
            "\n" +
            "Public Sub Run()\r\n" +
            "    BuildValue\r\n" +
            "End Sub";
        var previous = VbaSyntaxTree.ParseModule(uri, original);

        var result = VbaSyntaxTree.ParseOrUpdate(uri, updated, previous);
        var full = VbaSyntaxTree.ParseModule(uri, updated);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.FullModule, result.UpdateKind);
        Assert.Equal(
            full.TokenStream.Tokens.Select(token => (token.Kind, token.Text, token.Range)),
            result.SyntaxTree.TokenStream.Tokens.Select(token => (token.Kind, token.Text, token.Range)));
        Assert.Equal(
            full.Module.Declarations.Select(declaration => (declaration.Name, declaration.Range)),
            result.SyntaxTree.Module.Declarations.Select(declaration => (declaration.Name, declaration.Range)));
    }

    [Fact]
    public void ParserFallsBackToFullModuleForBoundaryAndRecoveryCases()
    {
        var original = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var boundaryChanged = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValueRenamed() As String",
            "    BuildValueRenamed = \"new\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var malformed = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"unterminated",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var previous = VbaSyntaxTree.ParseModule("file:///C:/work/Worker.bas", original);

        var boundaryResult = VbaSyntaxTree.ParseOrUpdate("file:///C:/work/Worker.bas", boundaryChanged, previous);
        var recoveryResult = VbaSyntaxTree.ParseOrUpdate("file:///C:/work/Worker.bas", malformed, previous);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.FullModule, boundaryResult.UpdateKind);
        Assert.Null(boundaryResult.MemberUpdate);
        Assert.Equal(VbaSyntaxTreeParseUpdateKind.FullModule, recoveryResult.UpdateKind);
        Assert.Null(recoveryResult.MemberUpdate);
        Assert.Contains(recoveryResult.SyntaxTree.Diagnostics, diagnostic => diagnostic.Code == "syntax.unterminatedStringLiteral");
    }

    [Fact]
    public void IncrementalAndFullParsingMatchForNestedBlocksLabelsArgumentsAndPreprocessor()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var original = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "Public Function BuildValue(ByVal inputValue As Long, Optional ByVal flag As Boolean = False) As String",
            "StartHere:",
            "#If VBA7 Then",
            "    If flag Then",
            "        BuildValue = CStr(inputValue)",
            "    Else",
            "        BuildValue = Format$(inputValue, \"0\")",
            "    End If",
            "#End If",
            "End Function",
            "",
            "Public Sub Run()",
            "    Debug.Print BuildValue(1, flag:=True)",
            "End Sub"
        ]);
        var updated = original.Replace(
            "        BuildValue = Format$(inputValue, \"0\")",
            "        BuildValue = Format$(inputValue + 1, \"0\")",
            StringComparison.Ordinal);
        var previous = VbaSyntaxTree.ParseModule(uri, original);

        var incremental = VbaSyntaxTree.ParseOrUpdate(uri, updated, previous, out var observation);
        var full = VbaSyntaxTree.ParseModule(uri, updated);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, incremental.UpdateKind);
        Assert.Equal(VbaIncrementalParseRoute.ModuleMemberSourceWindow, observation.Route);
        AssertSyntaxEquivalent(full, incremental.SyntaxTree);
    }

    [Fact]
    public void ParserFallsBackWhenContinuedHeaderLineChanges()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var original = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue( _",
            "    ByVal inputValue As Long) As String",
            "    BuildValue = CStr(inputValue)",
            "End Function"
        ]);
        var updated = original.Replace(
            "    ByVal inputValue As Long) As String",
            "    ByVal changedValue As Long) As String",
            StringComparison.Ordinal);
        var previous = VbaSyntaxTree.ParseModule(uri, original);

        var result = VbaSyntaxTree.ParseOrUpdate(uri, updated, previous, out var observation);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.FullModule, result.UpdateKind);
        Assert.Equal(VbaIncrementalParseFallbackReason.MemberBoundaryTouched, observation.FallbackReason);
    }

    [Fact]
    public void RandomizedSafeCallableBodyEditsMatchFullParsing()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "",
            "'* Computes a value.",
            "Public Function BuildValue(ByVal inputValue As Long, Optional ByVal flag As Boolean = False) As String",
            "    Dim value As Long",
            "    value = inputValue",
            "    If flag Then",
            "        value = value + 1",
            "    End If",
            "    BuildValue = CStr(value)",
            "End Function",
            "",
            "Public Sub Tail()",
            "    Debug.Print BuildValue(1, flag:=True)",
            "End Sub"
        ]);
        var random = new Random(211);
        var previous = VbaSyntaxTree.ParseModule(uri, source);
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var replacement = iteration % 3 == 0
                ? $"    value = inputValue + {random.Next(1, 10)}"
                : iteration % 3 == 1
                    ? $"    value = inputValue + {random.Next(10, 100)} + {random.Next(1, 10)}"
                    : "    value = inputValue";
            var next = ReplaceLine(source, 6, replacement);

            var incremental = VbaSyntaxTree.ParseOrUpdate(uri, next, previous, out var observation);
            var full = VbaSyntaxTree.ParseModule(uri, next);

            Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, incremental.UpdateKind);
            Assert.Equal(VbaIncrementalParseRoute.ModuleMemberSourceWindow, observation.Route);
            AssertSyntaxEquivalent(full, incremental.SyntaxTree);
            source = next;
            previous = incremental.SyntaxTree;
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void ModuleMemberSourceWindowBenchmarkReportsLatencyAllocationAndWindowSize()
    {
        const string uri = "file:///C:/work/IncrementalBenchmark.bas";
        var first = CreateBenchmarkFixture(1);
        var second = CreateBenchmarkFixture(2);
        var previous = VbaSyntaxTree.ParseModule(uri, first);
        const int warmups = 10;
        const int measurements = 50;

        for (var index = 0; index < warmups; index++)
        {
            var source = index % 2 == 0 ? second : first;
            var result = VbaSyntaxTree.ParseOrUpdate(uri, source, previous, out var observation);
            Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
            Assert.Equal(VbaIncrementalParseRoute.ModuleMemberSourceWindow, observation.Route);
            previous = result.SyntaxTree;
        }

        var elapsed = new TimeSpan[measurements];
        var projectionElapsed = new TimeSpan[measurements];
        var allocations = new long[measurements];
        var projectionChecksum = 0L;
        VbaIncrementalParseObservation lastObservation = default;
        for (var index = 0; index < measurements; index++)
        {
            var source = index % 2 == 0 ? second : first;
            var beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var result = VbaSyntaxTree.ParseOrUpdate(uri, source, previous, out lastObservation);
            elapsed[index] = Stopwatch.GetElapsedTime(started);
            allocations[index] = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
            Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, result.UpdateKind);
            Assert.Equal(VbaIncrementalParseRoute.ModuleMemberSourceWindow, lastObservation.Route);
            Assert.Equal(VbaIncrementalParseFallbackReason.None, lastObservation.FallbackReason);
            Assert.True(lastObservation.ParseWindowUtf16Length < lastObservation.DocumentUtf16Length / 20);
            var projectionStarted = Stopwatch.GetTimestamp();
            foreach (var token in result.SyntaxTree.TokenStream.Tokens)
            {
                projectionChecksum += token.Range.End.Offset;
            }

            projectionElapsed[index] = Stopwatch.GetElapsedTime(projectionStarted);
            previous = result.SyntaxTree;
        }

        output.WriteLine(
            "module member source window parse+segment p50={0:F3} ms, p95={1:F3} ms, absolute projection p95={2:F3} ms, allocation p95={3} bytes, document={4}, window={5}, member={6}, fallback={7}, projectionChecksum={8}",
            Percentile(elapsed, 0.50).TotalMilliseconds,
            Percentile(elapsed, 0.95).TotalMilliseconds,
            Percentile(projectionElapsed, 0.95).TotalMilliseconds,
            Percentile(allocations, 0.95),
            lastObservation.DocumentUtf16Length,
            lastObservation.ParseWindowUtf16Length,
            lastObservation.MemberUtf16Length,
            lastObservation.FallbackReason,
            projectionChecksum);
    }

    private static void AssertSyntaxEquivalent(VbaSyntaxTree expected, VbaSyntaxTree actual)
    {
        Assert.Equal(
            expected.TokenStream.Tokens.Select(token => (token.Kind, token.Text, token.Range)),
            actual.TokenStream.Tokens.Select(token => (token.Kind, token.Text, token.Range)));
        Assert.Equal(
            expected.Module.Members.Select(member => (member.Name, member.Kind, member.BlockRange, member.IsExternal, member.IsStatic)),
            actual.Module.Members.Select(member => (member.Name, member.Kind, member.BlockRange, member.IsExternal, member.IsStatic)));
        Assert.Equal(
            expected.Module.Declarations.Select(declaration => (
                declaration.Name,
                declaration.Kind,
                declaration.Visibility,
                declaration.Range,
                declaration.LineIndex,
                declaration.ParentProcedureName,
                declaration.ParentProcedureRange,
                declaration.ParentTypeName,
                declaration.TypeReference,
                declaration.IsExternal,
                declaration.IsStatic,
                declaration.DeclarationLabel)),
            actual.Module.Declarations.Select(declaration => (
                declaration.Name,
                declaration.Kind,
                declaration.Visibility,
                declaration.Range,
                declaration.LineIndex,
                declaration.ParentProcedureName,
                declaration.ParentProcedureRange,
                declaration.ParentTypeName,
                declaration.TypeReference,
                declaration.IsExternal,
                declaration.IsStatic,
                declaration.DeclarationLabel)));
        Assert.Equal(
            expected.Module.CallableDeclarations.Select(declaration => (
                declaration.Name,
                declaration.Kind,
                declaration.Visibility,
                declaration.Range,
                declaration.BlockRange,
                declaration.LineIndex,
                declaration.Signature.Label)),
            actual.Module.CallableDeclarations.Select(declaration => (
                declaration.Name,
                declaration.Kind,
                declaration.Visibility,
                declaration.Range,
                declaration.BlockRange,
                declaration.LineIndex,
                declaration.Signature.Label)));
        foreach (var (expectedDeclaration, actualDeclaration) in expected.Module.CallableDeclarations.Zip(actual.Module.CallableDeclarations))
        {
            Assert.Equal(
                expectedDeclaration.Parameters.Select(parameter => (
                    parameter.Name,
                    parameter.Range,
                    parameter.TypeReference,
                    parameter.IsOptional,
                    parameter.IsByRef)),
                actualDeclaration.Parameters.Select(parameter => (
                    parameter.Name,
                    parameter.Range,
                    parameter.TypeReference,
                    parameter.IsOptional,
                    parameter.IsByRef)));
        }

        Assert.Equal(
            expected.Module.Statements.Select(statement => (statement.Kind, statement.Text, statement.Range, statement.IsMalformed)),
            actual.Module.Statements.Select(statement => (statement.Kind, statement.Text, statement.Range, statement.IsMalformed)));
        Assert.Equal(
            expected.Module.Expressions.Select(expression => (expression.Kind, expression.Text, expression.Range, expression.IsContinued)),
            actual.Module.Expressions.Select(expression => (expression.Kind, expression.Text, expression.Range, expression.IsContinued)));
        Assert.Equal(
            expected.Module.ArgumentLists.Select(argumentList => (
                argumentList.Callee,
                argumentList.Range,
                argumentList.IsContinued)),
            actual.Module.ArgumentLists.Select(argumentList => (
                argumentList.Callee,
                argumentList.Range,
                argumentList.IsContinued)));
        foreach (var (expectedArgumentList, actualArgumentList) in expected.Module.ArgumentLists.Zip(actual.Module.ArgumentLists))
        {
            Assert.Equal(
                expectedArgumentList.Arguments.Select(argument => (
                    argument.Kind,
                    argument.Text,
                    argument.Range,
                    argument.Name,
                    argument.NameRange,
                    argument.ValueText,
                    argument.ValueRange)),
                actualArgumentList.Arguments.Select(argument => (
                    argument.Kind,
                    argument.Text,
                    argument.Range,
                    argument.Name,
                    argument.NameRange,
                    argument.ValueText,
                    argument.ValueRange)));
        }

        Assert.Equal(
            expected.Module.Blocks.Select(block => (
                block.Kind,
                block.OpenerRange,
                block.CloserRange,
                block.ExpectedTerminator,
                block.Range,
                block.IsMalformedBarrier,
                block.MalformedBarrierOwnerRange)),
            actual.Module.Blocks.Select(block => (
                block.Kind,
                block.OpenerRange,
                block.CloserRange,
                block.ExpectedTerminator,
                block.Range,
                block.IsMalformedBarrier,
                block.MalformedBarrierOwnerRange)));
        foreach (var (expectedBlock, actualBlock) in expected.Module.Blocks.Zip(actual.Module.Blocks))
        {
            Assert.Equal(
                expectedBlock.Branches.Select(branch => (branch.Kind, branch.HeaderRange, branch.Range)),
                actualBlock.Branches.Select(branch => (branch.Kind, branch.HeaderRange, branch.Range)));
        }

        Assert.Equal(
            expected.Module.LineLabels.Select(label => (label.Name, label.IsNumeric, label.Range, label.ProcedureName, label.ProcedureRange)),
            actual.Module.LineLabels.Select(label => (label.Name, label.IsNumeric, label.Range, label.ProcedureName, label.ProcedureRange)));
        Assert.Equal(
            expected.Module.PreprocessorDirectives.Select(directive => (directive.Kind, directive.Text, directive.Range)),
            actual.Module.PreprocessorDirectives.Select(directive => (directive.Kind, directive.Text, directive.Range)));
        Assert.Equal(
            expected.Module.PreprocessorBlocks.Select(block => (
                block.IfDirective,
                block.EndDirective,
                block.Range)),
            actual.Module.PreprocessorBlocks.Select(block => (
                block.IfDirective,
                block.EndDirective,
                block.Range)));
        foreach (var (expectedBlock, actualBlock) in expected.Module.PreprocessorBlocks.Zip(actual.Module.PreprocessorBlocks))
        {
            Assert.Equal(
                expectedBlock.Branches.Select(branch => (branch.Directive, branch.Range)),
                actualBlock.Branches.Select(branch => (branch.Directive, branch.Range)));
        }

        Assert.Equal(
            expected.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Range)),
            actual.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Range)));
    }

    private static void AssertSegmentedWithLazySuffix<T>(IReadOnlyList<T> items)
    {
        var segmented = AssertSegmentedWithoutEagerProjection(items);
        Assert.True(segmented.LazyProjectedItemCount > 0);
    }

    private static IVbaSegmentedSyntaxList AssertSegmentedWithoutEagerProjection<T>(IReadOnlyList<T> items)
    {
        var segmented = Assert.IsAssignableFrom<IVbaSegmentedSyntaxList>(items);
        Assert.True(segmented.SegmentCount >= 0);
        Assert.Equal(0, segmented.EagerProjectedItemCount);
        return segmented;
    }

    private static string ReplaceLine(string source, int lineIndex, string replacement)
    {
        var lines = source.Split('\n');
        lines[lineIndex] = replacement;
        return string.Join('\n', lines);
    }

    private static string CreateBenchmarkFixture(int editedValue)
    {
        var lines = new List<string>(2500)
        {
            "Attribute VB_Name = \"IncrementalBenchmark\"",
            "Option Explicit"
        };
        for (var memberIndex = 0; memberIndex < 300; memberIndex++)
        {
            var value = memberIndex == 149 ? editedValue : 1;
            lines.Add($"Public Sub Routine{memberIndex:D3}()");
            lines.Add("    Dim value As Long");
            lines.Add($"    value = {memberIndex}");
            lines.Add($"    value = value + {value}");
            lines.Add("End Sub");
        }

        return string.Join('\n', lines);
    }

    private static TimeSpan Percentile(IEnumerable<TimeSpan> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        var index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
        return ordered[Math.Max(0, index)];
    }

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        var index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
        return ordered[Math.Max(0, index)];
    }

    private static string JoinWithAlternatingNewlines(
        string firstNewLine,
        string secondNewLine,
        params string[] lines)
    {
        var source = new System.Text.StringBuilder();
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                source.Append(index % 2 == 0 ? secondNewLine : firstNewLine);
            }

            source.Append(lines[index]);
        }

        return source.ToString();
    }
}
