using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Text;

namespace SharpLensMcp;

// generate_test_stub: a compilable test skeleton for a target method — pairs with
// find_untested_code. The stub is written for a test project that can call the system
// under test: a project that references the SUT's project (directly or through other
// projects), else the SUT's own project, whichever first has a test framework (xUnit's,
// NUnit's, MSTest's or TUnit's test attribute type resolving in its compilation). A
// target that project cannot call (private, protected, or internal without
// InternalsVisibleTo) is refused. The SUT is constructed via the public constructor
// with the fewest parameters; a generic SUT or method is closed over type arguments that
// satisfy their constraints; interface-typed arguments become TODO-marked default!
// placeholders and every ref argument gets a local of its own. Generation-only by design
// — returns code, never writes.
public partial class RoslynService
{
    public async Task<object> GenerateTestStubAsync(
        string typeName, string methodName, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var resolution = await ResolveMemberByNameAsync(typeName, methodName, overloadIndex, cancellationToken);
        if (resolution.Symbol is not { } symbol) return resolution.Error!;
        if (symbol is not IMethodSymbol method || method.MethodKind != MethodKind.Ordinary)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                $"'{symbol.ToDisplayString()}' is not an ordinary method",
                hint: "generate_test_stub targets callable methods; properties and constructors are not supported.",
                context: new { typeName, methodName });
        }

        var sutType = method.ContainingType;
        var sutProject = sutType.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree is { } sutTree
            ? _solution!.GetDocument(sutTree)?.Project
            : null;
        var (framework, testProject) = await DetectTestFrameworkAsync(sutProject, cancellationToken);

        // The test project must be able to call the target.
        var testCompilation = testProject == null ? null : await testProject.GetCompilationAsync(cancellationToken);
        if (InaccessibleTarget(method, testCompilation) is { } inaccessible)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{method.ToDisplayString()}' is not accessible from {testProject?.Name ?? "a test project"}: it is {inaccessible}",
                hint: inaccessible.Contains("internal")
                    ? "Test it through the accessible members that use it, or grant the test project InternalsVisibleTo."
                    : "Test it through the accessible members that use it.",
                context: new { typeName, methodName, testProject = testProject?.Name });
        }

        var (attribute, classAttribute, frameworkUsing) = framework switch
        {
            "nunit" => ("[Test]", "[TestFixture]", "NUnit.Framework"),
            "mstest" => ("[TestMethod]", "[TestClass]", "Microsoft.VisualStudio.TestTools.UnitTesting"),
            "tunit" => ("[Test]", null, "TUnit.Core"),
            _ => ("[Fact]", null, "Xunit")
        };

        // Concrete type arguments for a generic SUT and a generic method, each satisfying
        // its type parameter's constraints.
        var compilation = (testCompilation ?? (sutProject == null ? null : await sutProject.GetCompilationAsync(cancellationToken)))!;
        var typeArguments = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        var sutTypeArguments = sutType.TypeParameters.Select(tp => ChooseTypeArgument(tp, typeArguments, compilation)).ToList();
        var closedSut = sutTypeArguments.Count > 0 ? sutType.Construct(sutTypeArguments.ToArray()) : sutType;
        var methodTypeArguments = method.TypeParameters.Select(tp => ChooseTypeArgument(tp, typeArguments, compilation)).ToList();

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
        // stub needs only the framework using (for the test attribute), not a using for the
        // SUT's namespace.
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
        // need (a ref argument must be a writable variable, not a literal). Every
        // local name is unique in the test method.
        var refLocals = new List<string>();
        var localNames = new HashSet<string>(StringComparer.Ordinal) { "sut", "result" };
        string ArgFor(IParameterSymbol parameter)
        {
            var argName = EscapeIdentifier(parameter.Name);
            if (parameter.RefKind == RefKind.Out) return $"{argName}: out _";
            if (parameter.RefKind == RefKind.Ref)
            {
                var baseName = $"{parameter.Name}Ref";
                var localName = baseName;
                for (var i = 2; !localNames.Add(localName); i++) localName = $"{baseName}{i}";
                // Fully qualify: the stub imports only the framework namespace, so a
                // MinimallyQualified ref-parameter type from any other namespace would not
                // resolve (CS0246).
                refLocals.Add($"        {TypeRef(parameter.Type)} {EscapeIdentifier(localName)} = {PlaceholderValueFor(parameter.Type)};");
                return $"{argName}: ref {EscapeIdentifier(localName)}";
            }
            // `in` parameters accept a value without the keyword, so they need no special handling.
            return $"{argName}: {PlaceholderValueFor(parameter.Type)}";
        }

        string invocationTarget;
        string? ctorDeclaration = null;
        if (method.IsStatic)
        {
            invocationTarget = TypeRef(closedSut);
        }
        else
        {
            var constructor = closedSut.InstanceConstructors
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
            ctorDeclaration = $"        var sut = new {TypeRef(closedSut)}({ctorArgs});";
            invocationTarget = "sut";
        }

        var closedMethod = method.TypeParameters.Length > 0 ? method.Construct(methodTypeArguments.ToArray()) : method;
        var methodArgs = string.Join(", ", closedMethod.Parameters.Select(ArgFor));
        // A generic method needs explicit type arguments at the call site (inference
        // can't reach a parameter-less type parameter) or the stub won't compile.
        var typeArgs = method.IsGenericMethod
            ? "<" + string.Join(", ", methodTypeArguments.Select(TypeRef)) + ">"
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
                targetMethod = method.ToDisplayString(),
                notice = resolution.Type?.ShadowNotice
            },
            suggestedNextTools: new[]
            {
                testProject != null
                    ? $"validate_code with a contextFilePath from {testProject.Name} to confirm the stub compiles"
                    : "No test project found - the stub defaults to xUnit; add the framework reference first",
                "Write the stub to suggestedFilePath (or merge into the existing test class), fill in the TODOs"
            });
    }

    // The test project and its framework: among the projects that reference the SUT's
    // project (directly or transitively), in the solution's project order, then the SUT's
    // own project, the first whose compilation resolves a test framework's attribute.
    private async Task<(string framework, Project? testProject)> DetectTestFrameworkAsync(
        Project? sutProject, CancellationToken cancellationToken)
    {
        if (sutProject == null) return ("xunit", null);
        var logical = GetLogicalProjects();
        var sutMembers = logical.Of(sutProject).Projects.Select(p => p.Id).ToHashSet();
        var graph = _solution!.GetProjectDependencyGraph();
        var dependents = sutMembers
            .SelectMany(id => graph.GetProjectsThatTransitivelyDependOnThisProject(id))
            .ToHashSet();

        var candidates = logical.Projects
            .Where(p => p.Projects.Any(member => dependents.Contains(member.Id)) && !p.Projects.Any(member => sutMembers.Contains(member.Id)))
            .Append(logical.Of(sutProject));
        foreach (var candidate in candidates)
        {
            var project = candidate.Projects[0];
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation != null && TestFrameworkOf(compilation) is { } framework) return (framework, project);
        }
        return ("xunit", null);
    }

    // The test framework whose test attribute type the compilation resolves, checked in
    // xUnit, NUnit, MSTest, TUnit order (each attribute is the type its framework's
    // discovery contract starts from; see TestFrameworkContracts).
    private static string? TestFrameworkOf(Compilation compilation)
    {
        foreach (var (framework, ns, name) in new[]
                 {
                     ("xunit", "Xunit", "FactAttribute"),
                     ("nunit", "NUnit.Framework", "TestAttribute"),
                     ("mstest", "Microsoft.VisualStudio.TestTools.UnitTesting", "TestMethodAttribute"),
                     ("tunit", "TUnit.Core", "TestAttribute")
                 })
        {
            if (compilation.GetTypeByMetadataName($"{ns}.{name}") is { } attribute && TestFrameworkContracts.Is(attribute, ns, name))
                return framework;
        }
        return null;
    }

    // Why a test cannot call the method — the accessibility word of the method (or of
    // its type) that the test project cannot reach — or null when it can. Without a test
    // project, only members no other assembly can reach are refused.
    private static string? InaccessibleTarget(IMethodSymbol method, Compilation? testCompilation)
    {
        if (testCompilation != null)
        {
            var seen = SymbolFinder.FindSimilarSymbols(method, testCompilation).FirstOrDefault()
                ?? (SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, testCompilation.Assembly) ? method : null);
            if (seen != null && testCompilation.IsSymbolAccessibleWithin(seen, testCompilation.Assembly)) return null;
            if (seen != null || method.DeclaredAccessibility != Accessibility.Public) return AccessibilityWord(Restrictive(method));
            return null;
        }
        return Restrictive(method).DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal
            ? AccessibilityWord(Restrictive(method))
            : null;
    }

    // The method itself when it is not public, else its least accessible containing type.
    private static ISymbol Restrictive(IMethodSymbol method)
    {
        if (method.DeclaredAccessibility != Accessibility.Public) return method;
        for (var type = method.ContainingType; type != null; type = type.ContainingType)
        {
            if (type.DeclaredAccessibility != Accessibility.Public) return type;
        }
        return method;
    }

    private static string AccessibilityWord(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.Internal => "internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        _ => "public"
    };

    // A type argument for a type parameter that satisfies its constraints, given the
    // arguments already chosen for earlier parameters (so a constraint naming another
    // parameter, or the parameter itself as in T : IComparable<T>, is checked closed):
    // object, int or string when one of them satisfies them, else the first constraint
    // type (one that names no type parameter) that does, else object. The choice is
    // recorded in `chosen`.
    private static ITypeSymbol ChooseTypeArgument(
        ITypeParameterSymbol parameter, Dictionary<ITypeParameterSymbol, ITypeSymbol> chosen, Compilation compilation)
    {
        var candidates = new List<ITypeSymbol>
        {
            compilation.GetSpecialType(SpecialType.System_Object),
            compilation.GetSpecialType(SpecialType.System_Int32),
            compilation.GetSpecialType(SpecialType.System_String)
        };
        candidates.AddRange(parameter.ConstraintTypes.Where(t => !MentionsTypeParameter(t)));

        foreach (var candidate in candidates)
        {
            chosen[parameter] = candidate;
            var concrete = Substitute(candidate, chosen, compilation);
            if (Satisfies(parameter, concrete, chosen, compilation)) return chosen[parameter] = concrete;
        }
        return chosen[parameter] = compilation.GetSpecialType(SpecialType.System_Object);
    }

    private static bool Satisfies(
        ITypeParameterSymbol parameter, ITypeSymbol argument, Dictionary<ITypeParameterSymbol, ITypeSymbol> chosen, Compilation compilation)
    {
        if (argument is ITypeParameterSymbol || argument.TypeKind == TypeKind.Error) return false;
        if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType) return false;
        if (parameter.HasValueTypeConstraint
            && (!argument.IsValueType || argument.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)) return false;
        if (parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType) return false;
        if (parameter.HasConstructorConstraint && !argument.IsValueType
            && (argument.IsAbstract || argument is not INamedTypeSymbol named
                || !named.InstanceConstructors.Any(c => c.Parameters.IsEmpty && c.DeclaredAccessibility == Accessibility.Public)))
            return false;
        return parameter.ConstraintTypes.All(constraint =>
        {
            var conversion = compilation.ClassifyCommonConversion(argument, Substitute(constraint, chosen, compilation));
            return conversion.Exists && conversion.IsImplicit && !conversion.IsUserDefined && !conversion.IsNumeric;
        });
    }

    private static bool MentionsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        INamedTypeSymbol named => named.TypeArguments.Any(MentionsTypeParameter),
        IArrayTypeSymbol array => MentionsTypeParameter(array.ElementType),
        _ => false
    };

    // The type with the chosen type arguments substituted for the type parameters.
    private static ITypeSymbol Substitute(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> chosen, Compilation compilation) =>
        type switch
        {
            ITypeParameterSymbol parameter when chosen.TryGetValue(parameter, out var argument) => argument,
            INamedTypeSymbol { IsGenericType: true } named => named.ConstructedFrom.Construct(
                named.TypeArguments.Select(a => Substitute(a, chosen, compilation)).ToArray()),
            IArrayTypeSymbol array => compilation.CreateArrayTypeSymbol(Substitute(array.ElementType, chosen, compilation), array.Rank),
            _ => type
        };

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

    // A named-argument name must be @-escaped when the parameter name is a reserved
    // keyword (e.g. a parameter literally named `class`), or the call won't compile.
    private static string EscapeIdentifier(string name) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name)
            != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None
            ? "@" + name
            : name;
}
