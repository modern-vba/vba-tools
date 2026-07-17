using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaWithReceiverExpressionSyntaxTests
{
    [Theory]
    [InlineData("target", VbaModuleKind.StandardModule, false)]
    [InlineData("target.Parent", VbaModuleKind.StandardModule, false)]
    [InlineData("target. Parent", VbaModuleKind.StandardModule, false)]
    [InlineData("target _\n.Parent", VbaModuleKind.StandardModule, false)]
    [InlineData("GetTarget()", VbaModuleKind.StandardModule, false)]
    [InlineData("GetTarget(left + right)", VbaModuleKind.StandardModule, false)]
    [InlineData("target.CStr(value)", VbaModuleKind.StandardModule, false)]
    [InlineData("Library.Asc(\"A\")", VbaModuleKind.StandardModule, false)]
    [InlineData("target.Item(count%).Parent", VbaModuleKind.StandardModule, false)]
    [InlineData("target!Field", VbaModuleKind.StandardModule, false)]
    [InlineData("target _\n!Field", VbaModuleKind.StandardModule, false)]
    [InlineData("target _\n! _\nField", VbaModuleKind.StandardModule, false)]
    [InlineData("target.Mod", VbaModuleKind.StandardModule, false)]
    [InlineData("target.Mod(1)", VbaModuleKind.StandardModule, false)]
    [InlineData("target!Mod", VbaModuleKind.StandardModule, false)]
    [InlineData("target.Is", VbaModuleKind.StandardModule, false)]
    [InlineData("(target)", VbaModuleKind.StandardModule, false)]
    [InlineData("Nothing", VbaModuleKind.StandardModule, false)]
    [InlineData("Empty", VbaModuleKind.StandardModule, false)]
    [InlineData("Null", VbaModuleKind.StandardModule, false)]
    [InlineData("Err", VbaModuleKind.StandardModule, false)]
    [InlineData("Information.Err", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Information.Err", VbaModuleKind.StandardModule, false)]
    [InlineData("Information.Err()", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Information.Err()", VbaModuleKind.StandardModule, false)]
    [InlineData("CVar(value)", VbaModuleKind.StandardModule, false)]
    [InlineData("Len(value)", VbaModuleKind.StandardModule, false)]
    [InlineData("Strings.Len(value)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Strings.Len(value)", VbaModuleKind.StandardModule, false)]
    [InlineData("Date", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Date.Value", VbaModuleKind.StandardModule, false)]
    [InlineData("String(2, \"x\")", VbaModuleKind.StandardModule, false)]
    [InlineData("String(2, \"x\").Value", VbaModuleKind.StandardModule, false)]
    [InlineData("String(2, \"x\")!Value", VbaModuleKind.StandardModule, false)]
    [InlineData("Date.Value", VbaModuleKind.StandardModule, false)]
    [InlineData("Date!Value", VbaModuleKind.StandardModule, false)]
    [InlineData("Date()(0)", VbaModuleKind.StandardModule, false)]
    [InlineData("CreateObject(\"Scripting.Dictionary\")", VbaModuleKind.StandardModule, false)]
    [InlineData("Interaction.CreateObject(\"Scripting.Dictionary\")", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.CreateObject(\"Scripting.Dictionary\")", VbaModuleKind.StandardModule, false)]
    [InlineData("FileSystem.CurDir", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Strings.LeftB(\"text\", 1)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Array()", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Array(1, \"x\")", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Array(1, , 3)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Input(1, fileNumber)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.InputB(1, fileNumber)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.String(Number:=2, Character:=\"x\")", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.DateDiff(Interval:=\"d\", Date1:=startDate, Date2:=endDate)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.DateValue(Date:=startDate)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.DatePart(Interval:=\"d\", Date:=startDate)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.GetObject(PathName:=\"book.xls\")", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Environ(Expression:=\"PATH\")", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.Choose(1, \"a\", \"b\")", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.CallByName(target, \"Name\", vbGet)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.StrConv(\"text\", vbUpperCase)", VbaModuleKind.StandardModule, false)]
    [InlineData("VBA.InStr(Start:=1, String1:=\"abc\", String2:=\"b\")", VbaModuleKind.StandardModule, false)]
    [InlineData("DateDiff(\"d\", startDate, endDate)", VbaModuleKind.StandardModule, false)]
    [InlineData("Partition(value, 0, 100, 10)", VbaModuleKind.StandardModule, false)]
    [InlineData("Abs(value)", VbaModuleKind.StandardModule, false)]
    [InlineData("Library.Join(values)", VbaModuleKind.StandardModule, false)]
    [InlineData("target.TypeName(value)", VbaModuleKind.StandardModule, false)]
    [InlineData("New Class1", VbaModuleKind.StandardModule, false)]
    [InlineData("New Project1.Class1", VbaModuleKind.StandardModule, false)]
    [InlineData("New Project1. Class1", VbaModuleKind.StandardModule, false)]
    [InlineData("New Project1 _\n.Class1", VbaModuleKind.StandardModule, false)]
    [InlineData("Me", VbaModuleKind.ClassModule, false)]
    [InlineData(".Font", VbaModuleKind.StandardModule, true)]
    [InlineData("!Child", VbaModuleKind.StandardModule, true)]
    [InlineData("! Child", VbaModuleKind.StandardModule, true)]
    [InlineData("! _\nChild", VbaModuleKind.StandardModule, true)]
    public void Potential_udt_class_object_and_variant_receivers_are_accepted(
        string expression,
        VbaModuleKind moduleKind,
        bool allowLeadingMemberAccess)
    {
        Assert.True(IsComplete(expression, moduleKind, allowLeadingMemberAccess));
    }

    [Theory]
    [InlineData("1")]
    [InlineData(".5")]
    [InlineData("&HFF")]
    [InlineData("\"text\"")]
    [InlineData("#1/1/2020#")]
    [InlineData("True")]
    [InlineData("False")]
    [InlineData("TypeOf target Is Class1")]
    [InlineData("Not target")]
    [InlineData("Not CVar(value)")]
    [InlineData("+target")]
    [InlineData("-target")]
    [InlineData("-CVar(value)")]
    [InlineData("left + right")]
    [InlineData("CVar(value) + 1")]
    [InlineData("1 + CVar(value)")]
    [InlineData("CVar(value) & \"x\"")]
    [InlineData("left Mod right")]
    [InlineData("target Is Nothing")]
    [InlineData("(target + other)")]
    [InlineData("count%")]
    [InlineData("GetText$(1)")]
    [InlineData("target.Value%")]
    [InlineData("New Class1 Is Nothing")]
    [InlineData("CStr(value)")]
    [InlineData("CBool(value)")]
    [InlineData("CLng(value)")]
    [InlineData("Asc(\"A\")")]
    [InlineData("Strings.Asc(\"A\")")]
    [InlineData("VBA.Strings.Asc(\"A\")")]
    [InlineData("VBA.Asc(\"A\")")]
    [InlineData("Conversion.CStr(value)")]
    [InlineData("VBA.Conversion.CStr(value)")]
    [InlineData("VBA.CStr(value)")]
    [InlineData("target! Field")]
    [InlineData("target !Field")]
    [InlineData("target ! Field")]
    [InlineData("target! _\nField")]
    [InlineData("target _\n! Field")]
    [InlineData("target .Parent")]
    [InlineData("New Project1 .Class1")]
    [InlineData("New Project1 . Class1")]
    [InlineData("Circle")]
    [InlineData("Scale")]
    [InlineData("PSet")]
    [InlineData("Print")]
    public void Known_scalar_or_operator_receivers_are_rejected(string expression)
    {
        Assert.False(IsComplete(expression));
    }

    public static IEnumerable<object[]> Standard_library_non_receiver_members()
        => VbaStandardLibrarySyntaxFacts.NonReceiverMembers
            .Select(member => new object[] { member.OwnerName ?? string.Empty, member.MemberName });

    public static IEnumerable<object[]> Standard_library_non_value_owners()
        => VbaStandardLibrarySyntaxFacts.NonReceiverMembers
            .Where(member => member.AccessKind == VbaStandardLibraryMemberAccessKind.GlobalNamespace)
            .Select(member => member.OwnerName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(moduleName => new object[] { moduleName });

    [Fact]
    public void Non_receiver_catalog_matches_the_standard_library_owner_counts()
    {
        var expectedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Conversion"] = 13,
            ["DateTime"] = 2,
            ["FileSystem"] = 16,
            ["Financial"] = 13,
            ["Information"] = 14,
            ["Interaction"] = 12,
            ["Math"] = 9,
            ["Strings"] = 13,
            ["FormShowConstants"] = 2,
            ["VbAppWinStyle"] = 6,
            ["VbCalendar"] = 2,
            ["VbCallType"] = 4,
            ["VbCompareMethod"] = 2,
            ["VbDateTimeFormat"] = 5,
            ["VbDayOfWeek"] = 8,
            ["VbFileAttribute"] = 8,
            ["VbFirstWeekOfYear"] = 4,
            ["VbIMEStatus"] = 20,
            ["VbMsgBoxResult"] = 7,
            ["VbMsgBoxStyle"] = 20,
            ["VbQueryClose"] = 5,
            ["VbStrConv"] = 9,
            ["VbTriState"] = 3,
            ["VbVarType"] = 19,
            ["ColorConstants"] = 8,
            ["Constants"] = 11,
            ["KeyCodeConstants"] = 99,
            ["SystemColorConstants"] = 27,
            ["Err"] = 8,
            ["Global"] = 2,
            ["VBA"] = 8
        };
        var actualCounts = VbaStandardLibrarySyntaxFacts.NonReceiverMembers
            .GroupBy(member => member.OwnerName ?? "VBA", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(379, VbaStandardLibrarySyntaxFacts.NonReceiverMembers.Count);
        Assert.Equal(expectedCounts.Count, actualCounts.Count);
        foreach (var expected in expectedCounts)
        {
            Assert.Equal(expected.Value, actualCounts[expected.Key]);
        }

        Assert.Equal(
            ["ObjPtr", "StrPtr", "VarPtr", "Width"],
            VbaStandardLibrarySyntaxFacts.NonReceiverMembers
                .Where(member =>
                    member.AccessKind == VbaStandardLibraryMemberAccessKind.HiddenGlobalNamespace)
                .Select(member => member.MemberName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            ["LBound", "Spc", "Tab", "UBound"],
            VbaStandardLibrarySyntaxFacts.NonReceiverMembers
                .Where(member =>
                    member.AccessKind == VbaStandardLibraryMemberAccessKind.CompilerIntrinsic)
                .Select(member => member.MemberName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        Assert.All(
            VbaStandardLibrarySyntaxFacts.NonReceiverMembers.Where(member =>
                member.AccessKind is
                    VbaStandardLibraryMemberAccessKind.HiddenGlobalNamespace or
                    VbaStandardLibraryMemberAccessKind.CompilerIntrinsic),
            member => Assert.Null(member.OwnerName));
    }

    [Fact]
    public void Potential_receiver_catalog_matches_the_standard_library_owner_counts()
    {
        var expectedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Conversion"] = 10,
            ["DateTime"] = 17,
            ["FileSystem"] = 2,
            ["Information"] = 1,
            ["Interaction"] = 10,
            ["Math"] = 3,
            ["Strings"] = 25,
            ["VBA"] = 3
        };
        var actualCounts = VbaStandardLibrarySyntaxFacts.PotentialReceiverMembers
            .GroupBy(member => member.OwnerName ?? "VBA", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        Assert.Equal(71, VbaStandardLibrarySyntaxFacts.PotentialReceiverMembers.Count);
        Assert.Equal(450,
            VbaStandardLibrarySyntaxFacts.NonReceiverMembers.Count
                + VbaStandardLibrarySyntaxFacts.PotentialReceiverMembers.Count);
        Assert.Equal(expectedCounts.Count, actualCounts.Count);
        foreach (var expected in expectedCounts)
        {
            Assert.Equal(expected.Value, actualCounts[expected.Key]);
        }

        foreach (var member in VbaStandardLibrarySyntaxFacts.PotentialReceiverMembers)
        {
            Assert.Equal(
                VbaStandardLibraryMemberReceiverClassification.PotentialReceiver,
                VbaStandardLibrarySyntaxFacts.ClassifyGlobalMember(member.MemberName));
            if (member.OwnerName is not null)
            {
                Assert.Equal(
                    VbaStandardLibraryMemberReceiverClassification.PotentialReceiver,
                    VbaStandardLibrarySyntaxFacts.ClassifyOwnedMember(
                        member.OwnerName,
                        member.MemberName));
            }
        }

        var informationErr = Assert.Single(
            VbaStandardLibrarySyntaxFacts.PotentialReceiverMembers,
            member => member.OwnerName == "Information" && member.MemberName == "Err");
        Assert.Equal(
            VbaStandardLibraryPotentialReceiverDeclaredTypeCategory.NamedObject,
            informationErr.DeclaredTypeCategory);
        Assert.Equal(
            ["Array", "Input", "InputB"],
            VbaStandardLibrarySyntaxFacts.PotentialReceiverMembers
                .Where(member => member.OwnerName is null)
                .Select(member => member.MemberName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Standard_library_non_receiver_members))]
    public void Standard_library_non_receiver_members_are_rejected_in_all_qualification_forms(
        string ownerName,
        string memberName)
    {
        Assert.False(IsComplete(memberName));
        Assert.False(IsComplete($"VBA.{memberName}"));
        if (ownerName.Length > 0)
        {
            Assert.False(IsComplete($"{ownerName}.{memberName}"));
            Assert.False(IsComplete($"VBA.{ownerName}.{memberName}"));
        }
    }

    [Theory]
    [MemberData(nameof(Standard_library_non_value_owners))]
    public void Standard_library_module_and_enum_owners_are_not_value_receivers(string ownerName)
    {
        Assert.False(IsComplete(ownerName));
        Assert.False(IsComplete($"VBA.{ownerName}"));
    }

    [Theory]
    [InlineData("Join(values)")]
    [InlineData("Strings.Join(values)")]
    [InlineData("VBA.Join(values)")]
    [InlineData("VBA.Strings.Join(values)")]
    [InlineData("VBA.Information.IsNumeric(value)")]
    [InlineData("VBA.DateTime.Timer")]
    [InlineData("VBA.FileSystem.EOF(1)")]
    [InlineData("VBA.Financial.NPV(rate, values)")]
    [InlineData("VBA.Interaction.MsgBox(prompt)")]
    [InlineData("VBA.Math.Rnd")]
    [InlineData("Timer.Value")]
    [InlineData("DateTime.Timer.Value")]
    [InlineData("VBA.Timer.Value")]
    [InlineData("VBA.DateTime.Timer.Value")]
    [InlineData("VBA.Math.Rnd.Value")]
    [InlineData("Timer!Field")]
    [InlineData("DateTime.Timer!Field")]
    [InlineData("VBA.Timer!Field")]
    [InlineData("VBA.DateTime.Timer!Field")]
    [InlineData("vbCrLf")]
    [InlineData("Constants.vbCrLf")]
    [InlineData("VBA.vbCrLf")]
    [InlineData("VBA.Constants.vbCrLf")]
    [InlineData("VBA.Constants.vbCrLf.Value")]
    [InlineData("vbRed")]
    [InlineData("VBA.ColorConstants.vbRed")]
    [InlineData("vbBinaryCompare")]
    [InlineData("VbCompareMethod.vbBinaryCompare")]
    [InlineData("VBA.VbCompareMethod.vbBinaryCompare")]
    [InlineData("vbKeyA")]
    [InlineData("VBA.KeyCodeConstants.vbKeyA")]
    [InlineData("vbButtonFace")]
    [InlineData("VBA.SystemColorConstants.vbButtonFace")]
    [InlineData("VBA")]
    [InlineData("Err.Number")]
    [InlineData("VBA.Err.Number")]
    [InlineData("Err()")]
    [InlineData("Err(1)")]
    [InlineData("Err().Value")]
    [InlineData("VBA.Err()")]
    [InlineData("VBA.Err().Value")]
    [InlineData("Err!Field")]
    [InlineData("VBA.Err!Field")]
    [InlineData("(Err)()")]
    [InlineData("(CStr(value)).Value")]
    [InlineData("(VBA.Constants.vbCrLf).Value")]
    [InlineData("(Strings.Unknown).Value")]
    [InlineData("Err.Description.Value")]
    [InlineData("VBA.Err.Number!Field")]
    [InlineData("Beep")]
    [InlineData("Interaction.Beep")]
    [InlineData("VBA.Beep")]
    [InlineData("VBA.Interaction.Beep")]
    [InlineData("VBA.Interaction.Beep()")]
    [InlineData("VBA.Interaction.Beep.Value")]
    [InlineData("Err.Clear")]
    [InlineData("VBA.Err.Clear")]
    [InlineData("Err.Raise(1)")]
    [InlineData("Clear")]
    [InlineData("VBA.Clear")]
    [InlineData("Global.Load")]
    [InlineData("VBA.Load")]
    [InlineData("Collection")]
    [InlineData("VBA.Collection")]
    [InlineData("Collection.Item")]
    [InlineData("Collection.Add")]
    [InlineData("VBA.Collection.Item")]
    [InlineData("Strings.Unknown")]
    [InlineData("VBA.Strings.Unknown")]
    [InlineData("VbDayOfWeek.Unknown")]
    [InlineData("Err.Unknown")]
    [InlineData("VBA.Err.Unknown")]
    [InlineData("Global.Unknown")]
    [InlineData("VBA.Unknown")]
    [InlineData("VBA.String(1)")]
    [InlineData("Strings.String()")]
    [InlineData("VBA.String(1, 2, 3)")]
    [InlineData("VBA.String(, \"x\")")]
    [InlineData("VBA.String(1, Number:=2, Character:=\"x\")")]
    [InlineData("VBA.CVar()")]
    [InlineData("VBA.CVar(1, 2)")]
    [InlineData("VBA.CVar(Unknown:=1)")]
    [InlineData("Strings.Len")]
    [InlineData("Strings.Len()")]
    [InlineData("VBA.Date(1)")]
    [InlineData("VBA.Array(ArgList:=1)")]
    [InlineData("VBA.Choose(Index:=1)")]
    [InlineData("VBA.GetObject(, PathName:=\"book.xls\")")]
    [InlineData("VBA.CVar(String:=1)")]
    [InlineData("Information.Err.Number")]
    [InlineData("VBA.Information.Err.Clear")]
    [InlineData("Information.Err!Field")]
    [InlineData("Information.Err(1)")]
    [InlineData("Information.Err().Number")]
    [InlineData("(Information.Err).Number")]
    [InlineData("MacID(1)")]
    [InlineData("Reset")]
    [InlineData("Erl")]
    [InlineData("MacScript(\"return 1\")")]
    [InlineData("Width")]
    [InlineData("VarPtr(value)")]
    [InlineData("StrPtr(text)")]
    [InlineData("ObjPtr(target)")]
    [InlineData("LBound(values)")]
    [InlineData("UBound(values)")]
    [InlineData("Spc(1)")]
    [InlineData("Tab")]
    public void Representative_standard_library_non_receiver_paths_are_rejected(string expression)
    {
        Assert.False(IsComplete(expression));
    }

    private static bool IsComplete(
        string expression,
        VbaModuleKind moduleKind = VbaModuleKind.StandardModule,
        bool allowLeadingMemberAccess = false)
    {
        var tokens = VbaTokenStream.FromText($"With {expression}").Tokens
            .Where(token => token.Kind is not VbaTokenKind.Whitespace
                and not VbaTokenKind.NewLine
                and not VbaTokenKind.LineContinuation
                and not VbaTokenKind.Comment)
            .ToArray();
        return VbaWithReceiverExpressionSyntax.IsComplete(
            tokens,
            1,
            tokens.Length,
            moduleKind,
            allowLeadingMemberAccess);
    }
}
