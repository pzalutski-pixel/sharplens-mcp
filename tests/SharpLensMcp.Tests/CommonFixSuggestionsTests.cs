using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

public class CommonFixSuggestionsTests
{
    [Theory]
    [InlineData("CS0168", "Remove unused variable", "Use the variable", "Prefix with underscore to indicate intentionally unused")]
    [InlineData("CS0219", "Remove unused variable", "Use the variable in an expression")]
    [InlineData("CS1998", "Add await keyword to async operation", "Remove async modifier if method doesn't need to be async", "Return Task.CompletedTask or Task.FromResult()")]
    [InlineData("CS0162", "Remove unreachable code", "Fix control flow logic")]
    [InlineData("CS0649", "Initialize the field", "Remove unused field", "Mark as obsolete if legacy code")]
    [InlineData("CS8019", "Remove unnecessary using directive", "Run 'Organize Usings'")]
    [InlineData("CS0246", "Add missing using directive", "Check type name spelling", "Add assembly reference")]
    [InlineData("CS0103", "Add missing using directive", "Check name spelling", "Declare the variable or method")]
    [InlineData("CS4012", "Move Utf8JsonReader to non-async context", "Use synchronous JSON parsing", "Wrap in Task.Run() for async operation")]
    [InlineData("CS1503", "Cast argument to expected type", "Change parameter type", "Fix argument expression")]
    public void Returns_DocumentedSuggestions_ForKnownDiagnosticIds(string diagnosticId, params string[] expectedSuggestions)
    {
        var service = new RoslynService();
        var suggestions = service.GetCommonFixSuggestions(diagnosticId, "irrelevant message");
        suggestions.Should().Equal(expectedSuggestions,
            $"GetCommonFixSuggestions must return the exact documented list for {diagnosticId} in declaration order");
    }

    [Fact]
    public void Returns_GenericFallback_ForUnknownDiagnosticId()
    {
        var service = new RoslynService();
        var suggestions = service.GetCommonFixSuggestions("CS9999", "any message");
        suggestions.Should().Equal(new[]
        {
            "Review diagnostic message for fix guidance",
            "Consult C# documentation for CS9999"
        }, "the default branch must echo the requested diagnosticId into the second suggestion");
    }

    [Fact]
    public void Fallback_EchoesDiagnosticId_VerbatimEvenForNonStandardShapes()
    {
        var service = new RoslynService();
        var suggestions = service.GetCommonFixSuggestions("CUSTOM_RULE", "any");
        suggestions[1].Should().Be("Consult C# documentation for CUSTOM_RULE");
    }
}
