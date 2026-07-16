namespace VbaLanguageServer.Syntax;

/// <summary>
/// Validates the implementation-independent DATE token grammar and static ranges from MS-VBAL.
/// </summary>
internal static class VbaDateLiteralSyntax
{
    private static readonly string[] MonthNames =
    [
        "january", "february", "march", "april", "may", "june",
        "july", "august", "september", "october", "november", "december"
    ];

    /// <summary>
    /// Determines whether a token contains one complete, valid VBA date-or-time literal.
    /// </summary>
    public static bool IsComplete(VbaToken token)
    {
        if (token.Kind != VbaTokenKind.DateLiteral
            || token.Text.Length <= 2
            || token.Text[0] != '#'
            || token.Text[^1] != '#')
        {
            return false;
        }

        var value = TrimWhitespace(token.Text.AsSpan(1, token.Text.Length - 2));
        if (value.IsEmpty)
        {
            return false;
        }

        if (TryParseDate(value) || TryParseTime(value))
        {
            return true;
        }

        for (var index = 1; index < value.Length - 1; index++)
        {
            if (!IsWhitespace(value[index]) || IsWhitespace(value[index - 1]))
            {
                continue;
            }

            var rightStart = index;
            while (rightStart < value.Length && IsWhitespace(value[rightStart]))
            {
                rightStart++;
            }

            if (rightStart < value.Length
                && TryParseDate(value[..index])
                && TryParseTime(value[rightStart..]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseDate(ReadOnlySpan<char> value)
    {
        value = TrimWhitespace(value);
        Span<DateComponent> components = stackalloc DateComponent[3];
        var count = 0;
        var index = 0;
        while (true)
        {
            if (count == components.Length
                || !TryReadDateComponent(value, ref index, out components[count]))
            {
                return false;
            }

            count++;
            if (index == value.Length)
            {
                break;
            }

            if (!TryReadDateSeparator(value, ref index))
            {
                return false;
            }
        }

        var monthNameCount = 0;
        for (var componentIndex = 0; componentIndex < count; componentIndex++)
        {
            monthNameCount += components[componentIndex].IsMonthName ? 1 : 0;
        }

        if (count is < 2 or > 3 || monthNameCount > 1)
        {
            return false;
        }

        return count == 2
            ? IsValidTwoPartDate(components[0], components[1])
            : IsValidThreePartDate(components[0], components[1], components[2]);
    }

    private static bool IsValidTwoPartDate(DateComponent left, DateComponent middle)
    {
        if (!left.IsMonthName && !middle.IsMonthName)
        {
            return IsPotentialDay(left.Value, middle.Value)
                || IsPotentialDay(middle.Value, left.Value)
                || IsMonth(left.Value) && IsValidYear(middle.Value)
                || IsMonth(middle.Value) && IsValidYear(left.Value);
        }

        var month = left.IsMonthName ? left.Value : middle.Value;
        var number = left.IsMonthName ? middle.Value : left.Value;
        return IsPotentialDay(month, number) || IsValidYear(number);
    }

    private static bool IsValidThreePartDate(
        DateComponent left,
        DateComponent middle,
        DateComponent right)
    {
        if (!left.IsMonthName && !middle.IsMonthName && !right.IsMonthName)
        {
            return IsLegalDate(left.Value, middle.Value, right.Value)
                || IsLegalDate(middle.Value, right.Value, left.Value)
                || IsLegalDate(middle.Value, left.Value, right.Value);
        }

        var month = left.IsMonthName
            ? left.Value
            : middle.IsMonthName
                ? middle.Value
                : right.Value;
        Span<int> numbers = stackalloc int[2];
        var numberIndex = 0;
        if (!left.IsMonthName)
        {
            numbers[numberIndex++] = left.Value;
        }

        if (!middle.IsMonthName)
        {
            numbers[numberIndex++] = middle.Value;
        }

        if (!right.IsMonthName)
        {
            numbers[numberIndex] = right.Value;
        }

        return IsLegalDate(month, numbers[0], numbers[1])
            || IsLegalDate(month, numbers[1], numbers[0]);
    }

    private static bool IsLegalDate(int month, int day, int sourceYear)
    {
        if (!TryMapYear(sourceYear, out var year)
            || !IsMonth(month)
            || day <= 0)
        {
            return false;
        }

        return day <= DateTime.DaysInMonth(year, month);
    }

    private static bool IsPotentialDay(int month, int day)
        => IsMonth(month)
            && day > 0
            && day <= DateTime.DaysInMonth(2000, month);

    private static bool IsMonth(int value)
        => value is >= 1 and <= 12;

    private static bool IsValidYear(int value)
        => TryMapYear(value, out _);

    private static bool TryMapYear(int value, out int year)
    {
        year = value switch
        {
            >= 0 and <= 29 => 2000 + value,
            >= 30 and <= 99 => 1900 + value,
            >= 100 and <= 9999 => value,
            _ => 0
        };
        return year != 0;
    }

    private static bool TryParseTime(ReadOnlySpan<char> value)
    {
        value = TrimWhitespace(value);
        var index = 0;
        if (!TryReadDecimal(value, ref index, out var hour) || hour > 23)
        {
            return false;
        }

        SkipWhitespace(value, ref index);
        var suffixStart = index;
        if (TryReadAmPm(value, ref index))
        {
            return index == value.Length;
        }

        index = suffixStart;
        if (index >= value.Length || value[index] is not (':' or '.'))
        {
            return false;
        }

        index++;
        SkipWhitespace(value, ref index);
        if (!TryReadDecimal(value, ref index, out var minute) || minute > 59)
        {
            return false;
        }

        SkipWhitespace(value, ref index);
        if (index < value.Length && value[index] is ':' or '.')
        {
            index++;
            SkipWhitespace(value, ref index);
            if (!TryReadDecimal(value, ref index, out var second) || second > 59)
            {
                return false;
            }

            SkipWhitespace(value, ref index);
        }

        if (index < value.Length && !TryReadAmPm(value, ref index))
        {
            return false;
        }

        return index == value.Length;
    }

    private static bool TryReadDateComponent(
        ReadOnlySpan<char> value,
        ref int index,
        out DateComponent component)
    {
        component = default;
        if (TryReadDecimal(value, ref index, out var numericValue))
        {
            component = new DateComponent(numericValue, IsMonthName: false);
            return true;
        }

        var start = index;
        while (index < value.Length && char.IsAsciiLetter(value[index]))
        {
            index++;
        }

        if (start == index || !TryGetMonth(value[start..index], out var month))
        {
            return false;
        }

        component = new DateComponent(month, IsMonthName: true);
        return true;
    }

    private static bool TryReadDateSeparator(ReadOnlySpan<char> value, ref int index)
    {
        var start = index;
        SkipWhitespace(value, ref index);
        var hadWhitespace = index > start;
        if (index < value.Length && value[index] is '/' or '-' or ',')
        {
            index++;
            SkipWhitespace(value, ref index);
            return true;
        }

        return hadWhitespace;
    }

    private static bool TryReadDecimal(
        ReadOnlySpan<char> value,
        ref int index,
        out int result)
    {
        result = 0;
        var start = index;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
        {
            var digit = value[index] - '0';
            if (result > (9999 - digit) / 10)
            {
                return false;
            }

            result = result * 10 + digit;
            index++;
        }

        return index > start;
    }

    private static bool TryReadAmPm(ReadOnlySpan<char> value, ref int index)
    {
        if (index >= value.Length || value[index] is not ('A' or 'a' or 'P' or 'p'))
        {
            return false;
        }

        index++;
        if (index < value.Length && value[index] is 'M' or 'm')
        {
            index++;
        }

        return true;
    }

    private static bool TryGetMonth(ReadOnlySpan<char> value, out int month)
    {
        for (var index = 0; index < MonthNames.Length; index++)
        {
            var name = MonthNames[index];
            if (value.Equals(name, StringComparison.OrdinalIgnoreCase)
                || name.Length > 3
                && value.Equals(name.AsSpan(0, 3), StringComparison.OrdinalIgnoreCase))
            {
                month = index + 1;
                return true;
            }
        }

        month = 0;
        return false;
    }

    private static ReadOnlySpan<char> TrimWhitespace(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && IsWhitespace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsWhitespace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static void SkipWhitespace(ReadOnlySpan<char> value, ref int index)
    {
        while (index < value.Length && IsWhitespace(value[index]))
        {
            index++;
        }
    }

    private static bool IsWhitespace(char value)
        => value is ' ' or '\t' || char.IsWhiteSpace(value);

    private readonly record struct DateComponent(int Value, bool IsMonthName);
}
