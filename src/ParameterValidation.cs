namespace SharpLensMcp;

// The shared validators for tool parameters whose JSON type is right but whose VALUE is
// out of the documented domain. Each returns null when the value is valid, or the
// INVALID_PARAMETER error a tool returns as-is (RoslynService.CreateErrorResponse for a
// RoslynError) — never a silent clamp, default or empty result.
//
// The rules:
//   - Limit: every count/size bound (maxResults, topN, maxNodes, maxDepth, maxHops,
//     maxMembers, ...) is an integer in [minimum, maximum], minimum 1 unless the tool
//     documents another — a limit of 0 or less can only return nothing, so it is a
//     caller error, not an empty page.
//   - Offset: a paging offset is an integer >= 0.
//   - Range: a numeric parameter (e.g. a 0-1 similarity threshold) is within
//     [minimum, maximum], both inclusive.
//   - Choice: an enumerated string parameter matches one of the documented values
//     case-insensitively; the matched documented spelling is returned so the tool
//     compares against one canonical form. A null value is absent (the tool applies its
//     default); any other value, the empty string included, must match.
// Messages name the parameter and the allowed domain; Context carries
// { parameter, value, ... } so a client can correct the call mechanically.
internal static class ParameterValidation
{
    public static RoslynError? Limit(string parameter, int value, int maximum, int minimum = 1)
    {
        if (value >= minimum && value <= maximum) return null;
        return new RoslynError
        {
            Code = ErrorCodes.InvalidParameter,
            Message = $"{parameter} must be between {minimum} and {maximum} (got {value})",
            Hint = $"Pass {parameter} as an integer from {minimum} to {maximum}.",
            Context = new { parameter, value, minimum, maximum }
        };
    }

    // A nullable limit: null (absent) is valid, the tool applies its default.
    public static RoslynError? Limit(string parameter, int? value, int maximum, int minimum = 1) =>
        value is { } present ? Limit(parameter, present, maximum, minimum) : null;

    public static RoslynError? Offset(string parameter, int value)
    {
        if (value >= 0) return null;
        return new RoslynError
        {
            Code = ErrorCodes.InvalidParameter,
            Message = $"{parameter} must be 0 or greater (got {value})",
            Hint = $"Pass {parameter} as 0 for the first page, or the nextOffset of the previous page.",
            Context = new { parameter, value, minimum = 0 }
        };
    }

    public static RoslynError? Range(string parameter, double value, double minimum, double maximum)
    {
        if (value >= minimum && value <= maximum) return null;
        return new RoslynError
        {
            Code = ErrorCodes.InvalidParameter,
            Message = $"{parameter} must be between {Format(minimum)} and {Format(maximum)} (got {Format(value)})",
            Hint = $"Pass {parameter} as a number from {Format(minimum)} to {Format(maximum)}.",
            Context = new { parameter, value, minimum, maximum }
        };
    }

    public static RoslynError? Choice(
        string parameter, string? value, IReadOnlyList<string> allowedValues, out string? canonical)
    {
        canonical = null;
        if (value == null) return null;
        canonical = allowedValues.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
        if (canonical != null) return null;
        return new RoslynError
        {
            Code = ErrorCodes.InvalidParameter,
            Message = $"{parameter} must be one of: {string.Join(", ", allowedValues)} (got '{value}')",
            Hint = $"Pass {parameter} as one of: {string.Join(", ", allowedValues)} (case-insensitive).",
            Context = new { parameter, value, allowedValues }
        };
    }

    private static string Format(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
