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
    public const string Timeout = "TIMEOUT";
}
