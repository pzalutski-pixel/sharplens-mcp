using System.Text.Json.Nodes;

namespace SharpLensMcp;

// Single home for the MCP tool surface: the published schemas and the dispatcher
// that routes tools/call into RoslynService. Relocated verbatim from McpServer so
// the protocol class stays focused on JSON-RPC mechanics. The ToolsListGolden test
// locks the schema output byte-for-byte; the dispatcher contract tests lock routing.
internal static class ToolRegistry
{
    // One ToolDefinition per tool, schema and handler adjacent. The list order
    // defines tools/list order; the dictionary below serves dispatch lookups.
    internal static readonly IReadOnlyList<ToolDefinition> Definitions = BuildDefinitions();

    private static readonly Dictionary<string, ToolDefinition> DefinitionsByName =
        Definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);

    // Projects each schema with the MCP annotations derived from the record's
    // ReadOnly flag. A non-ReadOnly tool mutates workspace or disk state — some
    // edit source files (rename_symbol, organize_usings), others change only the
    // in-memory solution (load_solution, sync_documents) — so it carries
    // destructiveHint alongside readOnlyHint: false.
    internal static List<object> BuildToolSchemas() => Definitions
        .Select(d =>
        {
            var node = System.Text.Json.JsonSerializer.SerializeToNode(d.Schema)!.AsObject();
            node["annotations"] = d.ReadOnly
                ? new JsonObject { ["readOnlyHint"] = true }
                : new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = true };
            return (object)node;
        })
        .ToList();

    // Composed from per-concern builders so no single method holds the whole
    // tool surface. The list order (and thus tools/list order) is the builder
    // order below, each builder's entries in declaration order.
    private static List<ToolDefinition> BuildDefinitions() =>
        CoreDefinitions()
        .Concat(TypeDiscoveryDefinitions())
        .Concat(AiAndCodegenDefinitions())
        .Concat(CodeActionAndMetricsDefinitions())
        .Concat(DiscoveryAndQualityDefinitions())
        .Concat(FlowAndSearchDefinitions())
        .ToList();

    // Core: lifecycle, navigation, search, diagnostics, structure, the first-wave
    // refactorings.
    private static IEnumerable<ToolDefinition> CoreDefinitions()
    {
        return new ToolDefinition[]
        {
            new ToolDefinition("health_check", ReadOnly: true,
            (object)new
            {
                name = "health_check",
                description = "Check the health and status of the SharpLens MCP server and workspace. Returns server status, solution loaded state, project/document counts, cache size, and configuration. Reports load fidelity too: declaredProjects (compiling projects the solution file declares), a partial flag, loadFailures and loadWarnings — so you can detect a solution that silently dropped projects, without reloading — and lastLoadFailure, the last load (load_solution or the DOTNET_SOLUTION_PATH startup auto-load) that did not complete. O(1) by default; set includeDiagnostics=true to also compile a sample of project files and report distinct error/warning counts and how many projects were sampled (slower). Call this first to verify the server is ready.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        includeDiagnostics = new { type = "boolean", description = "Also compile up to five project files (every target framework of each) and report distinct error/warning counts, with diagnosticsSampledProjects of diagnosticsTotalProjects (default: false). Off by default because the compilation can take tens of seconds on a large solution." }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetHealthCheckAsync(
                p.Optional("includeDiagnostics", false),
                cancellationToken)),
            new ToolDefinition("load_solution", ReadOnly: false,
            (object)new
            {
                name = "load_solution",
                description = "Load a .NET solution for analysis. MUST be called before using any other analysis tools. A load that fails leaves the previously loaded solution in place. Returns projectCount (one per target framework) and documentCount, plus load-fidelity fields so a partial load is never silent: declaredProjectCount (compiling projects the solution file declares; shared projects and other non-compiling types are not counted), a partial flag (true when something declared is missing or unusable: a project that did not load or that MSBuild could not evaluate, a project with no references because it was not restored, a NuGet restore error), loadFailures (what made it partial), and loadWarnings with loadWarningCount (messages about projects that loaded completely, e.g. MSBuild or NuGet warnings). If partial is true, downstream results are based on an incomplete solution. Use health_check to re-check without reloading.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        solutionPath = new { type = "string", description = "Absolute path to a .sln, .slnx or .slnf file" }
                    },
                    required = new[] { "solutionPath" }
                }
            },
            (svc, p, cancellationToken) => svc.LoadSolutionAsync(
                p.Required<string>("solutionPath"),
                cancellationToken)),
            new ToolDefinition("sync_documents", ReadOnly: false,
            (object)new
            {
                name = "sync_documents",
                description = @"Synchronize document changes from disk into the loaded solution. Call this after using Edit/Write tools to ensure Roslyn has fresh content.

USAGE:
- sync_documents(filePaths: [""src/Foo.cs"", ""src/Bar.cs""]) - sync specific files
- sync_documents() - sync ALL documents (refresh entire solution)

WHEN TO CALL:
- After using Edit tool to modify .cs files
- After using Write tool to create new .cs files
- After deleting .cs files
- NOT needed after using SharpLensMcp refactoring tools (they auto-update)

HANDLES: Modified files (updates content), new files (adds to solution), deleted files (removes from solution).
Much faster than load_solution - only updates documents, doesn't re-parse projects.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePaths = new {
                            type = "array",
                            items = new { type = "string" },
                            description = "Optional: specific file paths to sync. If omitted, syncs ALL documents from disk."
                        }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.SyncDocumentsAsync(
                p.OptionalStringArray("filePaths"),
                cancellationToken)),
            new ToolDefinition("get_symbol_info", ReadOnly: true,
            (object)new
            {
                name = "get_symbol_info",
                description = "Get detailed semantic information about a symbol at a specific position. IMPORTANT: Uses ZERO-BASED coordinates. If your editor shows 'Line 14, Column 5', pass line=13, column=4. Returns symbol kind, type, namespace, documentation, location (the part that carries the body of a partial member; null for a symbol without source) and locations (every declaring part, in path order).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number (Visual Studio line 14 = line 13 here)" },
                        column = new { type = "integer", description = "Zero-based column number (Visual Studio col 5 = col 4 here)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetSymbolInfoAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                cancellationToken)),
            new ToolDefinition("go_to_definition", ReadOnly: true,
            (object)new
            {
                name = "go_to_definition",
                description = "Fast navigation to symbol definition. Returns definition (the part that carries the body of a partial member) and locations (every declaring part, in path order), each with start and end, without finding all references. IMPORTANT: Uses ZERO-BASED coordinates (editor line 10 = pass line 9).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file containing the symbol" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GoToDefinitionAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                cancellationToken)),
            new ToolDefinition("find_references", ReadOnly: true,
            (object)new
            {
                name = "find_references",
                description = "Find all references to a symbol across the entire solution. Each site is reported once (a multi-targeted project's site once, with the targetFrameworks it occurs in), ordered by file, line and column. Returns file paths, line numbers, code context, and a `kind` for each reference: write (assignment target including null-conditional and deconstruction targets, ++/--, ref/out arg), invocation (call target, including null-conditional calls), cast (cast target type), typeof, nameof, attribute, or read. IMPORTANT: Uses ZERO-BASED coordinates (editor line 10 = pass line 9).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file containing the symbol" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" },
                        maxResults = new { type = "integer", description = "Maximum number of references to return, 1-10000 (default: 100). totalCount counts every matching reference; truncated is set when the cap cuts some." },
                        kind = new { type = "string", description = "Optional kind filter (case-insensitive). One of: write, invocation, cast, typeof, nameof, attribute, read; any other value is INVALID_PARAMETER. When set, the response includes totalReferences (unfiltered) and totalReferencesAfterFilter so callers see both numbers." }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.FindReferencesAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.OptionalStruct<int>("maxResults"),
                p.Optional<string>("kind"),
                cancellationToken)),
            new ToolDefinition("find_implementations", ReadOnly: true,
            (object)new
            {
                name = "find_implementations",
                description = "Find all implementations of an interface or class: for an interface, every class and struct implementing it (directly or through a base); for a class (abstract or not), every class deriving from it, directly or indirectly. Abstract implementations are included and flagged isAbstract. Each type is reported once (with targetFrameworks when its project is multi-targeted), sorted by name, with its locations. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        maxResults = new { type = "integer", description = "Maximum number of implementations to return, 1-10000 (default: 50). totalImplementations counts all; truncated is set when the cap cuts some." }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.FindImplementationsAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.OptionalStruct<int>("maxResults"),
                cancellationToken)),
            new ToolDefinition("get_type_hierarchy", ReadOnly: true,
            (object)new
            {
                name = "get_type_hierarchy",
                description = "Get the inheritance hierarchy of a type: baseTypes (the base class chain, System.Object omitted), interfaces (all of them), and derivedTypes — the types directly below it: for a class the classes deriving from it, for an interface the interfaces deriving from it and the types implementing it, each once, sorted by name. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        maxDerivedTypes = new { type = "integer", description = "Maximum number of derived types to return, 1-10000 (default: 50). Caps derivedTypes only; totalCount/truncated describe derivedTypes, and baseTypes and interfaces are always complete." }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetTypeHierarchyAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.OptionalStruct<int>("maxDerivedTypes"),
                cancellationToken)),
            new ToolDefinition("search_symbols", ReadOnly: true,
            (object)new
            {
                name = "search_symbols",
                description = "Search for types, members and namespaces by name across the solution. Default glob mode supports wildcards (e.g., '*Handler' finds classes ending with 'Handler'); matchMode='fuzzy' uses the IDE pattern matcher with camel-hump matching ('RSvc' finds RoslynService). CASING: glob/substring matching is case-INSENSITIVE; fuzzy matching treats uppercase query letters as camel-hump anchors (effectively case-sensitive on them). Each result's kind is from the same vocabulary the kind filter accepts. Each symbol is reported once (a multi-targeted declaration with its targetFrameworks, a namespace once however many projects declare it), sorted by fully-qualified name. PAGINATION: Returns totalCount and hasMore; follow pagination.nextOffset until it is null.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Search query. Glob mode: wildcards * (any characters) and ? (single character), e.g. 'Handler', '*Handler', 'Get*', 'I?Service'. Fuzzy mode: camel humps and partial names, e.g. 'RSvc'. Case-insensitive." },
                        matchMode = new { type = "string", description = "'glob' (default) or 'fuzzy' (IDE-grade camel-hump/partial matching via Roslyn's pattern matcher); any other value is INVALID_PARAMETER" },
                        kind = new { type = "string", description = "Optional: filter by symbol kind, case-insensitive. Types: Class (record classes included), Record, Struct (record structs included), RecordStruct, Interface, Enum, Delegate. Members: Method, Constructor, StaticConstructor, Finalizer, Property, Indexer, Field, Event, Operator, Conversion. Other: Namespace. Any other value is INVALID_PARAMETER." },
                        maxResults = new { type = "integer", description = "Maximum number of results per page, 1-10000 (default: 50)" },
                        namespaceFilter = new { type = "string", description = "Optional: filter by namespace (supports wildcards). Examples: 'MyApp.Core.*', '*.Services', 'MyApp.*.Handlers'. Case-insensitive." },
                        offset = new { type = "integer", description = "Offset for pagination, 0 or greater (default: 0). Use pagination.nextOffset from previous response to get next page." }
                    },
                    required = new[] { "query" }
                }
            },
            (svc, p, cancellationToken) => svc.SearchSymbolsAsync(
                p.Required<string>("query"),
                p.Optional<string>("kind"),
                p.Optional("maxResults", 50),
                p.Optional<string>("namespaceFilter"),
                p.Optional("offset", 0),
                p.Optional("matchMode", "glob"),
                cancellationToken)),
            new ToolDefinition("semantic_query", ReadOnly: true,
            (object)new
            {
                name = "semantic_query",
                description = @"Advanced semantic code query with multiple filters. Find types and members (never namespaces) based on their semantic properties.

EXAMPLES:
- Async methods without CancellationToken: isAsync=true, parameterExcludes=[""CancellationToken""]
- Public static methods: accessibility=""Public"", isStatic=true
- Classes with [Obsolete]: kinds=[""Class""], attributes=[""Obsolete""]

FILTERS: All specified filters are combined with AND logic, and each is a constraint every result satisfies: isAsync and returnType keep only methods, parameterIncludes/parameterExcludes only methods and indexers, type only fields, properties and events. Omit a filter to skip it. Each symbol is reported once (a multi-targeted declaration with its targetFrameworks), sorted by fully-qualified name, with its kind from the same vocabulary kinds accepts and its location.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        kinds = new { type = "array", items = new { type = "string" }, description = "Optional: filter by symbol kinds (can specify multiple), case-insensitive. Types: Class (record classes included), Record, Struct (record structs included), RecordStruct, Interface, Enum, Delegate. Members: Method, Constructor, StaticConstructor, Finalizer, Property, Indexer, Field, Event, Operator, Conversion. Any other value is INVALID_PARAMETER. Example: ['Class', 'Interface']" },
                        isAsync = new { type = "boolean", description = "Optional: keep only methods that are async (true) or not async (false)" },
                        namespaceFilter = new { type = "string", description = "Optional: filter by namespace (supports wildcards). Examples: 'MyApp.Core.*', '*.Services'" },
                        accessibility = new { type = "string", description = "Optional: filter by accessibility, case-insensitive. Values: Public, Private, Internal, Protected, ProtectedOrInternal, ProtectedAndInternal; any other value is INVALID_PARAMETER" },
                        isStatic = new { type = "boolean", description = "Optional: filter by static modifier (true for static, false for instance)" },
                        type = new { type = "string", description = "Optional: keep only fields, properties and events of this type. A simple name matches a whole word of the type name; a qualified or generic one is a substring match. Example: 'ILogger' finds all ILogger fields/properties" },
                        returnType = new { type = "string", description = "Optional: keep only methods returning this type (matched like type). Example: 'Task' finds all methods returning Task or Task<T>" },
                        attributes = new { type = "array", items = new { type = "string" }, description = "Optional: filter by attributes (must have ALL specified), each by its short name, class name or fully-qualified name, case-insensitive. Example: ['Obsolete', 'System.ComponentModel.EditorBrowsableAttribute']" },
                        parameterIncludes = new { type = "array", items = new { type = "string" }, description = "Optional: keep only methods and indexers that HAVE parameters of all these types (matched like type). Example: ['CancellationToken']" },
                        parameterExcludes = new { type = "array", items = new { type = "string" }, description = "Optional: keep only methods and indexers that have NO parameter of these types (matched like type). Example: ['CancellationToken']" },
                        maxResults = new { type = "integer", description = "Maximum number of results, 1-10000 (default: 100). totalCount counts every match; truncated is set when the cap cuts some." }
                    },
                    required = new string[] { }
                }
            },
            (svc, p, cancellationToken) => svc.SemanticQueryAsync(
                p.OptionalStringArray("kinds"),
                p.OptionalStruct<bool>("isAsync"),
                p.Optional<string>("namespaceFilter"),
                p.Optional<string>("accessibility"),
                p.OptionalStruct<bool>("isStatic"),
                p.Optional<string>("type"),
                p.Optional<string>("returnType"),
                p.OptionalStringArray("attributes"),
                p.OptionalStringArray("parameterIncludes"),
                p.OptionalStringArray("parameterExcludes"),
                p.OptionalStruct<int>("maxResults"),
                cancellationToken)),
            new ToolDefinition("get_diagnostics", ReadOnly: true,
            (object)new
            {
                name = "get_diagnostics",
                description = "Get compiler errors, warnings, info messages, AND configured analyzer findings (StyleCop, Roslynator, NetAnalyzers, custom analyzers; editorconfig-resolved severities) for a file, a project, or the whole solution. DiagnosticSuppressors apply to every diagnostic, as in `dotnet build`. A multi-targeted project is evaluated for every target framework: each diagnostic is listed once (errors first, then by location, with its zero-based line and column) and carries targetFrameworks, the frameworks whose compilation reports it. Response includes analyzersRan/analyzerCount (distinct analyzers that ran) and analysisMode='live-compilation': these are Roslyn's live diagnostics, which closely track CI but can differ from a full `dotnet build` - a partially-loaded solution especially inflates unresolved-type errors, so check health_check's partial flag if counts look off. The list is capped at SHARPLENS_MAX_DIAGNOSTICS (default 100); errorCount/warningCount and totalCount count everything.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Optional: path to a specific file (absolute or solution-relative); omit for a project or the whole solution" },
                        projectPath = new { type = "string", description = "Optional: the project file path (absolute or solution-relative, either slash, e.g. 'Lib/Lib.csproj') or the project name ('Lib'; a multi-targeted 'Multi' covers every framework, 'Multi(net8.0)' one). An unknown project is an INVALID_PARAMETER error." },
                        severity = new { type = "string", description = "Optional: list only this severity (Error, Warning, Info, Hidden). Hidden returns hidden diagnostics whatever includeHidden says." },
                        includeHidden = new { type = "boolean", description = "Include hidden diagnostics (default: false)" },
                        runAnalyzers = new { type = "boolean", description = "Run configured DiagnosticAnalyzers in addition to the compiler (default: true). Set false for a fast compiler-only pass when speed matters; otherwise leave true so output matches CI build behavior." }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetDiagnosticsAsync(
                p.Optional<string>("filePath"),
                p.Optional<string>("projectPath"),
                p.Optional<string>("severity"),
                p.Optional("includeHidden", false),
                p.Optional("runAnalyzers", true),
                cancellationToken)),
            new ToolDefinition("get_code_fixes", ReadOnly: true,
            (object)new
            {
                name = "get_code_fixes",
                description = "Get the available code fixes for the diagnostic with this ID AT the given position (its span contains the position; pass the line and column get_diagnostics reports) - compiler and analyzer diagnostics alike, suppressed ones excluded. Fixes come from the built-in providers and the fixers the project's analyzer packages ship - the same providers apply_code_fix uses, so each entry's index can be passed straight to apply_code_fix as fixIndex. No such diagnostic at the position is SYMBOL_NOT_FOUND listing the diagnostics nearby. When no provider can fix the diagnostic, fallbackSuggestions carries manual guidance instead. WORKFLOW: (1) get_diagnostics to find issues, (2) get_code_fixes to see options, (3) apply_code_fix with fixIndex to fix.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        diagnosticId = new { type = "string", description = "Diagnostic ID (e.g., CS0246, CA1822)" },
                        line = new { type = "integer", description = "Zero-based line number of a position inside the diagnostic" },
                        column = new { type = "integer", description = "Zero-based column number of a position inside the diagnostic" }
                    },
                    required = new[] { "filePath", "diagnosticId", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetCodeFixesAsync(
                p.Required<string>("filePath"),
                p.Required<string>("diagnosticId"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                cancellationToken)),
            new ToolDefinition("apply_code_fix", ReadOnly: false,
            (object)new
            {
                name = "apply_code_fix",
                description = "Apply an automated code fix for the diagnostic with this ID AT the given position (its span contains the position) - compiler or analyzer diagnostic, fixed by the built-in providers or the project's analyzer-package fixers. No such diagnostic at the position is SYMBOL_NOT_FOUND: nothing elsewhere in the file is edited. WORKFLOW: (1) Call with no fixIndex to list available fixes, (2) Call with fixIndex and preview=true to preview changes, (3) Call with preview=false to apply. IMPORTANT: Uses ZERO-BASED coordinates.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        diagnosticId = new { type = "string", description = "Diagnostic ID (e.g., CS0168, CS1998, CS4012)" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" },
                        fixIndex = new { type = "integer", description = "Index of fix to apply (omit to list available fixes). Call without this parameter first to see available fixes." },
                        preview = new { type = "boolean", description = "Preview mode (default: true). Set to false to apply changes to disk. ALWAYS preview first!" }
                    },
                    required = new[] { "filePath", "diagnosticId", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.ApplyCodeFixAsync(
                p.Required<string>("filePath"),
                p.Required<string>("diagnosticId"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.OptionalStruct<int>("fixIndex"),
                p.Optional("preview", true),
                cancellationToken)),
            new ToolDefinition("get_project_structure", ReadOnly: true,
            (object)new
            {
                name = "get_project_structure",
                description = "Get solution/project structure: each project once - a multi-targeted project with targetFrameworks (and outputPaths per framework), a single-targeted one with the targetFramework MSBuild evaluates (set in the project file, an imported Directory.Build.props, or through a property). meta.totalCount counts the projects the pattern matched; only maxProjects truncates. IMPORTANT: For large solutions (100+ projects), use summaryOnly=true or projectNamePattern to avoid token limit errors. Maximum output is limited to 25,000 tokens.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        includeReferences = new { type = "boolean", description = "Include metadata references (default: true, limited to 100 per project)" },
                        includeDocuments = new { type = "boolean", description = "Include document lists (default: false, limited to 500 per project)" },
                        projectNamePattern = new { type = "string", description = "Filter projects by name pattern (supports * and ? wildcards, e.g., '*.Application' or 'MyApp.*')" },
                        maxProjects = new { type = "integer", description = "Maximum number of projects to return, 1-10000 (e.g., 10 for large solutions)" },
                        summaryOnly = new { type = "boolean", description = "Return only project names and counts (default: false, recommended for large solutions)" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetProjectStructureAsync(
                p.Optional("includeReferences", true),
                p.Optional("includeDocuments", false),
                p.Optional<string>("projectNamePattern"),
                p.OptionalStruct<int>("maxProjects"),
                p.Optional("summaryOnly", false),
                cancellationToken)),
            new ToolDefinition("organize_usings", ReadOnly: false,
            (object)new
            {
                name = "organize_usings",
                description = "Remove unused using directives (the compiler's CS8019 verdict, in EVERY target framework that compiles the file - a using one framework needs is kept) and sort the rest: global usings first, then plain (System namespaces leading), then static, then aliases - alphabetical within each group. Preprocessor directives on a removed using are kept, and a using block that #region/#if directives run through is not reordered (sortSkippedForDirectives), as in Roslyn's Remove and Sort Usings. A project whose references cannot resolve System.Object (not restored) is refused with ANALYSIS_FAILED - every using would look unnecessary. PREVIEW mode by default returns the organized text; set preview=false to apply to the file.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        preview = new { type = "boolean", description = "Preview mode (default: true) returns organizedText without changing the file. Set to false to apply." }
                    },
                    required = new[] { "filePath" }
                }
            },
            (svc, p, cancellationToken) => svc.OrganizeUsingsAsync(
                p.Required<string>("filePath"),
                p.Optional("preview", true),
                cancellationToken)),
            new ToolDefinition("organize_usings_batch", ReadOnly: false,
            (object)new
            {
                name = "organize_usings_batch",
                description = "Organize using directives (as organize_usings does) for multiple files in a project, each file once however many target frameworks compile it; generated code (*.g.cs, *.designer.cs, <auto-generated> files, generated_code = true) is skipped. Supports file pattern filtering (e.g., '*.cs', 'Services/*.cs'). PREVIEW mode by default - set preview=false to apply changes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Optional: Project name to process ('Multi' for every framework of a multi-targeted project, 'Multi(net8.0)' for one). An unknown name is an INVALID_PARAMETER error. If omitted, processes all projects in solution." },
                        filePattern = new { type = "string", description = "Optional: Glob pattern to filter files (e.g., '*.cs', 'Services/*.cs', '*Repository.cs'). Matches against file names, not full paths." },
                        preview = new { type = "boolean", description = "Preview mode (default: true). Set to false to apply changes to disk. ALWAYS preview first!" }
                    },
                    required = new string[] { }
                }
            },
            (svc, p, cancellationToken) => svc.OrganizeUsingsBatchAsync(
                p.Optional<string>("projectName"),
                p.Optional<string>("filePattern"),
                p.Optional("preview", true),
                cancellationToken)),
            new ToolDefinition("format_document_batch", ReadOnly: false,
            (object)new
            {
                name = "format_document_batch",
                description = "Format multiple documents in a project using Roslyn's whitespace formatter. Fixes indentation, spacing, and line breaks per the workspace's formatting rules without rewriting stylistic choices (null-suppression spacing and intentional blank lines are preserved). Each file is formatted once, under every target framework's preprocessor symbols in turn; generated code (*.g.cs, *.designer.cs, <auto-generated> files, generated_code = true) is skipped. PREVIEW mode by default - set preview=false to apply changes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Optional: Project name to format ('Multi' for every framework of a multi-targeted project, 'Multi(net8.0)' for one). An unknown name is an INVALID_PARAMETER error. If omitted, formats all projects in solution." },
                        includeTests = new { type = "boolean", description = "Include test projects (default: true). Set to false to skip projects with 'Test' in the name." },
                        filePattern = new { type = "string", description = "Optional: Glob pattern to filter files (e.g., '*.cs', 'Services/*.cs', '*Repository.cs'). Matches against file names, not full paths." },
                        mode = new { type = "string", description = "Formatting engine: 'whitespace' (default) fixes indentation/spacing and preserves the author's style — safe for whole projects. 'canonical' fully re-prints the syntax tree: the only mode that expands minified one-liner code, but it REWRITES stylistic whitespace (null-suppression spacing, blank lines between members) — use only on files you intend to fully restyle." },
                        preview = new { type = "boolean", description = "Preview mode (default: true). Set to false to apply changes to disk. ALWAYS preview first!" }
                    },
                    required = new string[] { }
                }
            },
            (svc, p, cancellationToken) => svc.FormatDocumentBatchAsync(
                p.Optional<string>("projectName"),
                p.Optional("includeTests", true),
                p.Optional("preview", true),
                p.Optional<string>("filePattern"),
                p.Optional<string>("mode"),
                cancellationToken)),
            new ToolDefinition("get_method_overloads", ReadOnly: true,
            (object)new
            {
                name = "get_method_overloads",
                description = "Get all overloads of the method, constructor, operator or local function at a position: the overload set every by-name tool indexes (declared overloads first, then inherited ones flagged isInherited; a constructor's set is its type's constructors, an explicit interface implementation's the members of the implemented name). Each entry carries the overloadIndex that get_method_source, get_method_signature, get_documentation and get_super_method use for it, its signature, kind, parameters and location (the body part of a partial method). Not a method (a property, an accessor) is NOT_A_METHOD. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetMethodOverloadsAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                cancellationToken)),
            new ToolDefinition("get_containing_member", ReadOnly: true,
            (object)new
            {
                name = "get_containing_member",
                description = "Get the member declaration around a position: a method, constructor, finalizer, operator, conversion, property, indexer, event, field (the one variable of a multi-variable declaration the position is on), enum member or delegate — accessors, local functions and lambdas count as their member — or, for a position in a type declaration outside its members (records and record structs included), the type. Returns its name, memberKind (search_symbols' kind vocabulary), signature and span. A position on a declaration of several fields that names none of them is AMBIGUOUS_MATCH; one outside every type is SYMBOL_NOT_FOUND. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetContainingMemberAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                cancellationToken)),
            new ToolDefinition("find_callers", ReadOnly: true,
            (object)new
            {
                name = "find_callers",
                description = "Find every call site of a method, constructor, accessor, operator or property (inverse of get_outgoing_calls). Essential for impact analysis: 'If I change this method, what code will be affected?' Each entry is one call site: callingSymbol (the member whose body makes the call - a lambda's or local function's containing member, and for a field/property initializer each constructor that runs it), location, kind (invocation, objectCreation, constructorInitializer, delegateCreation, propertyGet, propertySet, eventAdd, eventRemove, operator, conversion, implicitCall), viaDispatch (true when the call is bound to an interface or base member and its receiver's type can run this implementation) and inGeneratedCode. Sites are in location order. IMPORTANT: Uses ZERO-BASED coordinates.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" },
                        maxResults = new { type = "integer", description = "Maximum number of call sites to return, 1-1000 (default: 100); the first sites in location order are kept and totalCount reports them all." }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.FindCallersAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.OptionalStruct<int>("maxResults"),
                cancellationToken)),
            new ToolDefinition("find_unused_code", ReadOnly: true,
            (object)new
            {
                name = "find_unused_code",
                description = @"Find unused types, methods, properties, and fields in a project or entire solution: symbols with no reference outside their own declaration (a method that only calls itself is unused; a type is unused when nothing outside it references it or its members).

NEVER REPORTED: code something outside the source calls — the program's entry point (Main) and its type, test methods by each framework's discovery contract ([Fact], [Test], [TestMethod], ...) and their classes, test lifecycle code and fixtures, framework-set members such as MSTest's TestContext property, types a framework discovers by interface or attribute — and members reached through a contract (virtual, abstract, override, interface implementations).
USAGE: find_unused_code() for entire solution, or find_unused_code(projectName=""MyProject"") for specific project.
OUTPUT: List of unused symbols with location, kind, and accessibility, each once (a multi-targeted project is analyzed once), ordered by file and line. Default limit: 50 results.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Optional: analyze one project, by its name (case-insensitive; 'Name' covers every target framework of a multi-targeted project, 'Name(net8.0)' one); an unknown name is INVALID_PARAMETER listing the projects. Omit to analyze the entire solution." },
                        includePrivate = new { type = "boolean", description = "Include private members (default: true)" },
                        includeInternal = new { type = "boolean", description = "Include internal members (default: false - usually want to keep internal APIs)" },
                        symbolKindFilter = new { type = "string", description = "Optional: filter by symbol kind ('Class' (records included), 'Interface', 'Struct', 'Enum', 'Delegate', 'Type', 'Method', 'Property', 'Field', 'Member')" },
                        maxResults = new { type = "integer", description = "Maximum results to return, 1-10000 (default: 50). totalCount counts every unused symbol." }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindUnusedCodeAsync(
                p.Optional<string>("projectName"),
                p.Optional("includePrivate", true),
                p.Optional("includeInternal", false),
                p.Optional<string>("symbolKindFilter"),
                p.OptionalStruct<int>("maxResults"),
                cancellationToken)),
            new ToolDefinition("rename_symbol", ReadOnly: false,
            (object)new
            {
                name = "rename_symbol",
                description = "Safely rename a symbol (type, method, property, etc.) across the entire solution. Uses Roslyn's rename engine so all references are updated and its conflict check: the preview lists conflicts (each location where the new name would collide with a declaration or make a reference bind to something else), and an apply that would leave a conflict is refused with nothing written. A conflict Roslyn resolves itself (e.g. qualifying a field with this.) is not reported. SUPPORTS PREVIEW MODE - always preview first! ADDRESSING: use EITHER filePath+line+column (zero-based coordinates) OR typeName(+memberName) - exactly one mode. Default shows first 20 files with summary verbosity; a file compiled for several target frameworks is listed once. data.notice reports when the type name also matched framework types.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Positional addressing: absolute path to source file containing the symbol" },
                        line = new { type = "integer", description = "Positional addressing: zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Positional addressing: zero-based column number (editor column - 1)" },
                        typeName = new { type = "string", description = "Name-based addressing: type to target (fully-qualified, simple, or unique partial name). Use INSTEAD of filePath/line/column." },
                        memberName = new { type = "string", description = "Name-based addressing: member of typeName to rename. Omit to rename the type itself." },
                        overloadIndex = new { type = "integer", description = "Name-based addressing: zero-based overload index when memberName is overloaded (see get_method_overloads)" },
                        newName = new { type = "string", description = "New name for the symbol" },
                        preview = new { type = "boolean", description = "Preview changes without applying (default: true). ALWAYS preview first!" },
                        maxFiles = new { type = "integer", description = "Max files to show in preview, 1-1000 (default: 20, prevents large outputs)" },
                        verbosity = new { type = "string", description = "Output detail level: 'summary' (default, file paths + counts only ~200 tokens/file), 'compact' (add locations ~500 tokens/file), 'full' (include old/new text ~3000+ tokens/file); any other value is rejected" }
                    },
                    required = new[] { "newName" }
                }
            },
            (svc, p, cancellationToken) => svc.RenameSymbolAsync(
                p.Optional<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                p.Required<string>("newName"),
                p.Optional("preview", true),
                p.OptionalStruct<int>("maxFiles"),
                p.Optional<string>("verbosity"),
                p.Optional<string>("typeName"),
                p.Optional<string>("memberName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("extract_interface", ReadOnly: true,
            (object)new
            {
                name = "extract_interface",
                description = @"Generate an interface from a class or struct. Extracts all public instance members (methods, properties, events); a property keeps only its public accessors (a private or protected setter is not part of the interface).

USAGE: Address the class by typeName=""MyService"" (or position on its declaration), provide interfaceName=""IMyService"" (a valid C# identifier). includeMemberNames may only name public instance members; any other name is an error.
OUTPUT: Generated interface code ready to insert, and data.notice when the type name also matched framework types. Useful for dependency injection and testability.
ADDRESSING: use EITHER typeName OR filePath+line+column (zero-based) - exactly one mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Positional addressing: absolute path to source file containing the class" },
                        line = new { type = "integer", description = "Positional addressing: zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Positional addressing: zero-based column number (editor column - 1)" },
                        typeName = new { type = "string", description = "Name-based addressing: type to extract from (fully-qualified, simple, or unique partial name). Use INSTEAD of filePath/line/column." },
                        interfaceName = new { type = "string", description = "Name for the new interface (e.g., 'IMyService')" },
                        includeMemberNames = new { type = "array", items = new { type = "string" }, description = "Optional: specific member names to include (omit to include all public members)" }
                    },
                    required = new[] { "interfaceName" }
                }
            },
            (svc, p, cancellationToken) => svc.ExtractInterfaceAsync(
                p.Optional<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                p.Required<string>("interfaceName"),
                p.OptionalStringArray("includeMemberNames"),
                p.Optional<string>("typeName"),
                cancellationToken)),
            new ToolDefinition("dependency_graph", ReadOnly: true,
            (object)new
            {
                name = "dependency_graph",
                description = @"Visualize project dependencies as a graph. Shows which projects reference which, detects circular dependencies.

OUTPUT: format=""json"" returns nodes [{id, name, filePath, targetFrameworks}] — one per project file, a multi-targeted project being one node with its target frameworks — and edges [{from, to}] by node id; format=""mermaid"" returns the same graph as Mermaid flowchart text. Node ids are unique valid Mermaid identifiers, so same-named projects stay distinct. Both report cycles (every elementary cycle as node ids, starting and ending at its smallest id; cyclesTruncated when there are more than 1000) and stronglyConnectedComponents (every set of mutually dependent nodes).
USE CASE: Understand solution architecture, find circular dependencies, plan refactoring.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        format = new { type = "string", description = "Output format: 'json' (default) returns nodes and edges, 'mermaid' returns Mermaid diagram syntax; any other value is INVALID_PARAMETER" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetDependencyGraphAsync(
                p.Optional<string>("format"),
                cancellationToken)),
        };
    }

    // Name-based type discovery + data/control-flow + compound overviews.
    private static IEnumerable<ToolDefinition> TypeDiscoveryDefinitions()
    {
        return new ToolDefinition[]
        {
            // ============ NEW TOOLS: Name-Based Type Discovery ============

            new ToolDefinition("get_type_members", ReadOnly: true,
            (object)new
            {
                name = "get_type_members",
                description = @"Get the members of a type BY NAME: methods, constructors, properties, indexers, fields, events, operators, conversions and nested types — never property/event accessors or compiler-generated members. Each entry's kind is from the same vocabulary memberKind filters by; memberCounts counts every listed kind. An explicit interface implementation is flagged isExplicitInterfaceImplementation (it is not part of the type's public surface).

USAGE PATTERNS:
- Basic: get_type_members(""MyClass"") - list all members
- With inheritance: get_type_members(""MyService"", includeInherited=true) - adds the accessible base members C# inheritance gives the type (no private base members or base constructors; an overridden member once), flagged isInherited
- Filter by kind: get_type_members(""MyClass"", memberKind=""Method"")
- Verbosity control: verbosity=""summary"" (names only), ""compact"" (default, + signatures), ""full"" (+ docs, attrs, location)

WORKS WITH: Fully-qualified (""MyNamespace.MyClass"") or simple (""MyClass"") names; a partial name is TYPE_NOT_FOUND listing suggestions. When the name also matches framework types, data.notice names them.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type name (e.g., 'MyClass', 'MyNamespace.MyService')" },
                        includeInherited = new { type = "boolean", description = "Include members from base classes (default: false)" },
                        memberKind = new { type = "string", description = "Filter, case-insensitive: Method, Constructor, StaticConstructor, Finalizer, Property, Indexer, Field, Event, Operator, Conversion, NestedType; any other value is INVALID_PARAMETER" },
                        verbosity = new { type = "string", description = "'summary' (names only), 'compact' (default), 'full' (+ docs, attrs, location: null for a member without source); any other value is INVALID_PARAMETER" },
                        maxResults = new { type = "integer", description = "Maximum members to return, 1-10000 (default: 100)" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetTypeMembersAsync(
                p.Required<string>("typeName"),
                p.Optional("includeInherited", false),
                p.Optional<string>("memberKind"),
                p.Optional("verbosity", "compact"),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("get_method_signature", ReadOnly: true,
            (object)new
            {
                name = "get_method_signature",
                description = @"Get detailed method signature BY NAME including parameters, return type, nullability, and modifiers.

USAGE: get_method_signature(""MyClass"", ""ProcessData"") or with overload selection: get_method_signature(""MyClass"", ""ProcessData"", overloadIndex=1)
ADDRESSING: methodName may name a method, an operator by its metadata name (op_Addition), a constructor (the type's simple name or "".ctor""), or an explicit interface implementation by the implemented member's name. overloadIndex indexes the overload set get_method_overloads lists: declared overloads first, then inherited ones (isInherited). A name that denotes a non-method is NOT_A_METHOD. location is the part that carries the body of a partial method; data.notice names framework types the type name also matches.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Containing type name" },
                        methodName = new { type = "string", description = "Method name (or the type's simple name / '.ctor' for a constructor)" },
                        overloadIndex = new { type = "integer", description = "Which overload (0-based, default: 0), as get_method_overloads numbers them" }
                    },
                    required = new[] { "typeName", "methodName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetMethodSignatureAsync(
                p.Required<string>("typeName"),
                p.Required<string>("methodName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("get_attributes", ReadOnly: true,
            (object)new
            {
                name = "get_attributes",
                description = @"Find every application of an attribute, whatever it is applied to: the assembly or module, types, members, accessors, parameters, return values, type parameters, lambdas and local functions, an auto-property's backing field ([field:]), a record parameter's property ([property:]), each variable of a multi-variable field. Each entry carries its C# attribute target (assembly, module, type, method, field, property, event, param, return, typevar), the symbol, and attributeLocation (where the attribute is written); entries are in attributeLocation order, one per application (a multi-targeted project's once, with targetFrameworks). find_attribute_usages walks the same applications.

USAGE:
- Find obsolete: get_attributes(""Obsolete"")
- Find serializable: get_attributes(""Serializable"")
- Scope to project: get_attributes(""Obsolete"", scope=""project:MyProject"")
- Scope to file: get_attributes(""Obsolete"", scope=""file:src/MyClass.cs"")",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        attributeName = new { type = "string", description = "Attribute name, case-insensitive, with or without the Attribute suffix, simple or fully-qualified (e.g., 'Obsolete', 'Serializable', 'System.ObsoleteAttribute')" },
                        scope = new { type = "string", description = "'solution' (default), 'project:Name' (resolved like projectName elsewhere; unknown is INVALID_PARAMETER), or 'file:path' — absolute, relative to the solution directory, or the trailing path segments of exactly one document such as its file name (none is FILE_NOT_IN_SOLUTION, several AMBIGUOUS_MATCH). Any other value is INVALID_PARAMETER." },
                        maxResults = new { type = "integer", description = "Maximum results, 1-10000 (default: 100)" }
                    },
                    required = new[] { "attributeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetAttributesAsync(
                p.Required<string>("attributeName"),
                p.Optional<string>("scope"),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("get_derived_types", ReadOnly: true,
            (object)new
            {
                name = "get_derived_types",
                description = @"Find all types below a base type BY NAME: for a class, the classes deriving from it; for an interface, the interfaces deriving from it and the classes and structs implementing it. Each type once (with targetFrameworks when its project is multi-targeted), sorted by fully-qualified name. data.notice names framework types the base type name also matches.

USAGE:
- Find all subclasses: get_derived_types(""BaseService"")
- Direct children only: get_derived_types(""BaseClass"", includeTransitive=false)",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        baseTypeName = new { type = "string", description = "Base type name" },
                        includeTransitive = new { type = "boolean", description = "Include indirect descendants (default: true)" },
                        maxResults = new { type = "integer", description = "Maximum results, 1-10000 (default: 100)" }
                    },
                    required = new[] { "baseTypeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetDerivedTypesAsync(
                p.Required<string>("baseTypeName"),
                p.Optional("includeTransitive", true),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("get_base_types", ReadOnly: true,
            (object)new
            {
                name = "get_base_types",
                description = @"Get full inheritance chain BY NAME: the base classes nearest first (System.Object omitted) and every interface the type implements. data.notice names framework types the type name also matches.

USAGE: get_base_types(""MyService"") returns baseTypes: BaseService → ... (up to, not including, System.Object)",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type name" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetBaseTypesAsync(
                p.Required<string>("typeName"),
                cancellationToken)),
            new ToolDefinition("analyze_data_flow", ReadOnly: true,
            (object)new
            {
                name = "analyze_data_flow",
                description = @"Analyze variable assignments and usage in a code region.

Returns: variablesDeclared, alwaysAssigned, dataFlowsIn/Out, readInside/Outside, writtenInside/Outside, captured.

USAGE: analyze_data_flow(""path/to/file.cs"", startLine=10, endLine=25)",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        startLine = new { type = "integer", description = "Start line (0-based)" },
                        endLine = new { type = "integer", description = "End line (0-based)" }
                    },
                    required = new[] { "filePath", "startLine", "endLine" }
                }
            },
            (svc, p, cancellationToken) => svc.AnalyzeDataFlowAsync(
                p.Required<string>("filePath"),
                p.Required<int>("startLine"),
                p.Required<int>("endLine"),
                cancellationToken)),
            new ToolDefinition("analyze_control_flow", ReadOnly: true,
            (object)new
            {
                name = "analyze_control_flow",
                description = @"Analyze branching and reachability in a code region.

Returns: entryPoints, exitPoints, returnStatements, endPointIsReachable.

USAGE: analyze_control_flow(""path/to/file.cs"", startLine=10, endLine=25)",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        startLine = new { type = "integer", description = "Start line (0-based)" },
                        endLine = new { type = "integer", description = "End line (0-based)" }
                    },
                    required = new[] { "filePath", "startLine", "endLine" }
                }
            },
            (svc, p, cancellationToken) => svc.AnalyzeControlFlowAsync(
                p.Required<string>("filePath"),
                p.Required<int>("startLine"),
                p.Required<int>("endLine"),
                cancellationToken)),

            // ============ COMPOUND TOOLS ============

            new ToolDefinition("get_type_overview", ReadOnly: true,
            (object)new
            {
                name = "get_type_overview",
                description = @"Get comprehensive type overview in ONE CALL: type info + base types + member counts.

USAGE: get_type_overview(""MyService"") - returns everything you need to understand a type quickly.
OUTPUT: baseTypes is the base-class chain, nearest first, below object/ValueType/Enum/Delegate, and baseTypeCount its length; memberSummary counts the members get_type_members lists (methods, properties, fields, events, constructors, indexers, operators, nestedTypes); data.notice reports when the type name also matched framework types.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type name" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetTypeOverviewAsync(
                p.Required<string>("typeName"),
                cancellationToken)),
            new ToolDefinition("analyze_method", ReadOnly: true,
            (object)new
            {
                name = "analyze_method",
                description = @"Get comprehensive method analysis in ONE CALL: signature + callers + outgoing calls + location.

USAGE: analyze_method(""MyService"", ""ProcessData"") or analyze_method(""MyClass"", ""Calculate"", includeCallers=true, includeOutgoingCalls=true)
OUTPUT: signature, callers, outgoing calls, location, overloadCount, and data.notice when the type name also matched framework types. A member that is not a method is NOT_A_METHOD.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Containing type name" },
                        methodName = new { type = "string", description = "Method name" },
                        includeCallers = new { type = "boolean", description = "Include caller analysis (default: true)" },
                        includeOutgoingCalls = new { type = "boolean", description = "Include the methods this method calls - get_outgoing_calls' direct calls, in its entry shape (default: false)" },
                        maxCallers = new { type = "integer", description = "Max callers to return, 1-1000 (default: 20)" },
                        maxOutgoingCalls = new { type = "integer", description = "Max outgoing calls to return, 1-1000 (default: 50)" },
                        overloadIndex = new { type = "integer", description = "0-based overload index when the method name is overloaded, as get_method_overloads lists them; omit to get an AMBIGUOUS_MATCH listing the overloads" }
                    },
                    required = new[] { "typeName", "methodName" }
                }
            },
            (svc, p, cancellationToken) => svc.AnalyzeMethodAsync(
                p.Required<string>("typeName"),
                p.Required<string>("methodName"),
                p.Optional("includeCallers", true),
                p.Optional("includeOutgoingCalls", false),
                p.Optional("maxCallers", 20),
                p.Optional("maxOutgoingCalls", 50),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("get_file_overview", ReadOnly: true,
            (object)new
            {
                name = "get_file_overview",
                description = @"Get comprehensive file overview in ONE CALL: diagnostics summary + type declarations (each with a member outline) + namespace + line count. The agent's substitute for reading a whole file.

USAGE: get_file_overview(""path/to/MyClass.cs"")
OUTPUT: typeDeclarations[] each with members[] {name, kind, signature, accessibility, startLine, endLine} declared IN THIS FILE (a partial lists only its own part; a field declaring several variables is one member per variable) and memberCount, the number of those members; nested types appear both as a member and as their own entry. members is capped at maxMembersPerType (default 100) with membersTruncated flagged.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        maxMembersPerType = new { type = "integer", description = "Cap on members listed per type, 1-1000 (default: 100); membersTruncated flags when exceeded" }
                    },
                    required = new[] { "filePath" }
                }
            },
            (svc, p, cancellationToken) => svc.GetFileOverviewAsync(
                p.Required<string>("filePath"),
                p.Optional("maxMembersPerType", 100),
                cancellationToken)),
        };
    }

    // AI-focused inspection + the codegen/edit tools through extract_method.
    private static IEnumerable<ToolDefinition> AiAndCodegenDefinitions()
    {
        return new ToolDefinition[]
        {
            // Phase 2: AI-Focused Tools
            new ToolDefinition("get_missing_members", ReadOnly: true,
            (object)new
            {
                name = "get_missing_members",
                description = @"Get all interface and abstract members that must be implemented for a type — exactly the compiler's requirements: interface members without an implementation (CS0535; static abstract members included, members with a default body, static members with a body and nested types excluded) and abstract base members no class in between overrides (CS0534). A property or event is one entry, never its accessors.

USAGE: Position on a class that implements interfaces or extends abstract classes.
OUTPUT: List of missing members (fromInterface or fromAbstractClass, memberName, kind, isStatic) with exact signatures ready to copy. Use before implementing interfaces.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number on the type declaration" },
                        column = new { type = "integer", description = "Zero-based column number" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetMissingMembersAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                cancellationToken)),
            new ToolDefinition("get_outgoing_calls", ReadOnly: true,
            (object)new
            {
                name = "get_outgoing_calls",
                description = "Get every method a method, constructor, accessor, operator, finalizer or local function calls: ordinary, extension and generic calls (one entry per declaration), constructors (with the field/property initializers a constructor runs), the getter/setter a property or indexer use runs, operators, conversions, the implicit calls of foreach/using/await/deconstruction, and partial-method implementations. Calls inside lambdas and local functions belong to the containing member. An interface or virtual call lists the member it is bound to (viaDispatch: false) and the in-solution implementations its receiver's static type can run (viaDispatch: true); a sealed or unrelated receiver fans out to nothing. Each entry: method, shortName, returnType, isAsync, isExternal, kind, viaDispatch, depth, location; entries are in call-site order per hop. truncatedByDepth: a callee at the last hop calls something not yet listed. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number inside the member (a property with one accessor, or an expression-bodied property, addresses its accessor)" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        maxDepth = new { type = "integer", description = "How deep to trace calls into in-source callees, 1-10 (1 = direct only, default: 1). Each entry reports the hop it was found at in its depth field." }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetOutgoingCallsAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.OptionalStruct<int>("maxDepth"),
                cancellationToken)),
            new ToolDefinition("validate_code", ReadOnly: true,
            (object)new
            {
                name = "validate_code",
                description = @"Check if code would compile without writing to disk. Use to validate generated code before applying.

USAGE: validate_code(code=""public void Foo() {}"", contextFilePath=""path/to/file.cs"") to check with existing usings.
SNIPPETS: statements are compiled inside a method, members (methods, properties, fields) inside a class, and a complete file (namespace or type declarations) as itself. With contextFilePath the snippet compiles in that file's project (every target framework that compiles it), with the file's usings and namespace - a complete file that redeclares a type of the context file REPLACES that file (it is its new version); any other complete file is compiled as a new file of that project, beside it. Without it (or with standalone=true, for a complete file) the snippet compiles as a new file of one reference project (the one with the most references, reported as contextProject): its references, language version, compilation options (nullable, unsafe) and global usings.
OUTPUT: compiles (bool), contextProject, errors and warnings with snippet-relative zero-based line and column. Essential before inserting AI-generated code.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        code = new { type = "string", description = "C# code to validate: statements, members, or a complete file" },
                        contextFilePath = new { type = "string", description = "Optional: file whose project, usings and namespace the snippet compiles in" },
                        standalone = new { type = "boolean", description = "If true, treat code as a complete new file of the reference project (default: false)" }
                    },
                    required = new[] { "code" }
                }
            },
            (svc, p, cancellationToken) => svc.ValidateCodeAsync(
                p.Required<string>("code"),
                p.Optional<string>("contextFilePath"),
                p.Optional("standalone", false),
                cancellationToken)),
            new ToolDefinition("check_type_compatibility", ReadOnly: true,
            (object)new
            {
                name = "check_type_compatibility",
                description = @"Check if one type can be assigned to another. Use before generating assignments or casts.

USAGE: check_type_compatibility(sourceType=""MyDerivedClass"", targetType=""MyBaseClass"")
OUTPUT: compatible (bool), requiresCast (bool), conversionKind, and explanation of why/why not. notice is set when a type name resolved to a solution type that shadows same-named framework types (pass the fully-qualified name to target another).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        sourceType = new { type = "string", description = "The source type name (e.g., 'MyDerivedClass')" },
                        targetType = new { type = "string", description = "The target type name (e.g., 'MyBaseClass')" }
                    },
                    required = new[] { "sourceType", "targetType" }
                }
            },
            (svc, p, cancellationToken) => svc.CheckTypeCompatibilityAsync(
                p.Required<string>("sourceType"),
                p.Required<string>("targetType"),
                cancellationToken)),
            new ToolDefinition("get_instantiation_options", ReadOnly: true,
            (object)new
            {
                name = "get_instantiation_options",
                description = @"Get all ways to create an instance of a type: constructors, static factory methods on the type, every static Create*/Build*/New* method of other source types returning it (externalFactories, each once, sorted), and builder patterns.

USAGE: get_instantiation_options(typeName=""HttpClient"")
OUTPUT: List of constructors with signatures, factory methods, implementsIDisposable (System.IDisposable itself, not a same-named interface), and a hint naming what prevents or shapes instantiation (interface, static class, abstract class, IDisposable). data.notice names framework types the type name also matches.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "The type name to check (e.g., 'HttpClient')" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetInstantiationOptionsAsync(
                p.Required<string>("typeName"),
                cancellationToken)),
            new ToolDefinition("analyze_change_impact", ReadOnly: true,
            (object)new
            {
                name = "analyze_change_impact",
                description = @"Analyze what would break if you change a symbol. Identifies breaking changes before you make them.

USAGE: analyze_change_impact(filePath, line, column, changeType=""rename|changeType|addParameter|removeParameter|changeAccessibility|delete"")
OUTPUT: List of impacted locations — the same reference set find_references reports, each site once — whether change is safe, and specific issues at each location.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number of the symbol" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        changeType = new { type = "string", description = "Type of change, case-insensitive: rename, changeType, addParameter, removeParameter, changeAccessibility, delete; any other value is INVALID_PARAMETER" },
                        newValue = new { type = "string", description = "Optional: new value for rename/changeType" }
                    },
                    required = new[] { "filePath", "line", "column", "changeType" }
                }
            },
            (svc, p, cancellationToken) => svc.AnalyzeChangeImpactAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.Required<string>("changeType"),
                p.Optional<string>("newValue"),
                cancellationToken)),
            new ToolDefinition("get_method_source", ReadOnly: true,
            (object)new
            {
                name = "get_method_source",
                description = @"Get the actual source code of a method by type and method name. Eliminates need for file Read.

USAGE: get_method_source(typeName=""MyService"", methodName=""ProcessData"")
ADDRESSING: methodName may name a method, an operator by its metadata name (op_Addition), a constructor (the type's simple name or "".ctor""), or an explicit interface implementation by the implemented member's name. overloadIndex indexes the overload set get_method_overloads lists: declared overloads first, then inherited ones (isInherited). A name that denotes a non-method is NOT_A_METHOD.
OUTPUT: The source of the part that carries the body (a partial method's implementation): fullSource, bodySource, location {filePath, line, column} of its name, span of the whole declaration, and line count. data.notice names framework types the type name also matches.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "The containing type name (e.g., 'MyService', 'MyController')" },
                        methodName = new { type = "string", description = "The method name (e.g., 'ProcessData'), or the type's simple name / '.ctor' for a constructor" },
                        overloadIndex = new { type = "integer", description = "Which overload to get (0-based, default: 0), as get_method_overloads numbers them" }
                    },
                    required = new[] { "typeName", "methodName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetMethodSourceAsync(
                p.Required<string>("typeName"),
                p.Required<string>("methodName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("get_method_source_batch", ReadOnly: true,
            (object)new
            {
                name = "get_method_source_batch",
                description = @"Get source code for multiple methods in a single call (batch optimization).

USAGE: get_method_source_batch(methods: [{typeName: 'ServiceA', methodName: 'Process'}, {typeName: 'ServiceB', methodName: 'Handle'}])
OUTPUT: Results array with get_method_source's data for each method, plus errors array for any that failed. Every request is answered, so the result is never truncated: totalCount is the number requested, returnedCount the number resolved.
BENEFIT: One call instead of multiple - reduces round trips when tracing code flows.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        methods = new
                        {
                            type = "array",
                            description = "Array of method requests",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    typeName = new { type = "string", description = "Containing type name" },
                                    methodName = new { type = "string", description = "Method name" },
                                    overloadIndex = new { type = "integer", description = "Which overload (0-based, optional)" }
                                },
                                required = new[] { "typeName", "methodName" }
                            }
                        },
                        maxMethods = new { type = "integer", description = "Maximum methods a request may hold, 1-10000 (default: 20); a longer methods array is INVALID_PARAMETER" }
                    },
                    required = new[] { "methods" }
                }
            },
            (svc, p, cancellationToken) => svc.GetMethodSourceBatchAsync(
                ParseMethodBatchRequests(p, "methods"),
                p.Optional("maxMethods", 20),
                cancellationToken)),
            new ToolDefinition("generate_constructor", ReadOnly: true,
            (object)new
            {
                name = "generate_constructor",
                description = @"Generate a constructor from fields and/or properties of a type.

USAGE: Address the type by typeName=""MyService"" (or position on its declaration). Use includeProperties=true to also take every property a constructor can assign: one with a setter (any accessibility, init included) or a get-only auto-property; indexers, explicit interface implementations and computed properties are never taken.
OUTPUT: constructorCode string ready to paste (a member whose name a parameter shares is assigned through this.), parameter list, field and property assignments, and data.notice when the type name also matched framework types.
ADDRESSING: use EITHER typeName OR filePath+line+column (zero-based) - exactly one mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Positional addressing: absolute path to source file containing the type" },
                        line = new { type = "integer", description = "Positional addressing: zero-based line number on the type declaration" },
                        column = new { type = "integer", description = "Positional addressing: zero-based column number" },
                        typeName = new { type = "string", description = "Name-based addressing: type to generate for (fully-qualified, simple, or unique partial name). Use INSTEAD of filePath/line/column." },
                        includeProperties = new { type = "boolean", description = "Include assignable properties: with a setter, or get-only auto-properties (default: false)" },
                        initializeToDefault = new { type = "boolean", description = "Use ?? default for nullable types (default: false)" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GenerateConstructorAsync(
                p.Optional<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                p.Optional("includeProperties", false),
                p.Optional("initializeToDefault", false),
                p.Optional<string>("typeName"),
                cancellationToken)),
            new ToolDefinition("change_signature", ReadOnly: false,
            (object)new
            {
                name = "change_signature",
                description = @"Change a method, constructor or local-function signature and update everything that depends on it.

ACTIONS: add (name + type, optional defaultValue and position; -1 or no position appends), remove, rename (newName), reorder (order lists every kept parameter). Every change is validated first: an unknown action or parameter, a missing field, an invalid name/type/default, or an order C# rejects (required after optional, params not last, extension receiver not first) is an INVALID_PARAMETER error and nothing changes.
BEHAVIOR: the overridden member, overrides, implemented interface members and their implementations change together. A renamed parameter follows every reference (body, nameof, <param>, named arguments) through Roslyn's rename, and is refused on a conflict. Untouched parameters keep their syntax as written (attributes, modifiers, type spelling, default text). Each call's arguments are remapped from how it binds: arguments stay positional while no earlier parameter is skipped, after that they are named (keeping their written order) and added arguments get the defaultValue (or default). A method-group reference (a delegate conversion) is not a call and is listed in unrewrittenReferences, never rewritten.
WORKFLOW: (1) Call with preview=true (default) to see the call sites and how each argument list changes, (2) Review, (3) Call with preview=false to apply.
OUTPUT: oldSignature, newSignature, updatedDeclarations, callSites / callSitesUpdated, transformedCallSites (preview), unrewrittenReferences, notice (the type name also matched framework types), postApplyErrors (apply).
ADDRESSING: use EITHER typeName+memberName OR filePath+line+column (zero-based) - exactly one mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Positional addressing: absolute path to source file containing the method" },
                        line = new { type = "integer", description = "Positional addressing: zero-based line number on the method" },
                        column = new { type = "integer", description = "Positional addressing: zero-based column number" },
                        typeName = new { type = "string", description = "Name-based addressing: type containing the method (fully-qualified, simple, or unique partial name). Use INSTEAD of filePath/line/column." },
                        memberName = new { type = "string", description = "Name-based addressing: method to change (required with typeName). Use the type's simple name or '.ctor' for constructors." },
                        overloadIndex = new { type = "integer", description = "Name-based addressing: zero-based overload index when memberName is overloaded (see get_method_overloads)" },
                        changes = new
                        {
                            type = "array",
                            description = "Array of changes to apply",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    action = new { type = "string", description = "Action: 'add', 'remove', 'rename', or 'reorder' (case-insensitive)" },
                                    name = new { type = "string", description = "Parameter name (for add/remove/rename)" },
                                    type = new { type = "string", description = "Parameter type (for add)" },
                                    newName = new { type = "string", description = "New name (for rename)" },
                                    defaultValue = new { type = "string", description = "Default value (for add); also the argument inserted at existing call sites (default when omitted)" },
                                    position = new { type = "integer", description = "Position to insert (for add) in the parameter list as the earlier changes leave it; -1 or omitted means end" },
                                    order = new { type = "array", items = new { type = "string" }, description = "New parameter order (for reorder): every parameter that is not removed" }
                                },
                                required = new[] { "action" }
                            }
                        },
                        preview = new { type = "boolean", description = "Preview mode (default: true). Set to false to apply changes." }
                    },
                    required = new[] { "changes" }
                }
            },
            (svc, p, cancellationToken) => svc.ChangeSignatureAsync(
                p.Optional<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                ParseSignatureChanges(p, "changes"),
                p.Optional("preview", true),
                p.Optional<string>("typeName"),
                p.Optional<string>("memberName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("extract_method", ReadOnly: false,
            (object)new
            {
                name = "extract_method",
                description = @"Extract selected statements into a new method. Uses Roslyn's data-flow and control-flow analysis of the selection: values read inside become parameters, values assigned inside and used after (the caller's parameters included) flow back as the return value (a tuple for several), and a local declared inside and used after stays declared at the call site. The method is static when its caller cannot supply `this` (static member, static class, static local function or lambda) and async, awaited at the call, when the selection awaits (await foreach and await using included).

USAGE: Specify a startLine/endLine range covering complete statements of one block inside a method. Refused: a range that cuts through a statement or spans statements of different blocks, control flow leaving the range (return, break, continue, goto) or a yield, preprocessor directives, and any extraction whose result would not compile (the errors are listed; nothing is written).
OUTPUT: extractedCode (the new method) and replacementCode (the call) as they will be written, signature, detected parameters and return type.
WORKFLOW: (1) Preview with preview=true, (2) Apply with preview=false.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        startLine = new { type = "integer", description = "Zero-based start line of selection" },
                        endLine = new { type = "integer", description = "Zero-based end line of selection" },
                        methodName = new { type = "string", description = "Name for the new method" },
                        accessibility = new { type = "string", description = "Accessibility: private, public, protected, internal, protected internal or private protected (default: private)" },
                        preview = new { type = "boolean", description = "Preview mode (default: true). Set to false to apply." }
                    },
                    required = new[] { "filePath", "startLine", "endLine", "methodName" }
                }
            },
            (svc, p, cancellationToken) => svc.ExtractMethodAsync(
                p.Required<string>("filePath"),
                p.Required<int>("startLine"),
                p.Required<int>("endLine"),
                p.Required<string>("methodName"),
                p.Optional("accessibility", "private"),
                p.Optional("preview", true),
                cancellationToken)),
        };
    }

    // Code-action wrappers, complexity metrics, null-checks, equality members,
    // batch type-members, and attribute usages.
    private static IEnumerable<ToolDefinition> CodeActionAndMetricsDefinitions()
    {
        return new ToolDefinition[]
        {
            new ToolDefinition("get_code_actions_at_position", ReadOnly: true,
            (object)new
            {
                name = "get_code_actions_at_position",
                description = @"Get ALL available code actions (fixes + refactorings) at a position. This is the master tool that exposes 100+ Roslyn refactorings.

USAGE: get_code_actions_at_position(filePath, line, column) or with selection: add endLine, endColumn
OUTPUT: List of actions with title, kind (fix/refactoring), equivalenceKey
WORKFLOW: (1) Call this to see available actions, (2) Use apply_code_action_by_title to apply one
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        endLine = new { type = "integer", description = "Optional: end line for selection" },
                        endColumn = new { type = "integer", description = "Optional: end column for selection" },
                        includeCodeFixes = new { type = "boolean", description = "Include fixes for diagnostics (default: true)" },
                        includeRefactorings = new { type = "boolean", description = "Include refactorings (default: true)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetCodeActionsAtPositionAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.OptionalStruct<int>("endLine"),
                p.OptionalStruct<int>("endColumn"),
                p.Optional("includeCodeFixes", true),
                p.Optional("includeRefactorings", true),
                cancellationToken)),
            new ToolDefinition("apply_code_action_by_title", ReadOnly: false,
            (object)new
            {
                name = "apply_code_action_by_title",
                description = @"Apply a code action by its title. Supports exact and partial matching.

USAGE: apply_code_action_by_title(filePath, line, column, title)
OUTPUT: Changed files with preview or applied changes
WORKFLOW: (1) Call get_code_actions_at_position first, (2) Apply with preview=true, (3) Apply with preview=false
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        title = new { type = "string", description = "Action title (exact or partial match)" },
                        endLine = new { type = "integer", description = "Optional: end line for selection" },
                        endColumn = new { type = "integer", description = "Optional: end column for selection" },
                        preview = new { type = "boolean", description = "Preview mode (default: true). Set to false to apply." }
                    },
                    required = new[] { "filePath", "line", "column", "title" }
                }
            },
            (svc, p, cancellationToken) => svc.ApplyCodeActionByTitleAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.Required<string>("title"),
                p.OptionalStruct<int>("endLine"),
                p.OptionalStruct<int>("endColumn"),
                p.Optional("preview", true),
                cancellationToken)),
            new ToolDefinition("implement_missing_members", ReadOnly: false,
            (object)new
            {
                name = "implement_missing_members",
                description = @"Generate stub implementations for interface/abstract members.

USAGE: Position cursor on the class declaration (its name, keyword, modifiers or base list) that implements an interface or extends an abstract class: every member the compiler reports missing on its base list is implemented. On an interface name in the base list, that interface is implemented.
OUTPUT: Generated stub code for all missing members.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number on the class declaration" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        preview = new { type = "boolean", description = "Preview mode (default: true)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.ImplementMissingMembersAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.Optional("preview", true),
                cancellationToken)),
            new ToolDefinition("encapsulate_field", ReadOnly: false,
            (object)new
            {
                name = "encapsulate_field",
                description = @"Convert a field to a property with getter/setter.

USAGE: Position cursor on a field declaration.
OUTPUT: Generated property wrapping the field. The field's uses switch to the property, except where the property cannot take them (passed by ref or out), which keep the field; when that would not compile, the uses all keep the field; when neither compiles, nothing is applied and the errors are returned.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number on the field" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        preview = new { type = "boolean", description = "Preview mode (default: true)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.EncapsulateFieldAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.Optional("preview", true),
                cancellationToken)),
            new ToolDefinition("inline_variable", ReadOnly: false,
            (object)new
            {
                name = "inline_variable",
                description = @"Inline a variable, replacing all usages with its value (Roslyn's inline-temporary refactoring only; never another inline action such as inline method).

USAGE: Position cursor on a variable declaration or usage.
OUTPUT: Variable removed and all usages replaced with the expression.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        preview = new { type = "boolean", description = "Preview mode (default: true)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.InlineVariableAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.Optional("preview", true),
                cancellationToken)),
            new ToolDefinition("extract_variable", ReadOnly: false,
            (object)new
            {
                name = "extract_variable",
                description = @"Extract an expression to a local variable (Roslyn's ""Introduce local"" refactoring only: where only a field, constant or parameter could be introduced, nothing is applied).

USAGE: Position cursor on or select an expression.
OUTPUT: Expression extracted to a new local variable.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        endLine = new { type = "integer", description = "Optional: end line for selection" },
                        endColumn = new { type = "integer", description = "Optional: end column for selection" },
                        preview = new { type = "boolean", description = "Preview mode (default: true)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.ExtractVariableAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.OptionalStruct<int>("endLine"),
                p.OptionalStruct<int>("endColumn"),
                p.Optional("preview", true),
                cancellationToken)),
            new ToolDefinition("get_complexity_metrics", ReadOnly: true,
            (object)new
            {
                name = "get_complexity_metrics",
                description = @"Get complexity metrics for a method or entire file.

METRICS: cyclomatic (1 + decision points: if, ?:, ?., ??, ??=, loops, catch and catch filters, case labels, non-discard switch-expression arms, `when` clauses, &&, ||, pattern and/or), nesting (max depth of nested control structures; an else-if chain is one level), loc (lines), parameters (count), cognitive (Sonar: structural increments plus nesting; switch expressions count, lambdas and local functions nest their bodies)
USAGE: get_complexity_metrics(filePath) for file, or add line/column for specific method
OUTPUT: Per-member breakdown with all requested metrics, members named as C# declares them (a constructor by its type name, 'operator +', 'this[].get') at the line of their name
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Optional: zero-based line for specific method" },
                        column = new { type = "integer", description = "Optional: zero-based column" },
                        metrics = new { type = "array", items = new { type = "string" }, description = "Optional: specific metrics, one or more of [cyclomatic, nesting, loc, parameters, cognitive] (case-insensitive); an unknown name or an empty list is an INVALID_PARAMETER error. Omit for all." }
                    },
                    required = new[] { "filePath" }
                }
            },
            (svc, p, cancellationToken) => svc.GetComplexityMetricsAsync(
                p.Required<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                p.OptionalStringArray("metrics"),
                cancellationToken)),
            new ToolDefinition("add_null_checks", ReadOnly: false,
            (object)new
            {
                name = "add_null_checks",
                description = @"Add null-check guard clauses for the method's non-nullable reference-type parameters: ArgumentNullException.ThrowIfNull where every target framework of the project has it, an if-null-throw guard otherwise.

USAGE: Address the method by typeName+memberName (or position cursor on its declaration).
SKIPPED: out parameters (skippedOut), parameters whose contract allows null — annotated nullable, nullable value types, or a null default (skippedNullable) — and parameters the body already guards (skippedAlreadyGuarded).
OUTPUT: Generated guard clauses inserted at method start, and data.notice when the type name also matched framework types.
ADDRESSING: use EITHER typeName+memberName OR filePath+line+column (zero-based) - exactly one mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Positional addressing: absolute path to source file" },
                        line = new { type = "integer", description = "Positional addressing: zero-based line number on the method" },
                        column = new { type = "integer", description = "Positional addressing: zero-based column number" },
                        typeName = new { type = "string", description = "Name-based addressing: type containing the method (fully-qualified, simple, or unique partial name). Use INSTEAD of filePath/line/column." },
                        memberName = new { type = "string", description = "Name-based addressing: method to guard (required with typeName)" },
                        overloadIndex = new { type = "integer", description = "Name-based addressing: zero-based overload index when memberName is overloaded (see get_method_overloads)" },
                        preview = new { type = "boolean", description = "Preview mode (default: true)" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.AddNullChecksAsync(
                p.Optional<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                p.Optional("preview", true),
                p.Optional<string>("typeName"),
                p.Optional<string>("memberName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("generate_equality_members", ReadOnly: false,
            (object)new
            {
                name = "generate_equality_members",
                description = @"Generate Equals, GetHashCode, and == / != operators for a type.

USAGE: Address the type by typeName=""MyValue"" (or position cursor on its declaration).
OUTPUT: Generated equality members comparing all instance fields and properties with stored state (never indexers or explicit interface implementations). A member is compared with == where its type's == is a reflexive value comparison (integral types, bool, char, decimal, string, enums), else with EqualityComparer<T>.Default. GetHashCode uses System.HashCode where every target framework of the project has it, a multiplicative hash otherwise. data.notice reports when the type name also matched framework types.
ADDRESSING: use EITHER typeName OR filePath+line+column (zero-based) - exactly one mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Positional addressing: absolute path to source file" },
                        line = new { type = "integer", description = "Positional addressing: zero-based line number on the type" },
                        column = new { type = "integer", description = "Positional addressing: zero-based column number" },
                        typeName = new { type = "string", description = "Name-based addressing: type to generate for (fully-qualified, simple, or unique partial name). Use INSTEAD of filePath/line/column." },
                        includeOperators = new { type = "boolean", description = "Include == and != operators (default: true)" },
                        preview = new { type = "boolean", description = "Preview mode (default: true)" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GenerateEqualityMembersAsync(
                p.Optional<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                p.Optional("includeOperators", true),
                p.Optional("preview", true),
                p.Optional<string>("typeName"),
                cancellationToken)),
            new ToolDefinition("get_type_members_batch", ReadOnly: true,
            (object)new
            {
                name = "get_type_members_batch",
                description = @"Get members for multiple types in a single call (batch optimization).

USAGE: get_type_members_batch(typeNames: ['ServiceA', 'ServiceB', 'ControllerC'])
OUTPUT: One entry per requested name, in request order: get_type_members' data, or the error if the type is not found. Every request is answered, so the result is never truncated: totalCount is the number requested, returnedCount the number resolved.
BENEFIT: One call instead of multiple - reduces context usage for AI agents",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeNames = new { type = "array", items = new { type = "string" }, description = "Array of type names to look up" },
                        includeInherited = new { type = "boolean", description = "Include inherited members (default: false)" },
                        memberKind = new { type = "string", description = "Filter, case-insensitive: Method, Constructor, StaticConstructor, Finalizer, Property, Indexer, Field, Event, Operator, Conversion, NestedType; any other value is INVALID_PARAMETER" },
                        verbosity = new { type = "string", description = "'summary', 'compact' (default), or 'full'; any other value is INVALID_PARAMETER" },
                        maxResultsPerType = new { type = "integer", description = "Max members per type, 1-10000 (default: 50)" }
                    },
                    required = new[] { "typeNames" }
                }
            },
            (svc, p, cancellationToken) => svc.GetTypeMembersBatchAsync(
                p.RequiredStringArray("typeNames"),
                p.Optional("includeInherited", false),
                p.Optional<string>("memberKind"),
                p.Optional("verbosity", "compact"),
                p.Optional("maxResultsPerType", 50),
                cancellationToken)),
            new ToolDefinition("find_attribute_usages", ReadOnly: true,
            (object)new
            {
                name = "find_attribute_usages",
                description = @"Find every application of an attribute — the same applications get_attributes walks: the assembly or module, types, members, accessors, parameters, return values, type parameters, lambdas and local functions, backing fields ([field:]) and record parameter properties ([property:]).

USAGE: find_attribute_usages(attributeName: ""Authorize"")
USAGE: find_attribute_usages(attributeName: ""HttpGet"", projectName: ""MyApi"")

OUTPUT: List of applications in attributeLocation order — the symbol, its kind, the C# attribute target, arguments, the symbol's location and where the attribute is written — one per application (a multi-targeted project's once, with targetFrameworks).
Use for: finding all API endpoints, authorization points, serialization config, test fixtures.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        attributeName = new { type = "string", description = "Attribute name, case-insensitive, with or without the Attribute suffix, simple or fully-qualified (e.g. 'Authorize', 'HttpGet', 'System.ObsoleteAttribute')" },
                        projectName = new { type = "string", description = "Filter to one project (case-insensitive; 'Name' covers every target framework, 'Name(net8.0)' one); an unknown name is INVALID_PARAMETER" },
                        maxResults = new { type = "integer", description = "Maximum results, 1-10000 (default: 100)" }
                    },
                    required = new[] { "attributeName" }
                }
            },
            (svc, p, cancellationToken) => svc.FindAttributeUsagesAsync(
                p.Required<string>("attributeName"),
                p.Optional<string>("projectName"),
                p.Optional("maxResults", 100),
                cancellationToken)),
        };
    }

    // DI/reflection/dependency discovery, generated code, and the project-health
    // / coupling / coverage quality tools through diff_api_surface.
    private static IEnumerable<ToolDefinition> DiscoveryAndQualityDefinitions()
    {
        return new ToolDefinition[]
        {
            new ToolDefinition("get_di_registrations", ReadOnly: true,
            (object)new
            {
                name = "get_di_registrations",
                description = @"Scan for Microsoft.Extensions.DependencyInjection service registrations: AddTransient/AddScoped/AddSingleton and their AddKeyed* forms, TryAdd* and TryAddKeyed*, TryAddEnumerable, Add/TryAdd/Replace of a ServiceDescriptor (or a collection of them, including services.Add(descriptor)), and AddHostedService — also through a null-conditional receiver (services?.AddX). The registration API is recognized by its own declaring types, never by a method name alone.

USAGE: get_di_registrations()
USAGE: get_di_registrations(projectName: ""MyApi"")

OUTPUT: List of DI registrations in source order with lifetime, service type, implementation type (from the type arguments, typeof arguments, a factory lambda's produced type, an instance's type, or the descriptor), and location; each once (a multi-targeted project's with targetFrameworks).
Use for: understanding service wiring, finding missing registrations, auditing lifetimes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Filter to one project (case-insensitive; 'Name' covers every target framework, 'Name(net8.0)' one); an unknown name is INVALID_PARAMETER" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetDiRegistrationsAsync(
                p.Optional<string>("projectName"),
                cancellationToken)),
            new ToolDefinition("find_reflection_usage", ReadOnly: true,
            (object)new
            {
                name = "find_reflection_usage",
                description = @"Detect dynamic/reflection-based type and method usage that is invisible to static reference searches.

USAGE: find_reflection_usage()
USAGE: find_reflection_usage(projectName: ""MyApp"", maxResults: 50)

DETECTS: everything in System.Reflection; System.Type members that return reflection metadata (GetMethod(s), GetProperty(ies), GetField(s), GetEvent(s), GetMember(s), GetConstructor(s), GetInterface(s), GetNestedType(s), Type.GetType, MakeGenericType, ...) or invoke a member (InvokeMember); Activator; AppDomain.CreateInstance*; Delegate.DynamicInvoke; and Object.GetType(), the low-signal runtime-type query, listed last.
OUTPUT: List of reflection API calls with the API used, context, and location, each once (a multi-targeted project's with targetFrameworks), in source order after the high-signal/low-signal split.
Use for: finding hidden dependencies before refactoring, security audits, understanding dynamic behavior.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Filter to one project (case-insensitive; 'Name' covers every target framework, 'Name(net8.0)' one); an unknown name is INVALID_PARAMETER" },
                        maxResults = new { type = "integer", description = "Maximum results, 1-10000 (default: 100)" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindReflectionUsageAsync(
                p.Optional<string>("projectName"),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("find_circular_dependencies", ReadOnly: true,
            (object)new
            {
                name = "find_circular_dependencies",
                description = @"Detect cycles in project or namespace dependency graphs.

USAGE: find_circular_dependencies() — project-level cycles (one node per project file, as dependency_graph)
USAGE: find_circular_dependencies(level: ""namespace"") — namespace-level cycles: N depends on M where code in N uses a type (or a member of a type) declared in M, through any spelling — qualified names and aliases included; using directives alone are not dependencies

OUTPUT: The graph as nodes and edges, cycles (every elementary cycle, each starting and ending at its smallest node id; cyclesTruncated when there are more than 1000) and stronglyConnectedComponents (every set of mutually dependent nodes, always complete).
Use for: architecture analysis, identifying tightly coupled components.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        level = new { type = "string", description = "'project' (default) or 'namespace', case-insensitive; any other value is INVALID_PARAMETER" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindCircularDependenciesAsync(
                p.Optional<string>("level"),
                cancellationToken)),
            new ToolDefinition("get_nuget_dependencies", ReadOnly: true,
            (object)new
            {
                name = "get_nuget_dependencies",
                description = @"List NuGet package references per project file with versions, as MSBuild evaluates them (imports, Conditions, property versions, Central Package Management).

USAGE: get_nuget_dependencies()
USAGE: get_nuget_dependencies(projectName: ""MyApp"")

OUTPUT: One entry per project file with its packages (in evaluation order), versions, and asset settings. A multi-targeted project is one entry evaluated for each target framework: the entry and each package carry targetFrameworks, the frameworks a package applies to (identical references merged).
Use for: dependency audits, version checks, understanding external dependencies.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Filter to one project (case-insensitive; 'Name' covers every target framework, 'Name(net8.0)' one); an unknown name is INVALID_PARAMETER" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetNuGetDependenciesAsync(
                p.Optional<string>("projectName"),
                cancellationToken)),
            new ToolDefinition("get_source_generators", ReadOnly: true,
            (object)new
            {
                name = "get_source_generators",
                description = @"List active source generators and their generated output per project.

USAGE: get_source_generators()
USAGE: get_source_generators(projectName: ""MyApp"")

OUTPUT: List of generators per project (per target framework of a multi-targeted project) with their assembly info and timing, and the generated files in path order (hintPath formatted like every reported path).
Use for: understanding generated code, debugging generator issues.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Filter to one project (case-insensitive; 'Name' covers every target framework, 'Name(net8.0)' one); an unknown name is INVALID_PARAMETER" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetSourceGeneratorsAsync(
                p.Optional<string>("projectName"),
                cancellationToken)),
            new ToolDefinition("get_generated_code", ReadOnly: true,
            (object)new
            {
                name = "get_generated_code",
                description = @"View the source code produced by a source generator.

USAGE: get_generated_code(projectName: ""MyApp"", generatedFileName: ""MyType.g.cs"")

MATCHING: generatedFileName is the file name, the trailing whole segments of the generated path (""MyGenerator/MyType.g.cs""), or the hintPath get_source_generators reports; a partial name never matches. Several matching files (or one whose content differs between a multi-targeted project's frameworks) are AMBIGUOUS_MATCH listing the candidates; none is FILE_NOT_FOUND listing the generated file names.
OUTPUT: Full source code of the generated file, its hintPath, and targetFrameworks for a multi-targeted project.
Use get_source_generators first to discover available generated files.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Project containing the generated file (case-insensitive; 'Name' covers every target framework, 'Name(net8.0)' one); an unknown name is INVALID_PARAMETER" },
                        generatedFileName = new { type = "string", description = "File name or trailing path segments of the generated file" }
                    },
                    required = new[] { "projectName", "generatedFileName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetGeneratedCodeAsync(
                p.Required<string>("projectName"),
                p.Required<string>("generatedFileName"),
                cancellationToken)),
            new ToolDefinition("get_project_health", ReadOnly: true,
            (object)new
            {
                name = "get_project_health",
                description = "Composite audit dashboard for one project: aggregates diagnostics (with analyzers), unused code, god-object candidates, untested public surface, and async-hygiene issues. Returns counts + top-N hotspots per dimension; the breakdowns (diagnostics by id, unused symbols by kind) are computed from all the data, not the hotspot pages. Every section scans the same target frameworks: all of them for a multi-targeted project's plain name, one for a framework-qualified name. One call replaces 5+ separate tool calls for a project audit overview.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Project to audit ('Multi' for every framework of a multi-targeted project, 'Multi(net8.0)' for one). Required." },
                        includeAnalyzers = new { type = "boolean", description = "Run configured DiagnosticAnalyzers in addition to compiler diagnostics (default: true)." },
                        topN = new { type = "integer", description = "Hotspots per dimension, 1-100 (default: 5)." }
                    },
                    required = new[] { "projectName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetProjectHealthAsync(
                p.Required<string>("projectName"),
                p.Optional("includeAnalyzers", true),
                p.Optional("topN", 5),
                cancellationToken)),
            new ToolDefinition("find_god_objects", ReadOnly: true,
            (object)new
            {
                name = "find_god_objects",
                description = "Detect over-coupled types ('god objects'). For each source type computes efferent coupling (distinct in-solution types it depends on - the check_architecture edge set: base types, attributes, signatures and every type its bodies use, generic arguments included; a nested type's body counts for the nested type only), afferent coupling (distinct types in the whole solution that depend on it, even when projectName scopes the report), and member count (the members a reader sees: no accessors, no compiler-generated members, no nested types); reports types meeting both threshold filters, scored as efferent*0.4 + members*0.4 + afferent*0.2. A multi-targeted project's types are reported once. AUDIT-TIME tool — O(types*references); don't call in tight agent loops.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Limit the reported types to one project: its plain name (every target framework) or a framework-qualified name like 'Multi(net8.0)' (default: all projects)" },
                        minEfferentCoupling = new { type = "integer", description = "Minimum outbound type dependencies to qualify, 0-100000 (default: 20)" },
                        minMembers = new { type = "integer", description = "Minimum member count to qualify, 0-100000 (default: 20)" },
                        maxResults = new { type = "integer", description = "Cap on candidates returned, 1-1000 (default: 20). totalCount reflects unbounded match count." }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindGodObjectsAsync(
                p.Optional<string>("projectName"),
                p.Optional("minEfferentCoupling", 20),
                p.Optional("minMembers", 20),
                p.Optional("maxResults", 20),
                cancellationToken)),
            new ToolDefinition("find_untested_code", ReadOnly: true,
            (object)new
            {
                name = "find_untested_code",
                description = @"Find public/internal surface in a project that no test transitively reaches. Sorted by cyclomatic complexity so the riskiest untested code is reported first.

ENTRY POINTS: test methods by each framework's discovery contract - xUnit attributes deriving from [Fact] (incl. [Theory], [SkippableFact]) or implementing v3's IFactAttribute, NUnit attributes implementing ITestBuilder/ISimpleTestBuilder ([Test], [TestCase], [Theory], ...), MSTest attributes deriving from [TestMethod], TUnit [Test]; Reqnroll/SpecFlow step bindings and hooks; assembly-scoped set-up (MSTest AssemblyInitialize, NUnit SetUpFixture, TUnit Before(Assembly), xUnit v3 assembly fixtures). Each test also runs its class's lifecycle code: constructors and field initializers, set-up/tear-down (incl. on base classes), xUnit class/collection fixtures, IAsyncLifetime and Dispose.
WALK: calls, constructors (incl. : base/: this and implicit base calls) and member initializers, the property/indexer/event accessor actually used, operators and conversions, and the implicit calls of foreach, using, deconstruction and await, across project references.
OUTPUT: testMethodCount, entryPointCounts {tests, stepBindings, hooks}, uncoveredSymbols[] {fullName, kind, complexity, accessibility, location, reason}. reason is ""Not reachable from any test method"", or ""Reached only via interface or virtual dispatch"" when a test calls an interface or base member the symbol implements or overrides but never the symbol itself (another implementation or a mock may be what runs).
NOTE: audit-time tool; runs a solution-wide walk.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Project to audit (default: first project in solution)" },
                        includeProperties = new { type = "boolean", description = "Include properties (default: true)" },
                        includeInternal = new { type = "boolean", description = "Include internal members (default: false; only public)" },
                        maxResults = new { type = "integer", description = "Cap on reported items, 1-1000 (default: 50). totalCount reflects the true unbounded count." }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindUntestedCodeAsync(
                p.Optional<string>("projectName"),
                p.Optional("includeProperties", true),
                p.Optional("includeInternal", false),
                p.Optional("maxResults", 50),
                cancellationToken)),
            new ToolDefinition("get_call_graph", ReadOnly: true,
            (object)new
            {
                name = "get_call_graph",
                description = "Multi-hop transitive call graph (callers, callees, or both) from a method, constructor, accessor or operator. Replaces N round-trips of find_callers/get_outgoing_calls when tracing impact or understanding a subsystem, and agrees with them: the same calls (constructors, accessors, extension and generic calls as one node per declaration, lambdas and initializers attributed to their containing members), and interface or virtual calls fanned out to the implementations the receiver type can run. 'both' is the callers tree of the root united with its callees tree. Returns nodes + edges {from, to, kind, viaDispatch} (not a nested tree, to handle diamond shapes), with cycle detection and a mandatory maxDepth bound; each level's new nodes are added in canonical order, so ids and the nodes kept at maxNodes are stable. IMPORTANT: Uses ZERO-BASED coordinates.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "File containing the root method" },
                        line = new { type = "integer", description = "Zero-based line of the method declaration or invocation" },
                        column = new { type = "integer", description = "Zero-based column" },
                        direction = new { type = "string", description = "'callees' (default), 'callers', or 'both'" },
                        maxDepth = new { type = "integer", description = "Max BFS depth, 1-10 (default: 3). Required bound; depths above 10 explode on connected graphs." },
                        maxNodes = new { type = "integer", description = "Max total nodes, 1-10000 (default: 100). Sets truncatedByNodes when hit." }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (svc, p, cancellationToken) => svc.GetCallGraphAsync(
                p.Required<string>("filePath"),
                p.Required<int>("line"),
                p.Required<int>("column"),
                p.Optional("direction", "callees"),
                p.Optional("maxDepth", 3),
                p.Optional("maxNodes", 100),
                cancellationToken)),
            new ToolDefinition("find_path_between", ReadOnly: true,
            (object)new
            {
                name = "find_path_between",
                description = @"Point-to-point reachability over the call graph: can any source method reach any target method, and through what call chain? Fills the gap between get_call_graph (single-source) and find_callers/get_outgoing_calls (one hop). A neutral mechanism - you name the endpoints and any barriers; it has no built-in notion of what matters.

USAGE: find_path_between(sources: [{typeName, memberName}], targets: [{typeName, memberName}]). A memberName names a method (every overload), a property or event (its accessors), or the constructors ('.ctor' or the type's simple name). Optional avoidThrough (barrier methods the path may not cross), mustPassThrough (a single required checkpoint; maxHops and maxNodes then bound the whole path through it), maxHops (1-12 edges, default 8), maxNodes (1-10000, default 500).
OUTPUT: per source/target pair, reachable + the ordered hops (symbol + location) of a shortest connecting chain and hopCount (its edges); or reachable:false with hitHopLimit (raise maxHops) vs. a genuinely disconnected pair, the frontier sizes, and with mustPassThrough the failedLeg (sourceToCheckpoint or checkpointToTarget). notice carries any endpoint type name's shadowing of framework types. Among equally short paths the same one is reported on every run.
GRAPH: the call graph every call tool shares - constructors, accessors, extension and generic calls are edges, a lambda or local function is part of its containing member, and an interface or virtual call reaches the implementations its receiver's static type can run (a sealed or unrelated receiver reaches none).
LIMITS: over-approximate CONTROL reachability - a path is 'could reach' not 'will'; blind to reflection, delegates dispatched through fields, and DI-by-string; stops at the metadata boundary (a path to a BCL/NuGet member means the app calls it).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        sources = new
                        {
                            type = "array",
                            description = "Entry-point methods to search from. Each { typeName, memberName }; a member name matches all its overloads.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    typeName = new { type = "string", description = "Containing type (fully-qualified, simple, or unique partial name)" },
                                    memberName = new { type = "string", description = "Method name" }
                                },
                                required = new[] { "typeName", "memberName" }
                            }
                        },
                        targets = new
                        {
                            type = "array",
                            description = "Target methods to reach. Each { typeName, memberName }.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    typeName = new { type = "string", description = "Containing type (fully-qualified, simple, or unique partial name)" },
                                    memberName = new { type = "string", description = "Method name" }
                                },
                                required = new[] { "typeName", "memberName" }
                            }
                        },
                        avoidThrough = new
                        {
                            type = "array",
                            description = "Optional barrier methods: a reported path never traverses these (graph cut). Each { typeName, memberName }.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    typeName = new { type = "string", description = "Containing type" },
                                    memberName = new { type = "string", description = "Method name" }
                                },
                                required = new[] { "typeName", "memberName" }
                            }
                        },
                        mustPassThrough = new
                        {
                            type = "array",
                            description = "Optional single checkpoint the path must cross (source reaches it, and it reaches the target). More than one is rejected.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    typeName = new { type = "string", description = "Containing type" },
                                    memberName = new { type = "string", description = "Method name" }
                                },
                                required = new[] { "typeName", "memberName" }
                            }
                        },
                        maxHops = new { type = "integer", description = "Max path length in edges, 1-12 (default: 8)." },
                        maxNodes = new { type = "integer", description = "Node budget across both search frontiers, 1-10000 (default: 500). Sets hitHopLimit/truncated when hit." }
                    },
                    required = new[] { "sources", "targets" }
                }
            },
            (svc, p, cancellationToken) => svc.FindPathBetweenAsync(
                ParseMemberRefs(p, "sources", required: true),
                ParseMemberRefs(p, "targets", required: true),
                ParseMemberRefs(p, "avoidThrough", required: false),
                ParseMemberRefs(p, "mustPassThrough", required: false),
                p.Optional("maxHops", 8),
                p.Optional("maxNodes", 500),
                cancellationToken)),
            new ToolDefinition("get_external_type_info", ReadOnly: true,
            (object)new
            {
                name = "get_external_type_info",
                description = "Inspect types from external assemblies (NuGet packages, BCL, closed-source) the solution references. Returns the type's public and protected surface — each member once, labeled with get_type_members' kind vocabulary; never property/event accessors, compiler-generated members, inherited constructors, or explicit interface implementations (reachable only through the interface) — with signatures and structured XML docs (summary/returns/remarks/params/typeParams/exceptions) for types whose source isn't in the solution. Eliminates the need for agents to guess at dependency APIs.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Fully-qualified type name. Use backtick-arity for generics, e.g. 'System.Collections.Generic.List`1'." },
                        assemblyName = new { type = "string", description = "Optional: restrict resolution to a specific assembly when the type name is ambiguous." },
                        includeInherited = new { type = "boolean", description = "Include members inherited from base classes, flagged isInherited (default: true). Stops before System.Object." },
                        includeXmlDocs = new { type = "boolean", description = "Include XML doc summaries when available (default: true)." },
                        maxMembers = new { type = "integer", description = "Cap on member entries, 1-10000 (default: 200). totalCount reflects the unbounded member count." }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetExternalTypeInfoAsync(
                p.Required<string>("typeName"),
                p.Optional<string>("assemblyName"),
                p.Optional("includeInherited", true),
                p.Optional("includeXmlDocs", true),
                p.Optional("maxMembers", 200),
                cancellationToken)),
            new ToolDefinition("move_type_to_file", ReadOnly: true,
            (object)new
            {
                name = "move_type_to_file",
                description = @"Compute the file split that moves a type into its own correctly-named file ({TypeName}.cs in the same folder). The remedy tool for files that accumulate multiple types.

USAGE: move_type_to_file(typeName: ""MySecondClass"") - the new file holds the type in its namespace shape (file-scoped or block) with only the usings it uses; the origin's other types, top-level statements, assembly attributes and global usings stay behind. Preprocessor directives and the file header around a removed declaration stay balanced and in place in both files; a using the move left unused in the origin is removed.
OUTPUT: newFilePath plus the full newFileContent and updatedOriginContent, and data.notice when the type name also matched framework types. GENERATION-ONLY: never writes - YOU write both files (Write/Edit), then call sync_documents. A type already alone in its file is a no-op success.
Nested types and multi-file partial types are not supported.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type to move (fully-qualified, simple, or unique partial name)" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.MoveTypeToFileAsync(
                p.Required<string>("typeName"),
                cancellationToken)),
            new ToolDefinition("split_type", ReadOnly: true,
            (object)new
            {
                name = "split_type",
                description = @"Compute the partial-class split that moves the named members of a class or struct into a new file. The remedy tool for find_god_objects.

USAGE: split_type(typeName: ""MyService"", memberNames: [""HelperA"", ""HelperB""]). All overloads of a listed name move together; the type's simple name addresses constructors.
OUTPUT: newFilePath, movedMembers, partialModifierAdded, plus the full newFileContent and updatedOriginContent, and data.notice when the type name also matched framework types. Both halves carry the `partial` modifier; the new half carries no attributes, doc comment, primary-constructor parameter list or base list, and keeps the directives around the type. GENERATION-ONLY: never writes - YOU write both files (Write/Edit), then call sync_documents.
Records, nested types, and multi-file partial types are not supported.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Class or struct to split (fully-qualified, simple, or unique partial name)" },
                        memberNames = new { type = "array", items = new { type = "string" }, description = "Members to move into the new partial. A field declared together with other variables moves only when all of them are listed." },
                        newFileName = new { type = "string", description = "Bare file name for the new partial (default: '{TypeName}.Partial.cs'; '.cs' appended when missing)" }
                    },
                    required = new[] { "typeName", "memberNames" }
                }
            },
            (svc, p, cancellationToken) => svc.SplitTypeAsync(
                p.Required<string>("typeName"),
                p.RequiredStringArray("memberNames"),
                p.Optional<string>("newFileName"),
                cancellationToken)),
            new ToolDefinition("diff_api_surface", ReadOnly: true,
            (object)new
            {
                name = "diff_api_surface",
                description = @"Public-API breaking-change report against a git ref - the release-prep tool. Rebuilds the baseline API surface from git history (read-only git ls-tree/cat-file): the files the project compiled at the ref - its project file at the ref evaluated by MSBuild (globs, Compile Remove, linked Compile Include) - with the project's source generators run on it like on the current code, and diffs it against the current code.

USAGE: diff_api_surface(baselineRef: ""v1.5.3"") or any ref (""HEAD~5"", a branch, a commit). Optional projectName restricts to one project ('Multi' for every framework of a multi-targeted project, 'Multi(net8.0)' for one).
OUTPUT: breaking[] (removed symbols and changed signatures, including accessibility reductions) and added[] - locations on the current side; a multi-targeted project's changes are listed once with targetFrameworks. Members of a wholly removed or added type collapse into the single type entry.
REQUIRES: the solution must live in a git repository and the ref must exist locally.
NOTE: compares declaration signatures; base-list and attribute changes are not reported.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        baselineRef = new { type = "string", description = "Git ref to diff against: tag, branch, or commit (e.g. 'v1.5.3', 'HEAD~5')" },
                        projectName = new { type = "string", description = "Optional: restrict the comparison to one project (default: every project in the solution); an unknown name is an INVALID_PARAMETER error" }
                    },
                    required = new[] { "baselineRef" }
                }
            },
            (svc, p, cancellationToken) => svc.DiffApiSurfaceAsync(
                p.Required<string>("baselineRef"),
                p.Optional<string>("projectName"),
                cancellationToken)),
        };
    }

    // Exception/call flow, similar-code, stack traces, the remaining unimplemented-
    // behavior tools, and the fine-grained type-usage searches.
    private static IEnumerable<ToolDefinition> FlowAndSearchDefinitions()
    {
        return new ToolDefinition[]
        {
            new ToolDefinition("get_exception_flow", ReadOnly: true,
            (object)new
            {
                name = "get_exception_flow",
                description = @"Which exception types can escape a method (transitively through its in-source callees), and where each is caught along the way. C# has no checked exceptions - nothing else answers this.

USAGE: get_exception_flow(typeName: ""OrderService"", memberName: ""Submit"") or positional filePath+line+column.
OUTPUT: escaping[] (exceptionType, a sample throw site, viaDispatch) and caught[] (exceptionType, the catch location in the analyzed method, the originating throw site, viaDispatch); analyzedMembers (the method, then the in-source callees whose bodies were walked); truncatedByDepth; notice (the type name's shadowing of framework types, when any).
WALK: the call edges every call tool shares - ordinary, extension and generic calls, constructors with the initializers they run, the accessor a property/indexer use runs (getter and setter for ++, -- and compound assignment), operators, partial implementations, and, flagged viaDispatch, the implementations an interface or virtual call's receiver type can run. A lambda's throws are matched against the try/catch around it; a local function's at each of its call sites (an uncalled one contributes nothing). A catch that rethrows (`throw;` or `throw e;` of its own variable) absorbs nothing: the original exception escapes with its original throw site.
LIMITS: metadata-only callees (BCL/NuGet) contribute nothing; a catch with a `when` filter is treated as non-catching (conservative); async/Task wrapping is not modeled.
ADDRESSING: use EITHER typeName+memberName OR filePath+line+column (zero-based) - exactly one mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Positional addressing: absolute path to source file" },
                        line = new { type = "integer", description = "Positional addressing: zero-based line number on the method" },
                        column = new { type = "integer", description = "Positional addressing: zero-based column number" },
                        typeName = new { type = "string", description = "Name-based addressing: type containing the method (fully-qualified, simple, or unique partial name). Use INSTEAD of filePath/line/column." },
                        memberName = new { type = "string", description = "Name-based addressing: method to analyze (required with typeName)" },
                        overloadIndex = new { type = "integer", description = "Name-based addressing: zero-based overload index when memberName is overloaded (see get_method_overloads)" },
                        maxDepth = new { type = "integer", description = "How many callee levels to follow, 1-10 (default: 3). Depth 1 analyzes only the method's own throws." }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetExceptionFlowAsync(
                p.Optional<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                p.Optional("maxDepth", 3),
                p.Optional<string>("typeName"),
                p.Optional<string>("memberName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("find_similar_code", ReadOnly: true,
            (object)new
            {
                name = "find_similar_code",
                description = @"Find methods structurally similar to a query method or snippet - the anti-duplication check before implementing a helper that may already exist. Fingerprints are 4-gram shingles over the normalized token kinds of a body's content (statements or expression, without the enclosing braces or `=>`; a snippet with or without braces reads the same), identifiers and literals as wildcards, scored by Jaccard similarity, so renamed variables and different constants still match.

USAGE: find_similar_code(typeName: ""OrderService"", methodName: ""Validate"") or find_similar_code(snippet: ""<code>"") - exactly one query mode.
OUTPUT: matching members with score (0-1), location, and statement count (1 for an expression body), best first; notice is set when typeName resolved to a solution type that shadows same-named framework types; each candidate once (targetFrameworks for a multi-targeted project), never the query method itself. Candidates are methods, constructors, destructors, operators, conversions, local functions, accessors and expression-bodied properties/indexers; a block body needs at least 3 statements, an expression body enough tokens to form a shingle.
NOTE: audit-time tool - every call sweeps the solution once; there is no persistent index.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Query by method: type containing it (fully-qualified, simple, or unique partial name)" },
                        methodName = new { type = "string", description = "Query by method: method name within typeName" },
                        overloadIndex = new { type = "integer", description = "Query by method: zero-based overload index when methodName is overloaded" },
                        snippet = new { type = "string", description = "Query by raw code instead of an existing method. Use INSTEAD of typeName/methodName." },
                        threshold = new { type = "number", description = "Minimum similarity score 0-1 to report (default: 0.75)" },
                        maxResults = new { type = "integer", description = "Maximum matches to return, best first, 1-1000 (default: 10)" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindSimilarCodeAsync(
                p.Optional<string>("typeName"),
                p.Optional<string>("methodName"),
                p.OptionalStruct<int>("overloadIndex"),
                p.Optional<string>("snippet"),
                p.Optional("threshold", 0.75),
                p.Optional("maxResults", 10),
                cancellationToken)),
            new ToolDefinition("resolve_stack_trace", ReadOnly: true,
            (object)new
            {
                name = "resolve_stack_trace",
                description = @"Paste a .NET stack trace - frames come back mapped to file/line/symbol with source snippets, compiler name mangling undone: async state machines (Type+<Method>d__N.MoveNext), lambdas and closures (<Method>b__N_M in <>c / <>c__DisplayClass containers), async lambdas (<<Method>b__N_M>d.MoveNext), local functions (<Method>g__Local|N_M), demangled iterators (Method(args)+MoveNext()), static constructors (Type..cctor), nested types and explicit interface implementations (Type.Ns.IFace.Member). Reads .NET, Mono/Unity ([0x...] in file:N) and localized (bei/à/... file:Zeile N) frame lines.

USAGE: resolve_stack_trace(stackTrace: ""<ex.ToString() or log paste>"").
OUTPUT: ordered frames {raw, symbol, frameKind (method/async/iterator/lambda/localFunction/text), resolved, ambiguousOverload, location, snippet, notice}. An overload is chosen by the parameter types the frame names and by the reported line. Frames with file:line pointing into the solution resolve to that exact statement; frames without (or with a line outside the method, or too large to be a line) resolve to the declaration by name. BCL/third-party frames pass through with resolved: false. notice carries the type name's shadowing of framework types, when any. Lines are ZERO-based like every other tool (trace's 1-based numbers are converted).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        stackTrace = new { type = "string", description = "The stack trace text (ex.ToString(), a log excerpt, or a pasted fragment)" },
                        contextLines = new { type = "integer", description = "Source lines to include either side of each resolved location, 0-20 (default: 2)" }
                    },
                    required = new[] { "stackTrace" }
                }
            },
            (svc, p, cancellationToken) => svc.ResolveStackTraceAsync(
                p.Required<string>("stackTrace"),
                p.Optional("contextLines", 2),
                cancellationToken)),
            new ToolDefinition("remove_unused_code", ReadOnly: true,
            (object)new
            {
                name = "remove_unused_code",
                description = @"Compute the bulk cleanup for find_unused_code results: every reported declaration removed (all partial declarations; a multi-variable field only when every declarator is dead) plus the using directives the removals made unnecessary.

USAGE: remove_unused_code(symbolNames: [""DeadHelper"", ""GhostClass""]) for a targeted fast cleanup, or the find_unused_code filters (projectName, includePrivate, includeInternal, symbolKindFilter) for a sweep.
OUTPUT: removed[] and skipped[] per symbol, and files[] with each touched file's FULL updatedContent (preprocessor directives around a removed declaration are kept, so no #region or #if is left unbalanced). GENERATION-ONLY: never writes - YOU write each updatedContent (Write/Edit), then sync_documents and get_diagnostics. Re-run to confirm nothing remains.
NOTE: inherits find_unused_code's heuristics - verify with find_references before deleting anything public.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Restrict the sweep to one project, by name ('Multi') or one target framework of it ('Multi(net8.0)'); an unknown name is an error (default: all projects)" },
                        includePrivate = new { type = "boolean", description = "Consider private symbols (default: true)" },
                        includeInternal = new { type = "boolean", description = "Consider internal symbols (default: false)" },
                        symbolKindFilter = new { type = "string", description = "Filter: 'Class', 'Interface', 'Struct', 'Enum', 'Delegate', 'Type', 'Method', 'Property', 'Field', 'Member'" },
                        symbolNames = new { type = "array", items = new { type = "string" }, description = "Allow-list: only these simple names are checked and removed. Much faster than a sweep." },
                        maxSymbols = new { type = "integer", description = "Cap on symbols considered, 1-500 (default: 50)" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.RemoveUnusedCodeAsync(
                p.Optional<string>("projectName"),
                p.Optional("includePrivate", true),
                p.Optional("includeInternal", false),
                p.Optional<string>("symbolKindFilter"),
                p.OptionalStringArray("symbolNames"),
                p.Optional("maxSymbols", 50),
                cancellationToken)),
            new ToolDefinition("generate_test_stub", ReadOnly: true,
            (object)new
            {
                name = "generate_test_stub",
                description = @"Generate a compilable test skeleton for a target method - pairs with find_untested_code. The test project is the first project that references the type's project (directly or transitively), else the type's own project, whose compilation has a test framework (xUnit/NUnit/MSTest/TUnit test attribute type); none found defaults to xUnit with frameworkDetected: false. A target that test project cannot call (private, protected, or internal without InternalsVisibleTo) is refused. The SUT is constructed via the public constructor with the fewest parameters; a generic SUT or method is closed over type arguments satisfying its constraints; interface-typed arguments become TODO-marked default! placeholders; every ref argument gets its own local; a Task-returning target produces an async Task test.

USAGE: generate_test_stub(typeName: ""OrderService"", methodName: ""Submit"").
OUTPUT: testCode, testClassName, suggestedFilePath (the existing {Type}Tests.cs when present, else the test project root), framework, and data.notice when the type name also matched framework types. GENERATION-ONLY: never writes - YOU write the stub and fill in the TODOs.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type declaring the method (fully-qualified, simple, or unique partial name)" },
                        methodName = new { type = "string", description = "Method to generate a test for" },
                        overloadIndex = new { type = "integer", description = "Zero-based overload index when methodName is overloaded (see get_method_overloads)" }
                    },
                    required = new[] { "typeName", "methodName" }
                }
            },
            (svc, p, cancellationToken) => svc.GenerateTestStubAsync(
                p.Required<string>("typeName"),
                p.Required<string>("methodName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("check_architecture", ReadOnly: true,
            (object)new
            {
                name = "check_architecture",
                description = @"Enforce dependency rules over the real type-dependency graph: every type-to-type edge a type's own declaration makes is checked against your deny rules - named base types and interfaces, attributes (with their typeof arguments), member signatures, and everything its bodies and initializers use: created types, call and member-access receivers, static members (enum values included), extension-method hosts, generic arguments, typeof, casts and `as`, type tests and patterns, catch types, local variables (foreach, using, out var, pattern declarations) and lambda parameters. Compiler-generated members (implicit constructors, record members) and void are not edges; a nested type's body is the nested type's.

USAGE: check_architecture(rules: [{ ""from"": ""MyApp.Domain*"", ""deny"": ""MyApp.Infrastructure*"" }]). Globs (* and ?) match namespaces by default; level=""project"" matches project names instead (a multi-targeted project by its plain name).
OUTPUT: violations[] (by fromType, then toType) with fromType, toType, the matched rule, and a representative source location (null when the dependency is signature-only), plus rulesEvaluated and edgesChecked.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        rules = new
                        {
                            type = "array",
                            description = "Dependency rules to enforce",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    from = new { type = "string", description = "Glob matching the SOURCE namespace (or project with level='project')" },
                                    deny = new { type = "string", description = "Glob matching the FORBIDDEN target namespace (or project)" }
                                }
                            }
                        },
                        level = new { type = "string", description = "'namespace' (default) or 'project' - what the globs match against" }
                    },
                    required = new[] { "rules" }
                }
            },
            (svc, p, cancellationToken) => svc.CheckArchitectureAsync(
                ParseArchitectureRules(p, "rules"),
                p.Optional("level", "namespace"),
                cancellationToken)),
            new ToolDefinition("find_unused_dependencies", ReadOnly: true,
            (object)new
            {
                name = "find_unused_dependencies",
                description = @"Which declared PackageReference / ProjectReference entries the compiler never actually needs - Roslyn's own GetUsedAssemblyReferences verdict per project. Package references are read as MSBuild evaluates the project (imports such as the nearest Directory.Build.props, Conditions, $(Property) versions, Update=, VersionOverride, Central Package Management) and compile references are attributed to packages through the restore assets (project.assets.json), wherever packages were restored to.

USAGE: find_unused_dependencies() or find_unused_dependencies(projectName: ""MyApp"").
OUTPUT: per project (a multi-targeted project once, with targetFrameworks; a reference counts as used when any framework uses it) - unusedPackages, unusedProjectReferences, and skipped entries with reasons (packages contributing no compile-time assemblies - analyzers, source generators, build assets, or compile assets excluded by ExcludeAssets - cannot be judged this way).
CAVEAT: compile-time verdict only - a package loaded purely via reflection (plugins, MEF) appears unused. Verify with find_reflection_usage before removing.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Restrict the audit to one project (default: every project)" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindUnusedDependenciesAsync(
                p.Optional<string>("projectName"),
                cancellationToken)),
            new ToolDefinition("find_dead_branches", ReadOnly: true,
            (object)new
            {
                name = "find_dead_branches",
                description = @"Unreachable basic blocks per method body, from the real control-flow graph (not syntax heuristics): code after return/throw, constant-false branches, code after non-breaking infinite loops.

USAGE: find_dead_branches(filePath: ""src/MyService.cs"") or find_dead_branches(projectName: ""MyApp"") - exactly one scope.
OUTPUT: deadBranches[] with the owning member (""Type.Member"", a constructor by its type name), the first unreachable statement's location (a lone `return x;` or `throw` included), and a snippet; each block once, with targetFrameworks for a multi-targeted project; methodsAnalyzed for scan scope. Top-level bodies only - a local function's or lambda's interior flow is not walked.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Scan one file. Use INSTEAD of projectName." },
                        projectName = new { type = "string", description = "Scan every document of one project. Use INSTEAD of filePath." },
                        maxResults = new { type = "integer", description = "Cap on reported dead branches, 1-500 (default: 50); totalCount reflects all found" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindDeadBranchesAsync(
                p.Optional<string>("filePath"),
                p.Optional<string>("projectName"),
                p.Optional("maxResults", 50),
                cancellationToken)),
            new ToolDefinition("add_missing_imports", ReadOnly: true,
            (object)new
            {
                name = "add_missing_imports",
                description = @"Compute the using directives that fix a file's CS0246/CS0103/CS1061 diagnostics - compiler-accurate import suggestion over every namespace the compilation can see (source and referenced metadata). A name is looked up as the compiler binds it: an attribute name also as its ""Attribute""-suffixed attribute type, a generic name only among types of its arity, and a missing member (CS1061) only as an extension method that applies to the receiver's type.

USAGE: add_missing_imports(filePath: ""src/MyService.cs"").
OUTPUT: added[] (each name with its namespace and the using line; every name a using fixes, also when names share it) plus updatedContent with the usings inserted at their sorted position WITHOUT reordering existing ones; ambiguous[] names report their candidate namespaces and change nothing; unresolvable[] names have no candidate. GENERATION-ONLY: never writes - YOU write updatedContent, then sync_documents and get_diagnostics.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "File whose unresolved names should get usings computed" }
                    },
                    required = new[] { "filePath" }
                }
            },
            (svc, p, cancellationToken) => svc.AddMissingImportsAsync(
                p.Required<string>("filePath"),
                cancellationToken)),
            new ToolDefinition("find_naming_violations", ReadOnly: true,
            (object)new
            {
                name = "find_naming_violations",
                description = @"Naming-convention audit that reproduces the compiler's IDE1006 verdicts for the solution's own .editorconfig dotnet_naming_* rules; when none are configured it falls back to conventional defaults (PascalCase types/methods/local functions/properties, camelCase parameters/locals, _camelCase private instance fields - const/static fields and const locals exempt).

USAGE: find_naming_violations() or find_naming_violations(projectName: ""MyApp"").
OUTPUT: violations[] {symbol, name, kind, rule, expected, reason (the compiler's wording), location {filePath, line, column}, targetFrameworks}, in location order, each once; rulesSource says 'editorconfig' or 'defaults'; unsupportedRules lists configured rules the compiler ignores (no severity, no style, no capitalization) with the reason.
RULES: applicable_kinds, applicable_accessibilities and required_modifiers filters; pascal_case, camel_case, first_word_upper, all_upper, all_lower capitalization; required_prefix/suffix and word_separator; the compiler's precedence (more required modifiers, then fewer accessibilities, then fewer kinds, then rule name). The first rule that applies decides: a rule with severity none silences the symbols it applies to. Checked: namespaces, types, type parameters, members (not the names of overrides or interface implementations), every parameter (overrides', lambdas', class primary constructors' included; not a record's), every local and local function.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Restrict the audit to one project (default: every project)" },
                        maxResults = new { type = "integer", description = "Cap on reported violations, 1-1000 (default: 100); violationCount reflects all found" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindNamingViolationsAsync(
                p.Optional<string>("projectName"),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("get_extension_methods", ReadOnly: true,
            (object)new
            {
                name = "get_extension_methods",
                description = @"Which extension members apply to a type - both classic `this`-parameter extension methods and C# 14 `extension(T receiver)` block members - grouped by their containing static class.

USAGE: get_extension_methods(typeName: ""OrderService"").
OUTPUT: groups[] {containingClass, members[] {name, style: 'classic'|'extensionBlock', kind, signature, receiverType, location {filePath, line, column}}}, classes in name order and members in source order, each member once (a block member never again as the classic method it compiles to). Source-declared extensions only. A member applies when the compiler would let the type call it: receiver conversion, generic inference and constraints (extension<T>(T value) where T : IShape applies to every IShape). For a generic type definition (List`1), an extension on one of its constructions (this List<int>) applies too. data.notice names framework types the type name also matches.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Target type (fully-qualified or simple name)" },
                        maxResults = new { type = "integer", description = "Cap on extension members reported, 1-500 (default: 50); totalCount reflects all found" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetExtensionMethodsAsync(
                p.Required<string>("typeName"),
                p.Optional("maxResults", 50),
                cancellationToken)),
            new ToolDefinition("find_interceptors", ReadOnly: true,
            (object)new
            {
                name = "find_interceptors",
                description = @"Surface [InterceptsLocation] call rerouting - the hidden control flow behind ASP.NET minimal APIs, EF compiled models, and hand-written interceptors. Scans every tree of the generator-augmented compilation (generated code included, where most interceptors live), matching the attribute by simple name through any qualification (global::InterceptsLocation included) and reading its arguments by name or position.

USAGE: find_interceptors() or find_interceptors(projectName: ""MyApi"").
OUTPUT: interceptors[] {interceptorMethod, interceptorLocation, target, inGeneratedCode}, in location order, a multi-targeted project's once. Legacy (filePath, line, character) targets convert to ZERO-based line/column and include the intercepted line's text when the file is a solution document; (version, data) targets surface the raw data string undecoded.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Restrict the scan to one project (default: every project)" },
                        maxResults = new { type = "integer", description = "Cap on reported interceptors, 1-1000 (default: 100); totalCount reflects all found" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindInterceptorsAsync(
                p.Optional<string>("projectName"),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("get_documentation", ReadOnly: true,
            (object)new
            {
                name = "get_documentation",
                description = @"Full XML documentation for a symbol with <inheritdoc> resolved recursively - other tools return the raw tag. A member-level <inheritdoc> (bare or cref) is expanded element-by-element: inherited content fills the sections, params, and exceptions the local doc omits; inherited params and typeParams document the parameters at the same positions and are reported under this symbol's own names.

USAGE: get_documentation(typeName: ""OrderService"") for a type, or with memberName (+ overloadIndex) for a member.
OUTPUT: documentation {summary, returns, remarks, params[], typeParams[], exceptions[]} — text keeps <para> blocks apart (a blank line) and <code> line breaks — inheritedFrom (the symbol docs were inherited from, or null), and unresolved ('cycle' | 'no-inherited-member' | 'unresolved-cref' when an inheritdoc could not be fully resolved). A bare inheritdoc follows the overridden member, the explicit interface member, or the implicitly implemented one; on a constructor, the base class constructor with the same parameter types; on a type, its base type or first interface. data.notice names framework types the type name also matches.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type to document (fully-qualified or simple name)" },
                        memberName = new { type = "string", description = "Member to document (the type's simple name or '.ctor' for a constructor); omit to document the type itself" },
                        overloadIndex = new { type = "integer", description = "Zero-based overload index as get_method_overloads numbers the declared overloads; required when memberName is overloaded" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.GetDocumentationAsync(
                p.Required<string>("typeName"),
                p.Optional<string>("memberName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("fix_all", ReadOnly: true,
            (object)new
            {
                name = "fix_all",
                description = @"Compute the fix for EVERY instance of a diagnostic id across a file, project, or the whole solution - ""remove all unused usings"", ""drop every unused variable"". Uses the provider's own batch FixAll where available, falling back to an iterative fix-recompute-repeat loop.

USAGE: fix_all(diagnosticId: ""CS0219"", scope: ""solution""). scope 'file' requires filePath; 'project' requires projectName ('Multi' covers every target framework, 'Multi(net8.0)' one). fixIndex picks among the fixes offered for the first instance, as get_code_fixes lists them (default 0); the fix runs through the provider that offered it. A fix that is a menu of variants is refused with the variants (AMBIGUOUS_MATCH).
OUTPUT: files[] with each touched file's full updatedContent (a file compiled for several target frameworks once), diagnosticsMatched (each source location once), fixTitle, and fallbackMode. GENERATION-ONLY: never writes - YOU write each updatedContent, then sync_documents and get_diagnostics.
NOTE: operates on compiler diagnostics (e.g. CS0219, CS8019); analyzer-only ids are out of scope.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        diagnosticId = new { type = "string", description = "The diagnostic id to fix everywhere, e.g. 'CS0219'" },
                        scope = new { type = "string", description = "'solution' (default), 'project', or 'file'" },
                        filePath = new { type = "string", description = "Required when scope='file': the file to fix" },
                        projectName = new { type = "string", description = "Required when scope='project': the project to fix" },
                        fixIndex = new { type = "integer", description = "Which available fix to apply (default 0; see get_code_fixes)" }
                    },
                    required = new[] { "diagnosticId" }
                }
            },
            (svc, p, cancellationToken) => svc.FixAllAsync(
                p.Required<string>("diagnosticId"),
                p.Optional("scope", "solution"),
                p.Optional<string>("filePath"),
                p.Optional<string>("projectName"),
                p.Optional("fixIndex", 0),
                cancellationToken)),
            new ToolDefinition("find_tests", ReadOnly: true,
            (object)new
            {
                name = "find_tests",
                description = @"Which tests cover a symbol - the inverse of find_untested_code, with the same entry points and walk. Each entry point's transitive reach (a test together with its class's lifecycle code, or a step binding or hook on its own) is walked over in-source callees; it covers the target when its reach includes it.

USAGE: find_tests(typeName: ""OrderService"", memberName: ""Submit""). Omit memberName to ask which tests touch ANY member of the type. overloadIndex disambiguates overloads.
OUTPUT: coveringTests[] {testMethod, containingType, testProject, kind: test|stepBinding|hook, location}, dispatchOnlyTests[] (same shape: entry points that reach the target only through an interface or base member it implements or overrides, e.g. a call on an interface-typed variable - not counted as covering, since another implementation or a mock may run), totalTests (the number of test methods), and notice (the type name's shadowing of framework types, when any).
NOTE: audit-time tool - one solution sweep per call, no caching.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type containing the target (fully-qualified, simple, or unique partial name)" },
                        memberName = new { type = "string", description = "Member to find covering tests for; omit to target the type itself" },
                        overloadIndex = new { type = "integer", description = "Zero-based overload index when memberName is overloaded (see get_method_overloads)" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.FindTestsAsync(
                p.Required<string>("typeName"),
                p.Optional<string>("memberName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("get_super_method", ReadOnly: true,
            (object)new
            {
                name = "get_super_method",
                description = @"Navigate from an override or implementation to what it overrides / implements. Reports the full base-member chain plus every interface member the symbol satisfies (explicit and implicit). Works for methods, properties, and events.

USAGE: get_super_method(typeName: ""MyImpl"", memberName: ""Handle"") or positional filePath+line+column.
OUTPUT: overrides[] (the OverriddenX chain, root-ward) and implements[] (interface members), each {signature, containingType, location {filePath, line, column} or null, inMetadata}. data.notice names framework types typeName also matches.
ADDRESSING: use EITHER typeName+memberName OR filePath+line+column (zero-based) - exactly one mode.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Positional addressing: absolute path to source file" },
                        line = new { type = "integer", description = "Positional addressing: zero-based line number on the member" },
                        column = new { type = "integer", description = "Positional addressing: zero-based column number" },
                        typeName = new { type = "string", description = "Name-based addressing: type containing the member. Use INSTEAD of filePath/line/column." },
                        memberName = new { type = "string", description = "Name-based addressing: member to navigate from (required with typeName)" },
                        overloadIndex = new { type = "integer", description = "Name-based addressing: zero-based overload index when memberName is overloaded" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.GetSuperMethodAsync(
                p.Optional<string>("filePath"),
                p.OptionalStruct<int>("line"),
                p.OptionalStruct<int>("column"),
                p.Optional<string>("typeName"),
                p.Optional<string>("memberName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("find_type_instantiations", ReadOnly: true,
            (object)new
            {
                name = "find_type_instantiations",
                description = @"Where a type is constructed - every `new T(...)` and target-typed `new(...)` whose type is T. Complements find_references when you specifically want construction sites.

USAGE: find_type_instantiations(typeName: ""OrderService"").
OUTPUT: matches[] {location, lineText, kind} over source code (no generated trees, no metadata), in location order with each site once (a multi-targeted project's included), and notice (the type name's shadowing of framework types, when any).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type to find constructions of (fully-qualified, simple, or unique partial name)" },
                        maxResults = new { type = "integer", description = "Cap on results, 1-1000 (default: 100); totalCount reflects all found" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.FindTypeInstantiationsAsync(
                p.Required<string>("typeName"),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("find_pattern_usages", ReadOnly: true,
            (object)new
            {
                name = "find_pattern_usages",
                description = @"Where a type appears in a type test or pattern - `x is T`, `x as T`, and every declaration, type and recursive pattern whose matched type is T: in is/switch, `case T:` labels, under not/and/or/parentheses, as list-pattern elements, nested in property subpatterns, and type-less property patterns (`{ Total: > 5 }`) on a T input.

USAGE: find_pattern_usages(typeName: ""HttpException"").
OUTPUT: matches[] {location, lineText, kind} in location order, where kind is isExpression/asExpression/declarationPattern/typePattern/recursivePattern, and notice (the type name's shadowing of framework types, when any).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type to find pattern matches against (fully-qualified, simple, or unique partial name)" },
                        maxResults = new { type = "integer", description = "Cap on results, 1-1000 (default: 100); totalCount reflects all found" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.FindPatternUsagesAsync(
                p.Required<string>("typeName"),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("find_throw_sites", ReadOnly: true,
            (object)new
            {
                name = "find_throw_sites",
                description = @"Where an exception type is thrown - `throw new T(...)` and throw expressions. With includeDerived (default true), also throws of types deriving from T.

USAGE: find_throw_sites(typeName: ""InvalidOperationException"").
OUTPUT: matches[] {location, lineText, kind} over source code in location order, and notice (the type name's shadowing of framework types, when any - e.g. a source TimeoutException wins over System.TimeoutException).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Exception type to find throw sites of (fully-qualified, simple, or unique partial name)" },
                        includeDerived = new { type = "boolean", description = "Also match throws of derived types (default: true)" },
                        maxResults = new { type = "integer", description = "Cap on results, 1-1000 (default: 100); totalCount reflects all found" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.FindThrowSitesAsync(
                p.Required<string>("typeName"),
                p.Optional("includeDerived", true),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("find_catch_blocks", ReadOnly: true,
            (object)new
            {
                name = "find_catch_blocks",
                description = @"Where an exception type is caught - `catch (T ...)` clauses. With includeDerived (default true), also catch clauses whose declared type is a base of T (which would catch a T), and catch clauses with no exception type (`catch { }`, kind catchAll), which catch every T.

USAGE: find_catch_blocks(typeName: ""IOException"").
OUTPUT: matches[] {location, lineText, kind: catch|catchAll} over source code in location order, and notice (the type name's shadowing of framework types, when any).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Exception type to find catch blocks for (fully-qualified, simple, or unique partial name)" },
                        includeDerived = new { type = "boolean", description = "Also match catches whose declared type is a base of the target (default: true)" },
                        maxResults = new { type = "integer", description = "Cap on results, 1-1000 (default: 100); totalCount reflects all found" }
                    },
                    required = new[] { "typeName" }
                }
            },
            (svc, p, cancellationToken) => svc.FindCatchBlocksAsync(
                p.Required<string>("typeName"),
                p.Optional("includeDerived", true),
                p.Optional("maxResults", 100),
                cancellationToken)),
            new ToolDefinition("find_async_issues", ReadOnly: true,
            (object)new
            {
                name = "find_async_issues",
                description = @"Async-hygiene audit of the three highest-signal problems: async void that isn't an event handler (methods, local functions, and async lambdas converted to a void-returning delegate - their exceptions crash the process), blocking on async work (.Result / .Wait() / .GetAwaiter().GetResult() on a Task), and a CancellationToken in scope that isn't forwarded to an awaited call that could take one (a cancellable overload, generic ones included, or an optional token parameter left to its default; awaits through ConfigureAwait included). Every executable body is scanned: methods, constructors, operators, accessors, expression-bodied members, initializers, local functions and lambdas.

USAGE: find_async_issues() or find_async_issues(projectName: ""MyApp""; 'Multi' covers every framework of a multi-targeted project, 'Multi(net8.0)' one).
OUTPUT: issues[] {category: asyncVoid|blockingCall|tokenNotForwarded, symbol (the containing member), detail, location {filePath, line, column}, targetFrameworks} in location order, each once, and countByCategory. The total also appears in get_project_health.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Restrict the audit to one project (default: every project)" },
                        maxResults = new { type = "integer", description = "Cap on reported issues, 1-1000 (default: 100); totalCount reflects all found" }
                    }
                }
            },
            (svc, p, cancellationToken) => svc.FindAsyncIssuesAsync(
                p.Optional<string>("projectName"),
                p.Optional("maxResults", 100),
                cancellationToken))
        };
    }

    internal static Task<object> DispatchAsync(
        string name, JsonRpcParameters p, RoslynService svc,
        CancellationToken cancellationToken = default)
    {
        // 1.5.x tool names carried a "roslyn:" prefix, which violates the MCP
        // tool-name pattern ^[a-zA-Z0-9_-]{1,64}$. The bare names are canonical; the
        // prefixed spellings stay accepted as aliases for backward compatibility.
        // TODO(2.0): drop this strip once 1.5.x clients have migrated.
        if (name.StartsWith("roslyn:", StringComparison.Ordinal))
        {
            name = name["roslyn:".Length..];
        }

        if (!DefinitionsByName.TryGetValue(name, out var definition))
        {
            throw new JsonRpcInvalidParamsException($"Unknown tool: {name}");
        }

        return definition.Handler(svc, p, cancellationToken);
    }

    // The array-of-object argument parsers. Each reads its parameter through
    // NestedArguments, so every wrong shape — an absent required array, a non-array value,
    // a non-object element, an absent required element field, a field of the wrong JSON
    // type — is a -32602 naming the tool and the parameter path, never an empty list.
    // Element fields mirror the published item schemas: a field the schema lists under
    // the item's "required" is required here, every other field is optional.

    // find_path_between's endpoint arrays: [{typeName, memberName}, ...], both fields
    // required. An absent optional array (avoidThrough, mustPassThrough) reads as empty.
    internal static List<MemberRef> ParseMemberRefs(JsonRpcParameters p, string name, bool required)
    {
        var elements = required
            ? NestedArguments.RequiredObjectArray(p, name)
            : NestedArguments.OptionalObjectArray(p, name) ?? Array.Empty<JsonObject>();
        return elements
            .Select((element, i) => new MemberRef(
                NestedArguments.RequiredString(element, "typeName", $"{name}[{i}]", p.ToolName),
                NestedArguments.RequiredString(element, "memberName", $"{name}[{i}]", p.ToolName)))
            .ToList();
    }

    // change_signature's required changes array. Every element field is optional in the
    // item schema; an absent action reads as "", which names no action.
    internal static List<SignatureChange> ParseSignatureChanges(JsonRpcParameters p, string name)
    {
        return NestedArguments.RequiredObjectArray(p, name)
            .Select((element, i) =>
            {
                var path = $"{name}[{i}]";
                return new SignatureChange
                {
                    Action = NestedArguments.RequiredString(element, "action", path, p.ToolName),
                    Name = NestedArguments.OptionalString(element, "name", path, p.ToolName),
                    Type = NestedArguments.OptionalString(element, "type", path, p.ToolName),
                    NewName = NestedArguments.OptionalString(element, "newName", path, p.ToolName),
                    DefaultValue = NestedArguments.OptionalString(element, "defaultValue", path, p.ToolName),
                    Position = NestedArguments.OptionalInt(element, "position", path, p.ToolName),
                    Order = NestedArguments.OptionalStringArray(element, "order", path, p.ToolName)
                };
            })
            .ToList();
    }

    // check_architecture's required rules array. from/deny are optional in the item
    // schema and read as "" when absent; the tool rejects a rule with an empty glob.
    internal static List<ArchitectureRule> ParseArchitectureRules(JsonRpcParameters p, string name)
    {
        return NestedArguments.RequiredObjectArray(p, name)
            .Select((element, i) => new ArchitectureRule
            {
                From = NestedArguments.OptionalString(element, "from", $"{name}[{i}]", p.ToolName) ?? "",
                Deny = NestedArguments.OptionalString(element, "deny", $"{name}[{i}]", p.ToolName) ?? ""
            })
            .ToList();
    }

    // get_method_source_batch's required methods array: [{typeName, methodName,
    // overloadIndex?}, ...] with typeName and methodName required. Each request is the
    // dictionary GetMethodSourceBatchAsync reads; overloadIndex is present only when given.
    internal static List<Dictionary<string, object>> ParseMethodBatchRequests(JsonRpcParameters p, string name)
    {
        return NestedArguments.RequiredObjectArray(p, name)
            .Select((element, i) =>
            {
                var path = $"{name}[{i}]";
                var request = new Dictionary<string, object>
                {
                    ["typeName"] = NestedArguments.RequiredString(element, "typeName", path, p.ToolName),
                    ["methodName"] = NestedArguments.RequiredString(element, "methodName", path, p.ToolName)
                };
                if (NestedArguments.OptionalInt(element, "overloadIndex", path, p.ToolName) is { } overloadIndex)
                {
                    request["overloadIndex"] = overloadIndex;
                }
                return request;
            })
            .ToList();
    }
}
