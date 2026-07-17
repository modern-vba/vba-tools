namespace VbaLanguageServer.Syntax;

/// <summary>
/// Syntax facts for finite VBA Standard Library member paths whose declared
/// type determines whether they can satisfy a With receiver.
/// </summary>
internal static class VbaStandardLibrarySyntaxFacts
{
    private static readonly IReadOnlyList<VbaStandardLibraryMemberSyntaxFact> FixedReturnMemberFacts =
        Array.AsReadOnly(new[]
        {
            Scalar("Conversion", "CBool"),
            Scalar("Conversion", "CByte"),
            Scalar("Conversion", "CCur"),
            Scalar("Conversion", "CDate"),
            Scalar("Conversion", "CDbl"),
            Scalar("Conversion", "CInt"),
            Scalar("Conversion", "CLng"),
            Scalar("Conversion", "CLngLng"),
            Scalar("Conversion", "CLngPtr"),
            Scalar("Conversion", "CSng"),
            Scalar("Conversion", "CStr"),
            Scalar("Conversion", "MacID"),
            Scalar("Conversion", "Val"),

            Enum("DateTime", "Calendar"),
            Scalar("DateTime", "Timer"),

            Scalar("FileSystem", "Dir"),
            Scalar("FileSystem", "EOF"),
            Scalar("FileSystem", "FileAttr"),
            Scalar("FileSystem", "FileLen"),
            Scalar("FileSystem", "FreeFile"),
            Scalar("FileSystem", "Loc"),
            Scalar("FileSystem", "LOF"),
            Scalar("FileSystem", "Seek"),

            Scalar("Financial", "DDB"),
            Scalar("Financial", "FV"),
            Scalar("Financial", "IPmt"),
            Scalar("Financial", "IRR"),
            Scalar("Financial", "MIRR"),
            Scalar("Financial", "NPer"),
            Scalar("Financial", "NPV"),
            Scalar("Financial", "Pmt"),
            Scalar("Financial", "PPmt"),
            Scalar("Financial", "PV"),
            Scalar("Financial", "Rate"),
            Scalar("Financial", "SLN"),
            Scalar("Financial", "SYD"),

            Enum("Information", "IMEStatus"),
            Scalar("Information", "IsArray"),
            Scalar("Information", "IsDate"),
            Scalar("Information", "IsEmpty"),
            Scalar("Information", "IsError"),
            Scalar("Information", "IsMissing"),
            Scalar("Information", "IsNull"),
            Scalar("Information", "IsNumeric"),
            Scalar("Information", "IsObject"),
            Scalar("Information", "Erl"),
            Scalar("Information", "QBColor"),
            Scalar("Information", "RGB"),
            Scalar("Information", "TypeName"),
            Enum("Information", "VarType"),

            Scalar("Interaction", "DoEvents"),
            Enum("Interaction", "GetAttr"),
            Scalar("Interaction", "GetSetting"),
            Scalar("Interaction", "InputBox"),
            Scalar("Interaction", "MacScript"),
            Enum("Interaction", "MsgBox"),
            Scalar("Interaction", "Shell"),

            Scalar("Math", "Atn"),
            Scalar("Math", "Cos"),
            Scalar("Math", "Exp"),
            Scalar("Math", "Log"),
            Scalar("Math", "Rnd"),
            Scalar("Math", "Sin"),
            Scalar("Math", "Sqr"),
            Scalar("Math", "Tan"),

            Scalar("Strings", "Asc"),
            Scalar("Strings", "AscB"),
            Scalar("Strings", "AscW"),
            Scalar("Strings", "FormatCurrency"),
            Scalar("Strings", "FormatDateTime"),
            Scalar("Strings", "FormatNumber"),
            Scalar("Strings", "FormatPercent"),
            Scalar("Strings", "InStrRev"),
            Scalar("Strings", "Join"),
            Scalar("Strings", "MonthName"),
            Scalar("Strings", "Replace"),
            Scalar("Strings", "StrReverse"),
            Scalar("Strings", "WeekdayName")
        });

    private static readonly IReadOnlyList<VbaStandardLibraryMemberSyntaxFact> NonReceiverMemberFacts =
        Array.AsReadOnly(FixedReturnMemberFacts
            .Concat(CreateFixedConstantFacts())
            .Concat(CreateGlobalNamespaceAndObjectFixedValueFacts())
            .Concat(CreateNoValueMemberFacts())
            .Concat(CreateSupplementalGlobalNonReceiverFacts())
            .ToArray());

    private static readonly IReadOnlyList<VbaStandardLibraryPotentialReceiverMemberSyntaxFact>
        PotentialReceiverMemberFacts = CreatePotentialReceiverMemberFacts();

    private static readonly IReadOnlyDictionary<string, VbaStandardLibraryMemberSyntaxFact>
        GlobalNonReceiverMembersByName = NonReceiverMemberFacts
            .ToDictionary(
                member => member.MemberName,
                StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, Dictionary<string, VbaStandardLibraryMemberSyntaxFact>>
        NonReceiverMembersByOwner = NonReceiverMemberFacts
            .Where(member => member.OwnerName is not null)
            .GroupBy(member => member.OwnerName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(
                    member => member.MemberName,
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, VbaStandardLibraryPotentialReceiverMemberSyntaxFact>
        GlobalPotentialReceiverMembersByName = PotentialReceiverMemberFacts
            .ToDictionary(
                member => member.MemberName,
                StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<
        string,
        Dictionary<string, VbaStandardLibraryPotentialReceiverMemberSyntaxFact>>
        PotentialReceiverMembersByOwner = PotentialReceiverMemberFacts
            .Where(member => member.OwnerName is not null)
            .GroupBy(member => member.OwnerName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(
                    member => member.MemberName,
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> KnownOwnerNames =
        new HashSet<string>(
            NonReceiverMemberFacts
                .Where(member => member.OwnerName is not null)
                .Select(member => member.OwnerName!)
                .Concat(PotentialReceiverMemberFacts
                    .Where(member => member.OwnerName is not null)
                    .Select(member => member.OwnerName!))
                .Append("Collection"),
            StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> NonValueTypeOwnerNames =
        new HashSet<string>(["Collection"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> NonReceiverDefaultMemberOwnerNames =
        new HashSet<string>(["Err"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> NamedReceiverObjectNames =
        new HashSet<string>(["Err"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The finite fixed scalar, enum, and no-value declarations from MS-VBAL,
    /// supplemented by VBA 7.1 hidden globals and compiler intrinsics.
    /// Value-returning members declared as Variant or Object are deliberately
    /// absent. Members with an explicit identifier type character are rejected
    /// independently.
    /// </summary>
    public static IReadOnlyList<VbaStandardLibraryMemberSyntaxFact> NonReceiverMembers
        => NonReceiverMemberFacts;

    /// <summary>
    /// The finite unsuffixed VBA Standard Library members whose declared
    /// Variant or object type can satisfy a With receiver. Variant results may
    /// remain eligible for late-bound postfix access; named object results keep
    /// their stricter declared-member boundary.
    /// </summary>
    public static IReadOnlyList<VbaStandardLibraryPotentialReceiverMemberSyntaxFact>
        PotentialReceiverMembers
        => PotentialReceiverMemberFacts;

    public static VbaStandardLibraryMemberReceiverClassification ClassifyGlobalMember(
        string memberName)
    {
        if (GlobalNonReceiverMembersByName.ContainsKey(memberName))
        {
            return VbaStandardLibraryMemberReceiverClassification.NonReceiver;
        }

        return GlobalPotentialReceiverMembersByName.ContainsKey(memberName)
            ? VbaStandardLibraryMemberReceiverClassification.PotentialReceiver
            : VbaStandardLibraryMemberReceiverClassification.Unknown;
    }

    public static bool TryGetGlobalPotentialReceiverMember(
        string memberName,
        out VbaStandardLibraryPotentialReceiverMemberSyntaxFact member)
        => GlobalPotentialReceiverMembersByName.TryGetValue(memberName, out member!);

    public static VbaStandardLibraryMemberReceiverClassification ClassifyOwnedMember(
        string ownerName,
        string memberName)
    {
        if (NonReceiverMembersByOwner.TryGetValue(ownerName, out var nonReceiverMembers)
            && nonReceiverMembers.ContainsKey(memberName))
        {
            return VbaStandardLibraryMemberReceiverClassification.NonReceiver;
        }

        return PotentialReceiverMembersByOwner.TryGetValue(ownerName, out var potentialMembers)
            && potentialMembers.ContainsKey(memberName)
                ? VbaStandardLibraryMemberReceiverClassification.PotentialReceiver
                : VbaStandardLibraryMemberReceiverClassification.Unknown;
    }

    public static bool TryGetOwnedPotentialReceiverMember(
        string ownerName,
        string memberName,
        out VbaStandardLibraryPotentialReceiverMemberSyntaxFact member)
    {
        if (PotentialReceiverMembersByOwner.TryGetValue(ownerName, out var ownerMembers))
        {
            return ownerMembers.TryGetValue(memberName, out member!);
        }

        member = null!;
        return false;
    }

    public static bool IsKnownOwner(string ownerName)
        => KnownOwnerNames.Contains(ownerName);

    public static bool IsNonValueTypeOwner(string ownerName)
        => NonValueTypeOwnerNames.Contains(ownerName);

    public static bool IsNamedReceiverObject(string ownerName)
        => NamedReceiverObjectNames.Contains(ownerName);

    /// <summary>
    /// Returns whether an owner exposes a default member whose declared type
    /// cannot satisfy the static type requirement of a With receiver.
    /// Err.Number is the only such VBA Standard Library member.
    /// </summary>
    public static bool HasNonReceiverDefaultMember(string ownerName)
        => NonReceiverDefaultMemberOwnerNames.Contains(ownerName);

    private static VbaStandardLibraryMemberSyntaxFact Scalar(string ownerName, string memberName)
        => new(
            ownerName,
            memberName,
            VbaStandardLibraryMemberAccessKind.GlobalNamespace,
            VbaStandardLibraryDeclaredTypeCategory.FixedScalar);

    private static VbaStandardLibraryMemberSyntaxFact Enum(string ownerName, string memberName)
        => new(
            ownerName,
            memberName,
            VbaStandardLibraryMemberAccessKind.GlobalNamespace,
            VbaStandardLibraryDeclaredTypeCategory.FixedEnum);

    private static VbaStandardLibraryMemberSyntaxFact GlobalNamespaceAndObjectScalar(
        string ownerName,
        string memberName)
        => new(
            ownerName,
            memberName,
            VbaStandardLibraryMemberAccessKind.GlobalNamespaceAndObject,
            VbaStandardLibraryDeclaredTypeCategory.FixedScalar);

    private static VbaStandardLibraryMemberSyntaxFact Sub(
        string ownerName,
        string memberName,
        VbaStandardLibraryMemberAccessKind accessKind)
        => new(
            ownerName,
            memberName,
            accessKind,
            VbaStandardLibraryDeclaredTypeCategory.NoValue);

    private static IReadOnlyList<VbaStandardLibraryPotentialReceiverMemberSyntaxFact>
        CreatePotentialReceiverMemberFacts()
        => Array.AsReadOnly(new[]
        {
            Function(null, "Array", "*ArgList"),
            Function(null, "Input", "Number FileNumber"),
            Function(null, "InputB", "Number FileNumber"),

            Function("Conversion", "CVDate", "Expression"),
            Function("Conversion", "CDec", "Expression"),
            Function("Conversion", "CVar", "Expression"),
            Function("Conversion", "CVErr", "Expression"),
            Function("Conversion", "Error", "?ErrorNumber"),
            Function("Conversion", "Fix", "Number"),
            Function("Conversion", "Hex", "Number"),
            Function("Conversion", "Int", "Number"),
            Function("Conversion", "Oct", "Number"),
            Function("Conversion", "Str", "Number"),

            Function("DateTime", "DateAdd", "Interval Number Date"),
            Function("DateTime", "DateDiff", "Interval Date1 Date2 ?FirstDayOfWeek ?FirstWeekOfYear"),
            Function("DateTime", "DatePart", "Interval Date ?FirstDayOfWeek ?FirstWeekOfYear"),
            Function("DateTime", "DateSerial", "Year Month Day"),
            Function("DateTime", "DateValue", "Date"),
            Function("DateTime", "Day", "Date"),
            Function("DateTime", "Hour", "Time"),
            Function("DateTime", "Minute", "Time"),
            Function("DateTime", "Month", "Date"),
            Function("DateTime", "Second", "Time"),
            Function("DateTime", "TimeSerial", "Hour Minute Second"),
            Function("DateTime", "TimeValue", "Time"),
            Function("DateTime", "Weekday", "Date ?FirstDayOfWeek"),
            Function("DateTime", "Year", "Date"),
            Property("DateTime", "Date"),
            Property("DateTime", "Now"),
            Property("DateTime", "Time"),

            Function("FileSystem", "CurDir", "?Drive"),
            Function("FileSystem", "FileDateTime", "PathName"),

            ObjectFunction("Information", "Err", ""),

            Function("Interaction", "CallByName", "Object ProcName CallType *Args"),
            Function("Interaction", "Choose", "Index *Choice"),
            Function("Interaction", "Command", ""),
            Function("Interaction", "CreateObject", "Class ?ServerName"),
            Function("Interaction", "Environ", "Expression"),
            Function("Interaction", "GetAllSettings", "AppName Section"),
            Function("Interaction", "GetObject", "?PathName ?Class"),
            Function("Interaction", "IIf", "Expression TruePart FalsePart"),
            Function("Interaction", "Partition", "Number Start Stop Interval"),
            Function("Interaction", "Switch", "*VarExpr"),

            Function("Math", "Abs", "Number"),
            Function("Math", "Round", "Number ?NumDigitsAfterDecimal"),
            Function("Math", "Sgn", "Number"),

            Function("Strings", "Chr", "CharCode"),
            Function("Strings", "ChrB", "CharCode"),
            Function("Strings", "ChrW", "CharCode"),
            Function("Strings", "Filter", "SourceArray Match ?Include ?Compare"),
            Function("Strings", "Format", "Expression ?Format ?FirstDayOfWeek ?FirstWeekOfYear"),
            Function("Strings", "InStr", "?Start ?String1 ?String2 ?Compare"),
            Function("Strings", "InStrB", "?Start ?String1 ?String2 ?Compare"),
            Function("Strings", "LCase", "String"),
            Function("Strings", "Left", "String Length"),
            Function("Strings", "LeftB", "String Length"),
            Function("Strings", "Len", "Expression"),
            Function("Strings", "LenB", "Expression"),
            Function("Strings", "LTrim", "String"),
            Function("Strings", "RTrim", "String"),
            Function("Strings", "Trim", "String"),
            Function("Strings", "Mid", "String Start ?Length"),
            Function("Strings", "MidB", "String Start ?Length"),
            Function("Strings", "Right", "String Length"),
            Function("Strings", "RightB", "String Length"),
            Function("Strings", "Space", "Number"),
            Function("Strings", "Split", "Expression ?Delimiter ?Limit ?Compare"),
            Function("Strings", "StrComp", "String1 String2 ?Compare"),
            Function("Strings", "StrConv", "String Conversion ?LocaleID"),
            Function("Strings", "String", "Number Character"),
            Function("Strings", "UCase", "String")
        });

    private static VbaStandardLibraryPotentialReceiverMemberSyntaxFact Function(
        string? ownerName,
        string memberName,
        string parameterSpecification)
        => new(
            ownerName,
            memberName,
            VbaStandardLibraryPotentialReceiverMemberKind.Function,
            VbaStandardLibraryPotentialReceiverDeclaredTypeCategory.Variant,
            CreateParameterSignature(parameterSpecification));

    private static VbaStandardLibraryPotentialReceiverMemberSyntaxFact ObjectFunction(
        string ownerName,
        string memberName,
        string parameterSpecification)
        => Function(ownerName, memberName, parameterSpecification) with
        {
            DeclaredTypeCategory =
                VbaStandardLibraryPotentialReceiverDeclaredTypeCategory.NamedObject
        };

    private static VbaStandardLibraryPotentialReceiverMemberSyntaxFact Property(
        string ownerName,
        string memberName)
        => new(
            ownerName,
            memberName,
            VbaStandardLibraryPotentialReceiverMemberKind.Property,
            VbaStandardLibraryPotentialReceiverDeclaredTypeCategory.Variant,
            []);

    private static IReadOnlyList<VbaStandardLibraryParameterSyntaxFact>
        CreateParameterSignature(string parameterSpecification)
        => parameterSpecification
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => new VbaStandardLibraryParameterSyntaxFact(
                parameter.TrimStart('?', '*'),
                IsOptional: parameter.StartsWith('?'),
                IsParamArray: parameter.StartsWith('*')))
            .ToArray();

    private static IReadOnlyList<VbaStandardLibraryMemberSyntaxFact> CreateFixedConstantFacts()
    {
        var members = new List<VbaStandardLibraryMemberSyntaxFact>();

        AddMembers(members, "FormShowConstants", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbModal vbModeless");
        AddMembers(members, "VbAppWinStyle", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbHide vbMaximizedFocus vbMinimizedFocus vbMinimizedNoFocus vbNormalFocus vbNormalNoFocus");
        AddMembers(members, "VbCalendar", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbCalGreg vbCalHijri");
        AddMembers(members, "VbCallType", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbGet vbLet vbMethod vbSet");
        AddMembers(members, "VbCompareMethod", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbBinaryCompare vbTextCompare");
        AddMembers(members, "VbDateTimeFormat", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbGeneralDate vbLongDate vbLongTime vbShortDate vbShortTime");
        AddMembers(members, "VbDayOfWeek", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbFriday vbMonday vbSaturday vbSunday vbThursday vbTuesday vbUseSystemDayOfWeek vbWednesday");
        AddMembers(members, "VbFileAttribute", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbNormal vbReadOnly vbHidden vbSystem vbVolume vbDirectory vbArchive vbAlias");
        AddMembers(members, "VbFirstWeekOfYear", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbFirstFourDays vbFirstFullWeek vbFirstJan1 vbUseSystem");
        AddMembers(members, "VbIMEStatus", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbIMEAlphaDbl vbIMEAlphaSng vbIMEDisable vbIMEHiragana vbIMEKatakanaDbl " +
            "vbIMEKatakanaSng vbIMEModeAlpha vbIMEModeAlphaFull vbIMEModeDisable " +
            "vbIMEModeHangul vbIMEModeHangulFull vbIMEModeHiragana vbIMEModeKatakana " +
            "vbIMEModeKatakanaHalf vbIMEModeNoControl vbIMEModeOff vbIMEModeOn " +
            "vbIMENoOp vbIMEOff vbIMEOn");
        AddMembers(members, "VbMsgBoxResult", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbAbort vbCancel vbIgnore vbNo vbOK vbRetry vbYes");
        AddMembers(members, "VbMsgBoxStyle", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbAbortRetryIgnore vbApplicationModal vbCritical vbDefaultButton1 vbDefaultButton2 " +
            "vbDefaultButton3 vbDefaultButton4 vbExclamation vbInformation vbMsgBoxHelpButton " +
            "vbMsgBoxRight vbMsgBoxRtlReading vbMsgBoxSetForeground vbOKCancel vbOKOnly " +
            "vbQuestion vbRetryCancel vbSystemModal vbYesNo vbYesNoCancel");
        AddMembers(members, "VbQueryClose", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbAppTaskManager vbAppWindows vbFormCode vbFormControlMenu vbFormMDIForm");
        AddMembers(members, "VbStrConv", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbFromUnicode vbHiragana vbKatakana vbLowerCase vbNarrow vbProperCase " +
            "vbUnicode vbUpperCase vbWide");
        AddMembers(members, "VbTriState", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbFalse vbTrue vbUseDefault");
        AddMembers(members, "VbVarType", VbaStandardLibraryDeclaredTypeCategory.FixedEnum,
            "vbArray vbBoolean vbByte vbCurrency vbDataObject vbDate vbDecimal vbDouble vbEmpty " +
            "vbError vbInteger vbLong vbLongLong vbNull vbObject vbSingle vbString " +
            "vbUserDefinedType vbVariant");

        AddMembers(members, "ColorConstants", VbaStandardLibraryDeclaredTypeCategory.FixedScalar,
            "vbBlack vbBlue vbCyan vbGreen vbMagenta vbRed vbWhite vbYellow");
        AddMembers(members, "Constants", VbaStandardLibraryDeclaredTypeCategory.FixedScalar,
            "vbBack vbCr vbCrLf vbFormFeed vbLf vbNewLine vbNullChar vbTab vbVerticalTab " +
            "vbNullString vbObjectError");
        AddMembers(members, "KeyCodeConstants", VbaStandardLibraryDeclaredTypeCategory.FixedScalar,
            "vbKeyLButton vbKeyRButton vbKeyCancel vbKeyMButton vbKeyBack vbKeyTab vbKeyClear " +
            "vbKeyReturn vbKeyShift vbKeyControl vbKeyMenu vbKeyPause vbKeyCapital vbKeyEscape " +
            "vbKeySpace vbKeyPageUp vbKeyPageDown vbKeyEnd vbKeyHome vbKeyLeft vbKeyUp " +
            "vbKeyRight vbKeyDown vbKeySelect vbKeyPrint vbKeyExecute vbKeySnapshot " +
            "vbKeyInsert vbKeyDelete vbKeyHelp vbKeyNumlock vbKeyMultiply vbKeyAdd " +
            "vbKeySeparator vbKeySubtract vbKeyDecimal vbKeyDivide");
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            members.Add(Scalar("KeyCodeConstants", $"vbKey{letter}"));
        }

        for (var digit = 0; digit <= 9; digit++)
        {
            members.Add(Scalar("KeyCodeConstants", $"vbKey{digit}"));
            members.Add(Scalar("KeyCodeConstants", $"vbKeyNumpad{digit}"));
        }

        for (var functionKey = 1; functionKey <= 16; functionKey++)
        {
            members.Add(Scalar("KeyCodeConstants", $"vbKeyF{functionKey}"));
        }

        AddMembers(members, "SystemColorConstants", VbaStandardLibraryDeclaredTypeCategory.FixedScalar,
            "vbScrollBars vbDesktop vbActiveTitleBar vbInactiveTitleBar vbMenuBar " +
            "vbWindowBackground vbWindowFrame vbMenuText vbWindowText vbTitleBarText " +
            "vbActiveBorder vbInactiveBorder vbApplicationWorkspace vbHighlight " +
            "vbHighlightText vbButtonFace vbButtonShadow vbGrayText vbButtonText " +
            "vbInactiveCaptionText vb3DHighlight vb3DDKShadow vb3DLight vb3DFace vb3Dshadow " +
            "vbInfoText vbInfoBackground");

        return members.AsReadOnly();
    }

    private static IReadOnlyList<VbaStandardLibraryMemberSyntaxFact>
        CreateGlobalNamespaceAndObjectFixedValueFacts()
        => Array.AsReadOnly(new[]
        {
            GlobalNamespaceAndObjectScalar("Err", "Description"),
            GlobalNamespaceAndObjectScalar("Err", "HelpContext"),
            GlobalNamespaceAndObjectScalar("Err", "HelpFile"),
            GlobalNamespaceAndObjectScalar("Err", "LastDllError"),
            GlobalNamespaceAndObjectScalar("Err", "Number"),
            GlobalNamespaceAndObjectScalar("Err", "Source")
        });

    private static IReadOnlyList<VbaStandardLibraryMemberSyntaxFact> CreateNoValueMemberFacts()
        => Array.AsReadOnly(new[]
        {
            Sub("FileSystem", "ChDir", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("FileSystem", "ChDrive", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("FileSystem", "FileCopy", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("FileSystem", "Kill", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("FileSystem", "MkDir", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("FileSystem", "RmDir", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("FileSystem", "Reset", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("FileSystem", "SetAttr", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Interaction", "AppActivate", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Interaction", "Beep", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Interaction", "DeleteSetting", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Interaction", "SaveSetting", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Interaction", "SendKeys", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Math", "Randomize", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Global", "Load", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Global", "Unload", VbaStandardLibraryMemberAccessKind.GlobalNamespace),
            Sub("Err", "Clear", VbaStandardLibraryMemberAccessKind.GlobalNamespaceAndObject),
            Sub("Err", "Raise", VbaStandardLibraryMemberAccessKind.GlobalNamespaceAndObject)
        });

    private static IReadOnlyList<VbaStandardLibraryMemberSyntaxFact>
        CreateSupplementalGlobalNonReceiverFacts()
        => Array.AsReadOnly(new[]
        {
            new VbaStandardLibraryMemberSyntaxFact(
                null,
                "Width",
                VbaStandardLibraryMemberAccessKind.HiddenGlobalNamespace,
                VbaStandardLibraryDeclaredTypeCategory.NoValue),
            new VbaStandardLibraryMemberSyntaxFact(
                null,
                "VarPtr",
                VbaStandardLibraryMemberAccessKind.HiddenGlobalNamespace,
                VbaStandardLibraryDeclaredTypeCategory.FixedScalar),
            new VbaStandardLibraryMemberSyntaxFact(
                null,
                "StrPtr",
                VbaStandardLibraryMemberAccessKind.HiddenGlobalNamespace,
                VbaStandardLibraryDeclaredTypeCategory.FixedScalar),
            new VbaStandardLibraryMemberSyntaxFact(
                null,
                "ObjPtr",
                VbaStandardLibraryMemberAccessKind.HiddenGlobalNamespace,
                VbaStandardLibraryDeclaredTypeCategory.FixedScalar),
            new VbaStandardLibraryMemberSyntaxFact(
                null,
                "LBound",
                VbaStandardLibraryMemberAccessKind.CompilerIntrinsic,
                VbaStandardLibraryDeclaredTypeCategory.FixedScalar),
            new VbaStandardLibraryMemberSyntaxFact(
                null,
                "UBound",
                VbaStandardLibraryMemberAccessKind.CompilerIntrinsic,
                VbaStandardLibraryDeclaredTypeCategory.FixedScalar),
            new VbaStandardLibraryMemberSyntaxFact(
                null,
                "Spc",
                VbaStandardLibraryMemberAccessKind.CompilerIntrinsic,
                VbaStandardLibraryDeclaredTypeCategory.NoValue),
            new VbaStandardLibraryMemberSyntaxFact(
                null,
                "Tab",
                VbaStandardLibraryMemberAccessKind.CompilerIntrinsic,
                VbaStandardLibraryDeclaredTypeCategory.NoValue)
        });

    private static void AddMembers(
        ICollection<VbaStandardLibraryMemberSyntaxFact> members,
        string ownerName,
        VbaStandardLibraryDeclaredTypeCategory category,
        string memberNames)
    {
        foreach (var memberName in memberNames.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            members.Add(new(
                ownerName,
                memberName,
                VbaStandardLibraryMemberAccessKind.GlobalNamespace,
                category));
        }
    }
}

internal readonly record struct VbaStandardLibraryMemberSyntaxFact(
    string? OwnerName,
    string MemberName,
    VbaStandardLibraryMemberAccessKind AccessKind,
    VbaStandardLibraryDeclaredTypeCategory DeclaredTypeCategory);

internal sealed record VbaStandardLibraryPotentialReceiverMemberSyntaxFact(
    string? OwnerName,
    string MemberName,
    VbaStandardLibraryPotentialReceiverMemberKind Kind,
    VbaStandardLibraryPotentialReceiverDeclaredTypeCategory DeclaredTypeCategory,
    IReadOnlyList<VbaStandardLibraryParameterSyntaxFact> Parameters);

internal readonly record struct VbaStandardLibraryParameterSyntaxFact(
    string Name,
    bool IsOptional,
    bool IsParamArray);

internal enum VbaStandardLibraryPotentialReceiverMemberKind
{
    Function,
    Property
}

internal enum VbaStandardLibraryPotentialReceiverDeclaredTypeCategory
{
    Variant,
    NamedObject
}

internal enum VbaStandardLibraryMemberReceiverClassification
{
    Unknown,
    PotentialReceiver,
    NonReceiver
}

internal enum VbaStandardLibraryMemberAccessKind
{
    GlobalNamespace,
    GlobalNamespaceAndObject,
    HiddenGlobalNamespace,
    CompilerIntrinsic
}

internal enum VbaStandardLibraryDeclaredTypeCategory
{
    FixedScalar,
    FixedEnum,
    NoValue
}
