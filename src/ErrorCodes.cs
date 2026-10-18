namespace SharpLensMcp;

/// <summary>
/// Error codes for consistent error handling across all tools.
/// </summary>
public static class ErrorCodes
{
    public const string SolutionNotLoaded = "SOLUTION_NOT_LOADED";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileNotInSolution = "FILE_NOT_IN_SOLUTION";
    public const string SymbolNotFound = "SYMBOL_NOT_FOUND";
    public const string TypeNotFound = "TYPE_NOT_FOUND";
    public const string NotAType = "NOT_A_TYPE";
    public const string NotAMethod = "NOT_A_METHOD";
    public const string AnalysisFailed = "ANALYSIS_FAILED";
    public const string InvalidParameter = "INVALID_PARAMETER";
    public const string AmbiguousMatch = "AMBIGUOUS_MATCH";
    public const string AmbiguousType = "AMBIGUOUS_TYPE";
    // An apply's target file changed on disk since the edit was computed from it; the
    // whole apply is refused so the disk edit is never overwritten.
    public const string StaleDocument = "STALE_DOCUMENT";
    // An apply's target file does not decode losslessly in the encoding it was read with (no
    // byte-order mark, not valid UTF-8, and a fallback code page that cannot represent its
    // bytes), so writing any edit would alter text outside the edit; the apply is refused.
    public const string UnsupportedEncoding = "UNSUPPORTED_ENCODING";
    public const string InternalError = "INTERNAL_ERROR";
    public const string Timeout = "TIMEOUT";
}
