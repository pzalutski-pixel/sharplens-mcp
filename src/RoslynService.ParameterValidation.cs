namespace SharpLensMcp;

// The envelope a tool returns for a ParameterValidation error.
public partial class RoslynService
{
    // The error envelope for a prepared RoslynError, such as a ParameterValidation result:
    //   if (ParameterValidation.Limit("maxResults", maxResults, 500) is { } invalid)
    //       return CreateErrorResponse(invalid);
    internal static object CreateErrorResponse(RoslynError error) => new ToolErrorResponse(success: false, error: error);
}
