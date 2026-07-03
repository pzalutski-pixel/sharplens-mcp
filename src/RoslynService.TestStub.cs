using Microsoft.CodeAnalysis;
using System.Text;

namespace SharpLensMcp;

// generate_test_stub: a compilable test skeleton for a target method — pairs
// with find_untested_code. The framework is detected from the solution's test
// projects (xUnit's, NUnit's, or MSTest's attribute types resolving in a
// project's compilation); the SUT is constructed via the public constructor
// with the fewest parameters; interface-typed arguments become TODO-marked
// default! placeholders the agent substitutes. Generation-only by design —
// returns code, never writes.
public partial class RoslynService
{
    public async Task<object> GenerateTestStubAsync(
        string typeName, string methodName, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (symbol, symbolError) = await ResolveSymbolByNameAsync(
            typeName, methodName, overloadIndex, cancellationToken);
        if (symbol == null) return symbolError!;
        if (symbol is not IMethodSymbol method || method.MethodKind != MethodKind.Ordinary)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                $"'{symbol.ToDisplayString()}' is not an ordinary method",
                hint: "generate_test_stub targets callable methods; properties and constructors are not supported.",
                context: new { typeName, methodName });
        }

        var sutType = method.ContainingType;
        var (framework, testProject) = await DetectTestFrameworkAsync(cancellationToken);

        var (attribute, classAttribute, frameworkUsing) = framework switch
        {
            "nunit" => ("[Test]", "[TestFixture]", "NUnit.Framework"),
            "mstest" => ("[TestMethod]", "[TestClass]", "Microsoft.VisualStudio.TestTools.UnitTesting"),
            _ => ("[Fact]", null, "Xunit")
        };

        // The value a test captures is what the method yields AFTER awaiting (if
        // awaitable): the GetAwaiter().GetResult() return type. A custom non-generic
        // awaitable can still produce a value, so key off that result type rather
        // than assuming any non-generic awaitable (like Task/ValueTask) is void.
        var awaitedResult = AwaitedResultType(method.ReturnType);
        var returnsAwaitable = awaitedResult != null;
        var effectiveResultType = returnsAwaitable ? awaitedResult! : method.ReturnType;
        var returnsValue = effectiveResultType.SpecialType != SpecialType.System_Void;

        var code = new StringBuilder();
        code.AppendLine($"using {frameworkUsing};");
        // The SUT and every generated type reference are global::-qualified (TypeRef), so the
        // stub needs only the framework using (for the [Fact]/[Test]/[TestMethod] attribute),
        // not a using for the SUT's namespace.
        var sutNamespace = sutType.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString()
            : null;
        code.AppendLine();

        var testNamespace = testProject?.DefaultNamespace ?? $"{sutNamespace ?? "Generated"}.Tests";
        code.AppendLine($"namespace {testNamespace};");
        code.AppendLine();
        if (classAttribute != null) code.AppendLine(classAttribute);
        var testClassName = $"{sutType.Name}Tests";
        code.AppendLine($"public class {testClassName}");
        code.AppendLine("{");
        code.AppendLine($"    {attribute}");
        code.AppendLine($"    public {(returnsAwaitable ? "async global::System.Threading.Tasks.Task" : "void")} {method.Name}_Behavior()");
        code.AppendLine("    {");

        // Build the call arguments first, collecting locals that ref parameters
        // need (a ref argument must be a writable variable, not a literal).
        var refLocals = new List<string>();
        string ArgFor(IParameterSymbol parameter)
        {
            var argName = EscapeIdentifier(parameter.Name);
            if (parameter.RefKind == RefKind.Out) return $"{argName}: out _";
            if (parameter.RefKind == RefKind.Ref)
            {
                var localName = EscapeIdentifier($"{parameter.Name}Ref");
                // Fully qualify: the stub imports only the SUT and framework namespaces, so
                // a MinimallyQualified ref-parameter type from any other namespace would not
                // resolve (CS0246) — the same slip the constraint-type rendering avoids.
                refLocals.Add($"        {TypeRef(parameter.Type)} {localName} = {PlaceholderValueFor(parameter.Type)};");
                return $"{argName}: ref {localName}";
            }
            // `in` parameters accept a value without the keyword, so they need no special handling.
            return $"{argName}: {PlaceholderValueFor(parameter.Type)}";
        }

        string invocationTarget;
        string? ctorDeclaration = null;
        if (method.IsStatic)
        {
            invocationTarget = TypeRef(sutType);
        }
        else
        {
            var constructor = sutType.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                .OrderBy(c => c.Parameters.Length)
                .FirstOrDefault();
            if (constructor == null)
            {
                return CreateErrorResponse(
                    ErrorCodes.AnalysisFailed,
                    $"'{sutType.ToDisplayString()}' has no public constructor to arrange the test with",
                    hint: "Use get_instantiation_options to find a factory, then adapt the stub manually.",
                    context: new { typeName });
            }
            var ctorArgs = string.Join(", ", constructor.Parameters.Select(ArgFor));
            ctorDeclaration = $"        var sut = new {TypeRef(sutType)}({ctorArgs});";
            invocationTarget = "sut";
        }

        var methodArgs = string.Join(", ", method.Parameters.Select(ArgFor));
        // A generic method needs explicit type arguments at the call site (inference
        // can't reach a parameter-less type parameter) or the stub won't compile.
        var typeArgs = method.IsGenericMethod
            ? "<" + string.Join(", ", method.TypeParameters.Select(StubTypeArgument)) + ">"
            : "";

        code.AppendLine("        // Arrange");
        foreach (var local in refLocals) code.AppendLine(local);
        if (ctorDeclaration != null) code.AppendLine(ctorDeclaration);
        code.AppendLine();
        code.AppendLine("        // Act");
        var awaitPrefix = returnsAwaitable ? "await " : "";
        var capturePrefix = returnsValue ? "var result = " : "";
        code.AppendLine($"        {capturePrefix}{awaitPrefix}{invocationTarget}.{method.Name}{typeArgs}({methodArgs});");
        code.AppendLine();
        code.AppendLine("        // Assert");
        code.AppendLine("        // TODO: assert the expected behavior");
        code.AppendLine("    }");
        code.AppendLine("}");

        var suggestedFilePath = SuggestTestFilePath(testProject, testClassName);

        return CreateSuccessResponse(
            data: new
            {
                appliesEditsAutomatically = false,
                framework,
                frameworkDetected = testProject != null,
                testClassName,
                testCode = code.ToString(),
                suggestedFilePath,
                targetMethod = method.ToDisplayString()
            },
            suggestedNextTools: new[]
            {
                testProject != null
                    ? $"validate_code with a contextFilePath from {testProject.Name} to confirm the stub compiles"
                    : "No test project found - the stub defaults to xUnit; add the framework reference first",
                "Write the stub to suggestedFilePath (or merge into the existing test class), fill in the TODOs"
            });
    }

    // The first project whose compilation resolves a known test-framework
    // attribute type decides the framework. Checked in xUnit, NUnit, MSTest
    // order within each project.
    private async Task<(string framework, Project? testProject)> DetectTestFrameworkAsync(
        CancellationToken cancellationToken)
    {
        foreach (var project in _solution!.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;
            if (compilation.GetTypeByMetadataName("Xunit.FactAttribute") != null)
                return ("xunit", project);
            if (compilation.GetTypeByMetadataName("NUnit.Framework.TestAttribute") != null)
                return ("nunit", project);
            if (compilation.GetTypeByMetadataName("Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute") != null)
                return ("mstest", project);
        }
        return ("xunit", null);
    }

    // Alongside an existing test file for the type when one exists, else a
    // new file in the test project's root, else (no test project) a relative name.
    private string? SuggestTestFilePath(Project? testProject, string testClassName)
    {
        if (testProject == null) return $"{testClassName}.cs";

        var existing = testProject.Documents.FirstOrDefault(d =>
            d.Name.Equals($"{testClassName}.cs", StringComparison.OrdinalIgnoreCase));
        if (existing?.FilePath != null) return FormatPath(existing.FilePath);

        var projectDir = Path.GetDirectoryName(testProject.FilePath);
        return projectDir == null
            ? $"{testClassName}.cs"
            : FormatPath(Path.Combine(projectDir, $"{testClassName}.cs"));
    }

    // value types -> default, strings -> "", interfaces -> a TODO the agent
    // must substitute, other references -> default!.
    private static string PlaceholderValueFor(ITypeSymbol type) => type switch
    {
        { TypeKind: TypeKind.Interface } => $"/* TODO: substitute {type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} */ default!",
        { SpecialType: SpecialType.System_String } => "\"\"",
        { IsValueType: true } => "default",
        _ => "default!"
    };

    // The value produced by awaiting the type — the return type of
    // GetAwaiter().GetResult() — or null when the type is not awaitable. A
    // result-less awaitable (Task/ValueTask) yields the void type; Task<T> and a
    // custom awaitable yield their actual result type. Covers the await pattern (an
    // accessible parameterless GetAwaiter whose result has IsCompleted + GetResult);
    // extension-method GetAwaiter is not detected.
    private static ITypeSymbol? AwaitedResultType(ITypeSymbol type)
    {
        var getAwaiter = type.GetMembers("GetAwaiter").OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.IsEmpty && !m.ReturnsVoid
                && m.DeclaredAccessibility == Accessibility.Public);
        if (getAwaiter == null) return null;
        var awaiter = getAwaiter.ReturnType;
        if (!awaiter.GetMembers("IsCompleted").OfType<IPropertySymbol>().Any()) return null;
        var getResult = awaiter.GetMembers("GetResult").OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.IsEmpty);
        return getResult?.ReturnType;
    }

    // A concrete type argument for a generic method's type parameter: int for a
    // struct constraint, the first explicit constraint type if present, else object.
    private static string StubTypeArgument(ITypeParameterSymbol tp)
    {
        if (tp.HasValueTypeConstraint) return "int";
        var constraint = tp.ConstraintTypes.FirstOrDefault();
        // Fully qualify the constraint type: the stub only imports the SUT and framework
        // namespaces, so a MinimallyQualified name from any other namespace would not
        // resolve (CS0246). global::-qualified names need no using.
        return constraint != null
            ? TypeRef(constraint)
            : "object";
    }

    // A named-argument name must be @-escaped when the parameter name is a reserved
    // keyword (e.g. a parameter literally named `class`), or the call won't compile.
    private static string EscapeIdentifier(string name) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name)
            != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name
            : name;
}
