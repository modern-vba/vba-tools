using VbaDev.App.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class VbeIdentifierRecasingClassifierTests
{
    [Fact]
    public void WarningRequiresAtLeastOneRecasingPair()
    {
        Assert.Throws<ArgumentException>(() =>
            new VbeIdentifierRecasingWarning("Module1", []));
    }

    [Theory]
    [InlineData("FileName", "FileName")]
    [InlineData("FileName", "FilePath")]
    [InlineData("", "Filename")]
    public void WarningRejectsPairsThatAreNotIdentifierOnlyRecasing(
        string sourceIdentifier,
        string vbeIdentifier)
    {
        Assert.Throws<ArgumentException>(() =>
            new VbeIdentifierRecasingWarning(
                "Module1",
                [new VbeIdentifierRecasingPair(sourceIdentifier, vbeIdentifier)]));
    }

    [Fact]
    public void WarningRejectsDuplicateDirectionalPairs()
    {
        var pair = new VbeIdentifierRecasingPair("FileName", "Filename");

        Assert.Throws<ArgumentException>(() =>
            new VbeIdentifierRecasingWarning("Module1", [pair, pair]));
    }

    [Fact]
    public void VerificationReportRejectsMoreThanOneWarningPerComponent()
    {
        var pair = new VbeIdentifierRecasingPair("FileName", "Filename");

        Assert.Throws<ArgumentException>(() =>
            new VbeImportVerificationReport(
            [
                new VbeIdentifierRecasingWarning("Module1", [pair]),
                new VbeIdentifierRecasingWarning("module1", [pair])
            ]));
    }

    [Fact]
    public void VerificationReportRejectsNullWarnings()
    {
        Assert.Throws<ArgumentException>(() =>
            new VbeImportVerificationReport([null!]));
    }

    [Fact]
    public void ClassifiesOneIdentifierCaseChangeAsOneDirectionalPair()
    {
        var expected = Verification(["Debug.Print FileName"]);
        var actual = Imported(["Debug.Print Filename"]);

        var classified = VbeIdentifierRecasingClassifier.TryClassify(
            expected,
            actual,
            out var pairs);

        Assert.True(classified);
        Assert.Equal(
            new VbeIdentifierRecasingPair("FileName", "Filename"),
            Assert.Single(pairs));
    }

    [Fact]
    public void DoesNotClassifyAnExactComponentAsRecasing()
    {
        var expected = Verification(["Debug.Print FileName"]);

        var classified = VbeIdentifierRecasingClassifier.TryClassify(
            expected,
            Imported(expected.CodeModuleLines),
            out var pairs);

        Assert.False(classified);
        Assert.Empty(pairs);
    }

    [Fact]
    public void RejectsChangedComponentIdentity()
    {
        var expected = Verification(["Debug.Print FileName"]);

        AssertRejected(
            expected,
            Imported(["Debug.Print Filename"], componentName: "module1"));
    }

    [Fact]
    public void RejectsChangedComponentKind()
    {
        var expected = Verification(["Debug.Print FileName"]);

        AssertRejected(
            expected,
            Imported(
                ["Debug.Print Filename"],
                componentKind: VbaSourceKind.ClassModule));
    }

    [Fact]
    public void RejectsChangedLineCount()
    {
        var expected = Verification(["Debug.Print FileName"]);

        AssertRejected(
            expected,
            Imported(["Debug.Print Filename", string.Empty]));
    }

    [Fact]
    public void RejectsChangedLineStructure()
    {
        AssertRejected(
            Verification(["FileName ", "OtherName"]),
            Imported(["Filename", " OtherName"]));
    }

    [Fact]
    public void RejectsChangedTokenKind()
    {
        AssertRejected(
            Verification(["FileName"]),
            Imported(["\"Filename\""]));
    }

    [Fact]
    public void RejectsChangedTokenCount()
    {
        AssertRejected(
            Verification(["FileName"]),
            Imported(["Filename OtherName"]));
    }

    [Fact]
    public void RejectsChangedTokenPosition()
    {
        AssertRejected(
            Verification([" FileName"]),
            Imported(["Filename "]));
    }

    [Theory]
    [InlineData("Public FileName", "public Filename")]
    [InlineData("Debug.Print \"FileName\"", "Debug.Print \"Filename\"")]
    [InlineData("Debug.Print #1-Jan-2026#", "Debug.Print #1-jan-2026#")]
    [InlineData("Debug.Print 42", "Debug.Print 43")]
    [InlineData("Debug.Print FileName + 1", "Debug.Print Filename - 1")]
    [InlineData("Debug.Print FileName, OtherName", "Debug.Print Filename; OtherName")]
    [InlineData("Debug.Print FileName  ", "Debug.Print Filename \t")]
    [InlineData("#If FileName Then", "#If Filename Then")]
    public void RejectsChangedNonIdentifierText(string sourceLine, string vbeLine)
    {
        AssertRejected(
            Verification([sourceLine]),
            Imported([vbeLine]));
    }

    [Theory]
    [InlineData("Debug.Print FileName ' FileName", "Debug.Print Filename ' Filename")]
    [InlineData("Rem FileName", "Rem Filename")]
    [InlineData("FileName: Rem OtherName", "Filename: Rem Othername")]
    public void RejectsIdentifierLikeTextInsideCommentSuffixes(
        string sourceLine,
        string vbeLine)
    {
        AssertRejected(
            Verification([sourceLine]),
            Imported([vbeLine]));
    }

    [Theory]
    [InlineData("Debug.Print &HAF", "Debug.Print &Haf")]
    [InlineData("Debug.Print &O77", "Debug.Print &o77")]
    [InlineData("Debug.Print 1E3", "Debug.Print 1e3")]
    [InlineData("Debug.Print 1D+3", "Debug.Print 1d+3")]
    [InlineData("Debug.Print 1.E3", "Debug.Print 1.e3")]
    public void RejectsIdentifierTokensThatBelongToNumericLiterals(
        string sourceLine,
        string vbeLine)
    {
        AssertRejected(
            Verification([sourceLine]),
            Imported([vbeLine]));
    }

    [Theory]
    [InlineData("Debug.Print HAF", "Debug.Print haf", "HAF", "haf")]
    [InlineData("Debug.Print E3", "Debug.Print e3", "E3", "e3")]
    [InlineData("Debug.Print 1 + E3", "Debug.Print 1 + e3", "E3", "e3")]
    public void ClassifiesNumericLookingIdentifiersOutsideLiteralShapes(
        string sourceLine,
        string vbeLine,
        string sourceIdentifier,
        string vbeIdentifier)
    {
        var classified = VbeIdentifierRecasingClassifier.TryClassify(
            Verification([sourceLine]),
            Imported([vbeLine]),
            out var pairs);

        Assert.True(classified);
        Assert.Equal(
            new VbeIdentifierRecasingPair(sourceIdentifier, vbeIdentifier),
            Assert.Single(pairs));
    }

    [Theory]
    [InlineData(
        "Debug.Print FileName ' unchanged",
        "Debug.Print Filename ' unchanged")]
    [InlineData("FileName: Rem unchanged", "Filename: Rem unchanged")]
    public void ClassifiesIdentifierRecasingWithAnExactCommentSuffix(
        string sourceLine,
        string vbeLine)
    {
        var classified = VbeIdentifierRecasingClassifier.TryClassify(
            Verification([sourceLine]),
            Imported([vbeLine]),
            out var pairs);

        Assert.True(classified);
        Assert.Equal(
            new VbeIdentifierRecasingPair("FileName", "Filename"),
            Assert.Single(pairs));
    }

    [Fact]
    public void RejectsDifferentIdentifierSpelling()
    {
        AssertRejected(
            Verification(["Debug.Print FileName"]),
            Imported(["Debug.Print FilePath"]));
    }

    [Fact]
    public void ReturnsNoPartialPairsWhenAnyDifferenceIsUnsafe()
    {
        AssertRejected(
            Verification(["Debug.Print FileName + OtherName"]),
            Imported(["Debug.Print Filename - Othername"]));
    }

    [Fact]
    public void ReturnsDistinctPairsInFirstOccurrenceOrder()
    {
        var classified = VbeIdentifierRecasingClassifier.TryClassify(
            Verification([
                "FileName = OtherName",
                "FileName = OtherName"
            ]),
            Imported([
                "Filename = Othername",
                "Filename = Othername"
            ]),
            out var pairs);

        Assert.True(classified);
        Assert.Equal(
            [
                new VbeIdentifierRecasingPair("FileName", "Filename"),
                new VbeIdentifierRecasingPair("OtherName", "Othername")
            ],
            pairs);
    }

    [Fact]
    public void KeepsDistinctDirectionalPairsForMixedSourceCasing()
    {
        var classified = VbeIdentifierRecasingClassifier.TryClassify(
            Verification(["FileName", "FILENAME", "FileName"]),
            Imported(["Filename", "Filename", "Filename"]),
            out var pairs);

        Assert.True(classified);
        Assert.Equal(
            [
                new VbeIdentifierRecasingPair("FileName", "Filename"),
                new VbeIdentifierRecasingPair("FILENAME", "Filename")
            ],
            pairs);
    }

    [Fact]
    public void AllowsIdentifierRecasingInsideExactPunctuation()
    {
        var classified = VbeIdentifierRecasingClassifier.TryClassify(
            Verification(["[FileName] = OtherName$"]),
            Imported(["[Filename] = Othername$"]),
            out var pairs);

        Assert.True(classified);
        Assert.Equal(2, pairs.Count);
    }

    private static VbeImportVerification Verification(IReadOnlyList<string> lines)
        => new("Module1", VbaSourceKind.StandardModule, lines, "utf8");

    private static VbeImportedComponent Imported(
        IReadOnlyList<string> lines,
        string componentName = "Module1",
        VbaSourceKind componentKind = VbaSourceKind.StandardModule)
        => new(componentName, componentKind, lines);

    private static void AssertRejected(
        VbeImportVerification expected,
        VbeImportedComponent actual)
    {
        var classified = VbeIdentifierRecasingClassifier.TryClassify(
            expected,
            actual,
            out var pairs);

        Assert.False(classified);
        Assert.Empty(pairs);
    }
}
