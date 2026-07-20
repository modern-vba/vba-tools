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
    public void ExactUriAndTextReuseTheSameTree()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string source = "Public Sub Run()\nEnd Sub";
        var previous = VbaSyntaxTree.ParseModule(uri, source);

        var changeSet = VbaSyntaxTree.ParseOrUpdate(uri, source, previous);

        Assert.IsType<VbaSyntaxTreeChangeSet.Unchanged>(changeSet);
        Assert.Same(previous, changeSet.SyntaxTree);
    }

    [Fact]
    public void UriSpellingChangeRequiresModuleRecomputation()
    {
        const string source =
            "Public Sub Run()\n"
            + "    Debug.Print \"old\"\n"
            + "End Sub";
        const string updated =
            "Public Sub Run()\n"
            + "    Debug.Print \"new\"\n"
            + "End Sub";
        var previous = VbaSyntaxTree.ParseModule(
            "file:///C:/work/Worker.bas",
            source);

        var changeSet = VbaSyntaxTree.ParseOrUpdate(
            "file:///C:/work/worker.bas",
            updated,
            previous);

        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(changeSet);
        Assert.NotSame(previous, changeSet.SyntaxTree);
        Assert.Equal(
            "file:///C:/work/worker.bas",
            changeSet.SyntaxTree.Uri);
    }

    [Fact]
    public void PubliclyConstructedTreeCannotProveExactReuse()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string source = "Public Sub Run()\nEnd Sub";
        var parsed = VbaSyntaxTree.ParseModule(uri, source);
        var publicTree = new VbaSyntaxTree(
            parsed.Uri,
            parsed.Text,
            parsed.TokenStream,
            parsed.Module,
            parsed.Diagnostics);

        var changeSet = VbaSyntaxTree.ParseOrUpdate(uri, source, publicTree);

        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(changeSet);
        Assert.NotSame(publicTree, changeSet.SyntaxTree);
        AssertSyntaxEquivalent(parsed, changeSet.SyntaxTree);
    }

    [Fact]
    public void WithModifiedTextCannotReuseStaleParserArtifacts()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string original =
            "Public Sub Run()\n"
            + "    Debug.Print \"old\"\n"
            + "End Sub";
        const string updated =
            "Public Sub Run()\n"
            + "    Debug.Print \"new\"\n"
            + "End Sub";
        var previous = VbaSyntaxTree.ParseModule(uri, original);
        var stale = previous with
        {
            Text = updated
        };
        var expected = VbaSyntaxTree.ParseModule(uri, updated);

        var changeSet = VbaSyntaxTree.ParseOrUpdate(uri, updated, stale);

        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(changeSet);
        Assert.Equal(updated, changeSet.SyntaxTree.Text);
        Assert.NotSame(stale.TokenStream, changeSet.SyntaxTree.TokenStream);
        Assert.NotSame(stale.Module, changeSet.SyntaxTree.Module);
        AssertSyntaxEquivalent(expected, changeSet.SyntaxTree);
    }

    [Fact]
    public void WithModifiedUriCannotProveExactReuse()
    {
        const string originalUri = "file:///C:/work/Worker.bas";
        const string modifiedUri = "file:///C:/work/Worker.cls";
        const string source = "Public Sub Run()\nEnd Sub";
        var previous = VbaSyntaxTree.ParseModule(originalUri, source);
        var stale = previous with
        {
            Uri = modifiedUri
        };
        var expected = VbaSyntaxTree.ParseModule(modifiedUri, source);

        var changeSet = VbaSyntaxTree.ParseOrUpdate(modifiedUri, source, stale);

        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(changeSet);
        Assert.Equal(VbaModuleKind.ClassModule, changeSet.SyntaxTree.Module.Kind);
        AssertSyntaxEquivalent(expected, changeSet.SyntaxTree);
    }

    [Fact]
    public void WithModifiedModuleCannotProveExactReuse()
    {
        const string uri = "file:///C:/work/Worker.bas";
        const string source = "Public Sub Run()\nEnd Sub";
        var previous = VbaSyntaxTree.ParseModule(uri, source);
        var stale = previous with
        {
            Module = previous.Module with
            {
                Kind = VbaModuleKind.ClassModule
            }
        };
        var expected = VbaSyntaxTree.ParseModule(uri, source);

        var changeSet = VbaSyntaxTree.ParseOrUpdate(uri, source, stale);

        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(changeSet);
        Assert.Equal(VbaModuleKind.StandardModule, changeSet.SyntaxTree.Module.Kind);
        AssertSyntaxEquivalent(expected, changeSet.SyntaxTree);
    }

    [Fact]
    public void ChangeSetVariantsHaveNoPublicConstructors()
    {
        var variants = new[]
        {
            typeof(VbaSyntaxTreeChangeSet.Unchanged),
            typeof(VbaSyntaxTreeChangeSet.Module),
            typeof(VbaSyntaxTreeChangeSet.ModuleMember)
        };

        Assert.All(variants, variant => Assert.Empty(variant.GetConstructors()));
        Assert.All(variants, variant => Assert.True(variant.IsSealed));
    }

    [Fact]
    public void PublicChangeSetSurfaceExposesOnlySemanticReuseProofs()
    {
        var assembly = typeof(VbaSyntaxTree).Assembly;
        var parseOrUpdate = Assert.Single(
            typeof(VbaSyntaxTree).GetMethods(),
            method => method.Name == nameof(VbaSyntaxTree.ParseOrUpdate));
        var variants = assembly.GetExportedTypes()
            .Where(type => type.BaseType == typeof(VbaSyntaxTreeChangeSet))
            .OrderBy(type => type.Name)
            .ToArray();

        Assert.Equal(typeof(VbaSyntaxTreeChangeSet), parseOrUpdate.ReturnType);
        Assert.Equal(
            [
                typeof(VbaSyntaxTreeChangeSet.Module),
                typeof(VbaSyntaxTreeChangeSet.ModuleMember),
                typeof(VbaSyntaxTreeChangeSet.Unchanged)
            ],
            variants);
        Assert.Equal(
            ["CurrentMember", "PreviousMember", "SyntaxTree"],
            typeof(VbaSyntaxTreeChangeSet.ModuleMember)
                .GetProperties()
                .Select(property => property.Name)
                .Order()
                .ToArray());
        Assert.Null(assembly.GetType(
            "VbaLanguageServer.Syntax.VbaSyntaxTreeParseResult"));
        Assert.Null(assembly.GetType(
            "VbaLanguageServer.Syntax.VbaSyntaxTreeParseUpdateKind"));
        Assert.Null(assembly.GetType(
            "VbaLanguageServer.Syntax.VbaModuleMemberIncrementalUpdate"));
    }

    [Fact]
    public void LanguageServerSourceHasNoLegacyIncrementalFullDocumentMaskOrEagerProjectionSelfReport()
    {
        var testDirectory = Path.GetDirectoryName(GetCurrentSourceFilePath());
        Assert.NotNull(testDirectory);
        var languageServerSourceDirectory = Path.GetFullPath(
            Path.Combine(
                testDirectory,
                "..",
                "..",
                "src"));
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     languageServerSourceDirectory,
                     "*.cs",
                     SearchOption.AllDirectories)
                 .Where(path => !IsBuildOutputPath(
                     languageServerSourceDirectory,
                     path)))
        {
            var source = File.ReadAllText(path);
            var compactSource = string.Concat(source.Where(character =>
                !char.IsWhiteSpace(character)));
            if (source.Contains("MaskUnchangedMembers", StringComparison.Ordinal))
            {
                violations.Add($"{path}: legacy MaskUnchangedMembers path");
            }

            if (compactSource.Contains(
                    "string.Create(source.Length",
                    StringComparison.Ordinal))
            {
                violations.Add($"{path}: full-length source mask allocation");
            }

            if (source.Contains(
                    "EagerProjectedItemCount",
                    StringComparison.Ordinal))
            {
                violations.Add($"{path}: eager projection self-report");
            }
        }

        Assert.Empty(violations);
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

        Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(result);
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
    public void ParserReturnsModuleMemberProofForSafeCallableBodyEdit()
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

        var memberChange = Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(result);
        Assert.Equal("BuildValue", memberChange.PreviousMember.Name);
        Assert.Equal("BuildValue", memberChange.CurrentMember.Name);
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

        Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(result);
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
        var original = CreateLineChangingProjectionFixture(expanded: false);
        var updated = CreateLineChangingProjectionFixture(expanded: true);
        var previous = VbaSyntaxTree.ParseModule(uri, original);

        var result = VbaSyntaxTree.ParseOrUpdate(
            uri,
            updated,
            previous,
            out var observation);
        var full = VbaSyntaxTree.ParseModule(uri, updated);

        Assert.Equal(VbaIncrementalParseFallbackReason.None, observation.FallbackReason);
        Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(result);
        AssertSyntaxEquivalent(full, result.SyntaxTree);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Members);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Declarations);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.CallableDeclarations);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Statements);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Expressions);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.ArgumentLists);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.Blocks);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.LineLabels);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.PreprocessorDirectives);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.Module.PreprocessorBlocks);
        AssertSegmentedWithLazySuffix(result.SyntaxTree.TokenStream.Tokens);
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

        Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(incremental);
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

        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(result);
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

        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(boundaryResult);
        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(recoveryResult);
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

        Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(incremental);
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

        Assert.IsType<VbaSyntaxTreeChangeSet.Module>(result);
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

            Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(incremental);
            Assert.Equal(VbaIncrementalParseRoute.ModuleMemberSourceWindow, observation.Route);
            AssertSyntaxEquivalent(full, incremental.SyntaxTree);
            source = next;
            previous = incremental.SyntaxTree;
        }
    }

    [Fact]
    public void RepeatedModuleMemberEditsKeepSegmentNestingBounded()
    {
        const string uri = "file:///C:/work/IncrementalDepth.bas";
        var first = CreateBenchmarkFixture(1);
        var second = CreateBenchmarkFixture(2);
        var previous = VbaSyntaxTree.ParseModule(uri, first);

        for (var iteration = 0; iteration < 201; iteration++)
        {
            var source = iteration % 2 == 0 ? second : first;
            var result = VbaSyntaxTree.ParseOrUpdate(uri, source, previous);

            Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(result);
            previous = result.SyntaxTree;
        }

        AssertSegmentNestingBounded(previous.Module.Declarations);
        AssertSegmentNestingBounded(previous.Module.Statements);
        AssertSegmentNestingBounded(previous.TokenStream.Tokens);
    }

    [Fact]
    public void RepeatedLineChangingModuleMemberEditsKeepProjectionCompositionBounded()
    {
        const string uri = "file:///C:/work/IncrementalProjectionDepth.bas";
        var compact = CreateLineChangingProjectionFixture(expanded: false);
        var expanded = CreateLineChangingProjectionFixture(expanded: true);
        var previous = VbaSyntaxTree.ParseModule(uri, compact);
        var finalSource = compact;

        for (var iteration = 0; iteration < 201; iteration++)
        {
            finalSource = iteration % 2 == 0 ? expanded : compact;
            var result = VbaSyntaxTree.ParseOrUpdate(
                uri,
                finalSource,
                previous,
                out var observation);

            Assert.Equal(
                VbaIncrementalParseFallbackReason.None,
                observation.FallbackReason);
            Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(result);
            previous = result.SyntaxTree;
        }

        AssertSyntaxEquivalent(
            VbaSyntaxTree.ParseModule(uri, finalSource),
            previous);
        AssertSegmentedWithLazySuffix(previous.Module.Members);
        AssertSegmentedWithLazySuffix(previous.Module.Declarations);
        AssertSegmentedWithLazySuffix(previous.Module.CallableDeclarations);
        AssertSegmentedWithLazySuffix(previous.Module.Statements);
        AssertSegmentedWithLazySuffix(previous.Module.Expressions);
        AssertSegmentedWithLazySuffix(previous.Module.ArgumentLists);
        AssertSegmentedWithLazySuffix(previous.Module.Blocks);
        AssertSegmentedWithLazySuffix(previous.Module.LineLabels);
        AssertSegmentedWithLazySuffix(previous.Module.PreprocessorDirectives);
        AssertSegmentedWithLazySuffix(previous.Module.PreprocessorBlocks);
        AssertSegmentedWithLazySuffix(previous.TokenStream.Tokens);
    }

    [Fact]
    public void DistinctModuleMemberEditsKeepSegmentLookupDepthLogarithmic()
    {
        const string uri = "file:///C:/work/IncrementalLookupDepth.bas";
        const int memberCount = 128;
        var source = CreateDistinctMemberEditFixture(memberCount);
        var previous = VbaSyntaxTree.ParseModule(uri, source);

        for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            var next = ReplaceLine(
                source,
                GetDistinctMemberValueLine(memberIndex),
                $"    value = {memberIndex + 2}");
            var incremental = VbaSyntaxTree.ParseOrUpdate(
                uri,
                next,
                previous,
                out var observation);
            var full = VbaSyntaxTree.ParseModule(uri, next);

            Assert.Equal(
                VbaIncrementalParseFallbackReason.None,
                observation.FallbackReason);
            Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(incremental);
            AssertSyntaxEquivalent(full, incremental.SyntaxTree);
            source = next;
            previous = incremental.SyntaxTree;
        }

        var members = Assert.IsAssignableFrom<IVbaSegmentedSyntaxList>(
            previous.Module.Members);
        Assert.True(members.SegmentCount >= memberCount);
        Assert.InRange(members.MaxLookupStepCount, 1, 8);
        Assert.True(members.MaxLookupStepCount < members.SegmentCount);
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
            Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(result);
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
            Assert.IsType<VbaSyntaxTreeChangeSet.ModuleMember>(result);
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
        var segmented = AssertSegmentedWithBoundedShape(items);
        Assert.True(segmented.LazyProjectedItemCount > 0);
        Assert.Equal(1, segmented.NestingDepth);
        Assert.Equal(1, segmented.MaxProjectionStepCount);
    }

    private static IVbaSegmentedSyntaxList AssertSegmentedWithBoundedShape<T>(
        IReadOnlyList<T> items)
    {
        var segmented = Assert.IsAssignableFrom<IVbaSegmentedSyntaxList>(items);
        Assert.InRange(segmented.SegmentCount, 1, 3);
        return segmented;
    }

    private static void AssertSegmentNestingBounded<T>(IReadOnlyList<T> items)
    {
        var segmented = Assert.IsAssignableFrom<IVbaSegmentedSyntaxList>(items);
        Assert.InRange(segmented.NestingDepth, 0, 1);
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

    private static string CreateLineChangingProjectionFixture(bool expanded)
    {
        var lines = new List<string>
        {
            "Attribute VB_Name = \"IncrementalProjectionDepth\"",
            "Option Explicit",
            "",
            "Public Sub EditTarget()",
            "    Dim value As Long",
            expanded
                ? "    value = 2"
                : "    value = 1"
        };
        if (expanded)
        {
            lines.Add("    value = value + 1");
        }

        lines.AddRange([
            "End Sub",
            "",
            "Public Function Tail(ByVal inputValue As Long) As String",
            "TailLabel:",
            "#If VBA7 Then",
            "    If inputValue > 0 Then",
            "        Tail = Format$(CStr(inputValue), \"0\")",
            "    Else",
            "        Tail = \"zero\"",
            "    End If",
            "#Else",
            "    Tail = CStr(inputValue)",
            "#End If",
            "End Function"
        ]);
        return string.Join('\n', lines);
    }

    private static string CreateDistinctMemberEditFixture(int memberCount)
    {
        var lines = new List<string>(2 + (memberCount * 5))
        {
            "Attribute VB_Name = \"IncrementalLookupDepth\"",
            "Option Explicit"
        };
        for (var memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            lines.Add($"Public Sub Routine{memberIndex:D3}()");
            lines.Add("    Dim value As Long");
            lines.Add("    value = 1");
            lines.Add("    Debug.Print value");
            lines.Add("End Sub");
        }

        return string.Join('\n', lines);
    }

    private static int GetDistinctMemberValueLine(int memberIndex)
        => 4 + (memberIndex * 5);

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

    private static bool IsBuildOutputPath(
        string sourceDirectory,
        string path)
    {
        var segments = Path.GetRelativePath(
                sourceDirectory,
                path)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
        return segments.Contains("bin", StringComparer.OrdinalIgnoreCase)
            || segments.Contains("obj", StringComparer.OrdinalIgnoreCase);
    }

    private static string GetCurrentSourceFilePath(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => path;
}
