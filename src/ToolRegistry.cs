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
                description = "Check the health and status of the SharpLens MCP server and workspace. Returns server status, solution loaded state, project/document counts, cache size, and configuration. O(1) by default; set includeDiagnostics=true to also compile a sample of projects and report error/warning counts (slower). Call this first to verify the server is ready.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        includeDiagnostics = new { type = "boolean", description = "Also compile up to five projects and report error/warning counts (default: false). Off by default because the compilation can take tens of seconds on a large solution." }
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
                description = "Load a .NET solution for analysis. MUST be called before using any other analysis tools. Returns: projectCount, documentCount, and load time. Use health_check to verify current state.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        solutionPath = new { type = "string", description = "Absolute path to .sln or .slnx file" }
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
                description = "Get detailed semantic information about a symbol at a specific position. IMPORTANT: Uses ZERO-BASED coordinates. If your editor shows 'Line 14, Column 5', pass line=13, column=4. Returns symbol kind, type, namespace, documentation, and location.",
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
                description = "Fast navigation to symbol definition. Returns the definition location without finding all references. IMPORTANT: Uses ZERO-BASED coordinates (editor line 10 = pass line 9).",
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
                description = "Find all references to a symbol across the entire solution. Returns file paths, line numbers, code context, and a `kind` for each reference: write (assignment LHS, ++/--, ref/out arg), invocation (call target), cast (cast target type), typeof, nameof, attribute, or read. IMPORTANT: Uses ZERO-BASED coordinates (editor line 10 = pass line 9).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file containing the symbol" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" },
                        maxResults = new { type = "integer", description = "Maximum number of references to return (default: 100). Results are truncated with a hint if limit is exceeded." },
                        kind = new { type = "string", description = "Optional kind filter (case-insensitive). One of: write, invocation, cast, typeof, nameof, attribute, read. When set, the response includes totalReferences (unfiltered) and totalReferencesAfterFilter so callers see both numbers." }
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
                description = "Find all implementations of an interface or abstract class. Returns implementing types with their locations. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        maxResults = new { type = "integer", description = "Maximum number of implementations to return (default: 50). Results are truncated with a hint if limit is exceeded." }
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
                description = "Get the inheritance hierarchy (base types and derived types) for a type. Returns baseTypes chain and derivedTypes list. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        maxDerivedTypes = new { type = "integer", description = "Maximum number of derived types to return (default: 50). Results are truncated with a hint if limit is exceeded." }
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
                description = "Search for types, methods, properties, etc. by name across the solution. Default glob mode supports wildcards (e.g., '*Handler' finds classes ending with 'Handler'); matchMode='fuzzy' uses the IDE pattern matcher with camel-hump matching ('RSvc' finds RoslynService). CASING: glob/substring matching is case-INSENSITIVE; fuzzy matching treats uppercase query letters as camel-hump anchors (effectively case-sensitive on them). PAGINATION: Returns totalCount and hasMore. Use offset to paginate through results.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Search query. Glob mode: wildcards * (any characters) and ? (single character), e.g. 'Handler', '*Handler', 'Get*', 'I?Service'. Fuzzy mode: camel humps and partial names, e.g. 'RSvc'. Case-insensitive." },
                        matchMode = new { type = "string", description = "'glob' (default) or 'fuzzy' (IDE-grade camel-hump/partial matching via Roslyn's pattern matcher)" },
                        kind = new { type = "string", description = "Optional: filter by symbol kind. For types use: Class, Interface, Struct, Enum, Delegate. For members use: Method, Property, Field, Event. Other: Namespace. Case-insensitive." },
                        maxResults = new { type = "integer", description = "Maximum number of results per page (default: 50)" },
                        namespaceFilter = new { type = "string", description = "Optional: filter by namespace (supports wildcards). Examples: 'MyApp.Core.*', '*.Services', 'MyApp.*.Handlers'. Case-insensitive." },
                        offset = new { type = "integer", description = "Offset for pagination (default: 0). Use pagination.nextOffset from previous response to get next page." }
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
                description = @"Advanced semantic code query with multiple filters. Find symbols based on their semantic properties.

EXAMPLES:
- Async methods without CancellationToken: isAsync=true, parameterExcludes=[""CancellationToken""]
- Public static methods: accessibility=""Public"", isStatic=true
- Classes with [Obsolete]: kinds=[""Class""], attributes=[""ObsoleteAttribute""]

FILTERS: All specified filters are combined with AND logic. Omit a filter to skip it. Returns symbol details with locations.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        kinds = new { type = "array", items = new { type = "string" }, description = "Optional: filter by symbol kinds (can specify multiple). For types: Class, Interface, Struct, Enum, Delegate. For members: Method, Property, Field, Event. Example: ['Class', 'Interface']" },
                        isAsync = new { type = "boolean", description = "Optional: filter methods by async/await (true for async methods, false for sync methods)" },
                        namespaceFilter = new { type = "string", description = "Optional: filter by namespace (supports wildcards). Examples: 'MyApp.Core.*', '*.Services'" },
                        accessibility = new { type = "string", description = "Optional: filter by accessibility (matches the reported DeclaredAccessibility name). Values: Public, Private, Internal, Protected, ProtectedOrInternal, ProtectedAndInternal" },
                        isStatic = new { type = "boolean", description = "Optional: filter by static modifier (true for static, false for instance)" },
                        type = new { type = "string", description = "Optional: filter fields/properties by their type. Partial match. Example: 'ILogger' finds all ILogger fields/properties" },
                        returnType = new { type = "string", description = "Optional: filter methods by return type. Partial match. Example: 'Task' finds all methods returning Task" },
                        attributes = new { type = "array", items = new { type = "string" }, description = "Optional: filter by attributes (must have ALL specified). Example: ['ObsoleteAttribute', 'EditorBrowsableAttribute']" },
                        parameterIncludes = new { type = "array", items = new { type = "string" }, description = "Optional: filter methods that MUST have these parameter types (partial match). Example: ['CancellationToken']" },
                        parameterExcludes = new { type = "array", items = new { type = "string" }, description = "Optional: filter methods that must NOT have these parameter types (partial match). Example: ['CancellationToken']" },
                        maxResults = new { type = "integer", description = "Maximum number of results (default: 100)" }
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
                description = "Get compiler errors, warnings, info messages, AND configured analyzer findings (StyleCop, Roslynator, NetAnalyzers, custom analyzers; editorconfig-resolved severities) for a file or project. Use before committing - this matches what CI will fail on. Response includes analyzersRan and analyzerCount so callers know whether analyzers contributed.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Optional: path to specific file, omit for all files" },
                        projectPath = new { type = "string", description = "Optional: path to specific project" },
                        severity = new { type = "string", description = "Optional: filter by severity (Error, Warning, Info)" },
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
                description = "Get the available code fixes for a specific diagnostic, from the same fix providers apply_code_fix uses - each entry's index can be passed straight to apply_code_fix as fixIndex. When no provider can fix the diagnostic, fallbackSuggestions carries manual guidance instead. WORKFLOW: (1) get_diagnostics to find issues, (2) get_code_fixes to see options, (3) apply_code_fix with fixIndex to fix.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        diagnosticId = new { type = "string", description = "Diagnostic ID (e.g., CS0246)" },
                        line = new { type = "integer", description = "Zero-based line number" },
                        column = new { type = "integer", description = "Zero-based column number" }
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
                description = "Apply automated code fix for a diagnostic. WORKFLOW: (1) Call with no fixIndex to list available fixes, (2) Call with fixIndex and preview=true to preview changes, (3) Call with preview=false to apply. IMPORTANT: Uses ZERO-BASED coordinates.",
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
                description = "Get solution/project structure. IMPORTANT: For large solutions (100+ projects), use summaryOnly=true or projectNamePattern to avoid token limit errors. Maximum output is limited to 25,000 tokens.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        includeReferences = new { type = "boolean", description = "Include package references (default: true, limited to 100 per project)" },
                        includeDocuments = new { type = "boolean", description = "Include document lists (default: false, limited to 500 per project)" },
                        projectNamePattern = new { type = "string", description = "Filter projects by name pattern (supports * and ? wildcards, e.g., '*.Application' or 'MyApp.*')" },
                        maxProjects = new { type = "integer", description = "Maximum number of projects to return (e.g., 10 for large solutions)" },
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
                description = "Remove unused using directives (the compiler's CS8019 verdict) and sort the rest: global usings first, then plain (System namespaces leading), then static, then aliases - alphabetical within each group. PREVIEW mode by default returns the organized text; set preview=false to apply to the file.",
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
                description = "Organize using directives for multiple files in a project. Supports file pattern filtering (e.g., '*.cs', 'Services/*.cs'). PREVIEW mode by default - set preview=false to apply changes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Optional: Project name to process. If omitted, processes all projects in solution." },
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
                description = "Format multiple documents in a project using Roslyn's whitespace formatter. Fixes indentation, spacing, and line breaks per the workspace's formatting rules without rewriting stylistic choices (null-suppression spacing and intentional blank lines are preserved). PREVIEW mode by default - set preview=false to apply changes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Optional: Project name to format. If omitted, formats all projects in solution." },
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
                description = "Get all overloads of a method. Returns list of signatures with parameter details. Use when you need to choose between overloads. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
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
                description = "Get information about the containing method/property/class at a position. Returns the enclosing symbol's name, kind, and signature. Useful for understanding context. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
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
                description = "Find all methods/properties that call or reference a specific symbol (inverse of find_references). Essential for impact analysis: 'If I change this method, what code will be affected?' IMPORTANT: Uses ZERO-BASED coordinates.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" },
                        maxResults = new { type = "integer", description = "Maximum number of call sites to return (default: 100). Results are truncated with a hint if limit is exceeded." }
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
                description = @"Find unused types, methods, properties, and fields in a project or entire solution. Returns symbols with zero references (excluding their declaration).

USAGE: find_unused_code() for entire solution, or find_unused_code(projectName=""MyProject"") for specific project.
OUTPUT: List of unused symbols with location, kind, and accessibility. Default limit: 50 results.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Optional: analyze specific project by name, omit to analyze entire solution" },
                        includePrivate = new { type = "boolean", description = "Include private members (default: true)" },
                        includeInternal = new { type = "boolean", description = "Include internal members (default: false - usually want to keep internal APIs)" },
                        symbolKindFilter = new { type = "string", description = "Optional: filter by symbol kind ('Class', 'Interface', 'Struct', 'Enum', 'Delegate', 'Type', 'Method', 'Property', 'Field', 'Member')" },
                        maxResults = new { type = "integer", description = "Maximum results to return (default: 50, helps manage large outputs)" }
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
                description = "Safely rename a symbol (type, method, property, etc.) across the entire solution. Uses Roslyn's semantic analysis to ensure all references are updated. SUPPORTS PREVIEW MODE - always preview first! ADDRESSING: use EITHER filePath+line+column (zero-based coordinates) OR typeName(+memberName) - exactly one mode. Default shows first 20 files with summary verbosity.",
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
                        maxFiles = new { type = "integer", description = "Max files to show in preview (default: 20, prevents large outputs)" },
                        verbosity = new { type = "string", description = "Output detail level: 'summary' (default, file paths + counts only ~200 tokens/file), 'compact' (add locations ~500 tokens/file), 'full' (include old/new text ~3000+ tokens/file)" }
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
                description = @"Generate an interface from a class or struct. Extracts all public instance members (methods, properties, events).

USAGE: Address the class by typeName=""MyService"" (or position on its declaration), provide interfaceName=""IMyService"".
OUTPUT: Generated interface code ready to insert. Useful for dependency injection and testability.
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

OUTPUT: format=""json"" returns structured data with nodes/edges. format=""mermaid"" returns diagram syntax.
USE CASE: Understand solution architecture, find circular dependencies, plan refactoring.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        format = new { type = "string", description = "Output format: 'json' (default) returns structured data, 'mermaid' returns Mermaid diagram syntax" }
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
                description = @"Get all members (methods, properties, fields, events) of a type BY NAME.

USAGE PATTERNS:
- Basic: get_type_members(""MyClass"") - list all members
- With inheritance: get_type_members(""MyService"", includeInherited=true)
- Filter by kind: get_type_members(""MyClass"", memberKind=""Method"")
- Verbosity control: verbosity=""summary"" (names only), ""compact"" (default, + signatures), ""full"" (+ docs, attrs)

WORKS WITH: Fully-qualified (""MyNamespace.MyClass""), simple (""MyClass""), or partial names.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type name (e.g., 'MyClass', 'MyNamespace.MyService')" },
                        includeInherited = new { type = "boolean", description = "Include members from base classes (default: false)" },
                        memberKind = new { type = "string", description = "Filter: 'Method', 'Property', 'Field', 'Event'" },
                        verbosity = new { type = "string", description = "'summary' (names only), 'compact' (default), 'full' (+ docs, attrs)" },
                        maxResults = new { type = "integer", description = "Maximum members to return (default: 100)" }
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

USAGE: get_method_signature(""MyClass"", ""ProcessData"") or with overload selection: get_method_signature(""MyClass"", ""ProcessData"", overloadIndex=1)",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Containing type name" },
                        methodName = new { type = "string", description = "Method name" },
                        overloadIndex = new { type = "integer", description = "Which overload (0-based, default: 0)" }
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
                description = @"Find all symbols with specific attributes.

USAGE:
- Find obsolete: get_attributes(""Obsolete"")
- Find serializable: get_attributes(""Serializable"")
- Scope to project: get_attributes(""Obsolete"", scope=""project:MyProject"")
- Scope to file: get_attributes(""Obsolete"", scope=""file:MyClass.cs"")",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        attributeName = new { type = "string", description = "Attribute name (e.g., 'Obsolete', 'Serializable', 'JsonProperty')" },
                        scope = new { type = "string", description = "'solution' (default), 'project:Name', or 'file:path'" },
                        maxResults = new { type = "integer", description = "Maximum results (default: 100)" }
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
                description = @"Find all types inheriting from a base type BY NAME.

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
                        maxResults = new { type = "integer", description = "Maximum results (default: 100)" }
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
                description = @"Get full inheritance chain BY NAME.

USAGE: get_base_types(""MyService"") returns: MyService → BaseService → ... → Object",
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
                description = @"Get comprehensive type overview in ONE CALL: type info + base types (first 3) + member counts.

USAGE: get_type_overview(""MyService"") - returns everything you need to understand a type quickly.",
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

USAGE: analyze_method(""MyService"", ""ProcessData"") or analyze_method(""MyClass"", ""Calculate"", includeCallers=true, includeOutgoingCalls=true)",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Containing type name" },
                        methodName = new { type = "string", description = "Method name" },
                        includeCallers = new { type = "boolean", description = "Include caller analysis (default: true)" },
                        includeOutgoingCalls = new { type = "boolean", description = "Include methods/properties this method calls (default: false)" },
                        maxCallers = new { type = "integer", description = "Max callers to return (default: 20)" },
                        maxOutgoingCalls = new { type = "integer", description = "Max outgoing calls to return (default: 50)" },
                        overloadIndex = new { type = "integer", description = "0-based overload to analyze when the method name is overloaded; omit to get an AMBIGUOUS_MATCH listing the overloads" }
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
OUTPUT: typeDeclarations[] each with members[] {name, kind, signature, accessibility, startLine, endLine} declared IN THIS FILE (a partial lists only its own part); nested types appear both as a member and as their own entry. members is capped at maxMembersPerType (default 100) with membersTruncated flagged.",
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
                description = @"Get all interface and abstract members that must be implemented for a type.

USAGE: Position on a class that implements interfaces or extends abstract classes.
OUTPUT: List of missing members with exact signatures ready to copy. Use before implementing interfaces.
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
                description = "Get all methods and properties that a method calls. Helps understand method dependencies and behavior. Returns list of called symbols with locations. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number inside the method" },
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
OUTPUT: compiles (bool), errors list with line numbers. Essential before inserting AI-generated code.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        code = new { type = "string", description = "C# code to validate" },
                        contextFilePath = new { type = "string", description = "Optional: file to use for context (usings, namespace)" },
                        standalone = new { type = "boolean", description = "If true, treat code as complete file (default: false)" }
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
OUTPUT: compatible (bool), requiresCast (bool), conversionKind, and explanation of why/why not.",
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
                description = @"Get all ways to create an instance of a type: constructors, factory methods, and builder patterns.

USAGE: get_instantiation_options(typeName=""HttpClient"")
OUTPUT: List of constructors with signatures, static factory methods, and hints (e.g., ""implements IDisposable"").",
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

USAGE: analyze_change_impact(filePath, line, column, changeType=""rename|changeType|addParameter|removeParameter"")
OUTPUT: List of impacted locations, whether change is safe, and specific issues at each location.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number of the symbol" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        changeType = new { type = "string", description = "Type of change: rename, changeType, addParameter, removeParameter, changeAccessibility, delete" },
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
OUTPUT: Full method source including signature, body, location (file + line numbers), and line count.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "The containing type name (e.g., 'MyService', 'MyController')" },
                        methodName = new { type = "string", description = "The method name (e.g., 'ProcessData')" },
                        overloadIndex = new { type = "integer", description = "Which overload to get (0-based, default: 0)" }
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
OUTPUT: Results array with source for each method, plus errors array for any that failed.
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
                        maxMethods = new { type = "integer", description = "Maximum methods to process (default: 20)" }
                    },
                    required = new[] { "methods" }
                }
            },
            (svc, p, cancellationToken) => svc.GetMethodSourceBatchAsync(
                ParseMethodBatchRequests(p.Raw("methods"), p.ToolName),
                p.Optional("maxMethods", 20),
                cancellationToken)),
            new ToolDefinition("generate_constructor", ReadOnly: true,
            (object)new
            {
                name = "generate_constructor",
                description = @"Generate a constructor from fields and/or properties of a type.

USAGE: Address the type by typeName=""MyService"" (or position on its declaration). Use includeProperties=true for auto-properties.
OUTPUT: constructorCode string ready to paste, parameter list, and field assignments.
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
                        includeProperties = new { type = "boolean", description = "Include properties with setters (default: false)" },
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
                description = @"Change a method signature and preview impact on all call sites.

ACTIONS: add (new param), remove, rename, reorder parameters.
WORKFLOW: (1) Call with preview=true (default) to see affected call sites, (2) Review changes, (3) Call with preview=false to apply.
OUTPUT: oldSignature, newSignature, list of call sites needing updates.
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
                                    action = new { type = "string", description = "Action: 'add', 'remove', 'rename', or 'reorder'" },
                                    name = new { type = "string", description = "Parameter name (for add/remove/rename)" },
                                    type = new { type = "string", description = "Parameter type (for add)" },
                                    newName = new { type = "string", description = "New name (for rename)" },
                                    defaultValue = new { type = "string", description = "Default value (for add)" },
                                    position = new { type = "integer", description = "Position to insert (for add), -1 means end" },
                                    order = new { type = "array", items = new { type = "string" }, description = "New parameter order (for reorder)" }
                                }
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
                ParseSignatureChanges(p.Raw("changes"), p.ToolName),
                p.Optional("preview", true),
                p.Optional<string>("typeName"),
                p.Optional<string>("memberName"),
                p.OptionalStruct<int>("overloadIndex"),
                cancellationToken)),
            new ToolDefinition("extract_method", ReadOnly: false,
            (object)new
            {
                name = "extract_method",
                description = @"Extract selected statements into a new method. Uses data flow analysis to determine parameters and return type.

USAGE: Specify startLine/endLine range containing complete statements inside a method.
OUTPUT: extractedCode (the new method), replacementCode (the call to insert), detected parameters and return type.
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
                        accessibility = new { type = "string", description = "Accessibility: private, public, internal (default: private)" },
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

USAGE: Position cursor on class declaration that implements interface or extends abstract class.
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
OUTPUT: Generated property wrapping the field.
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
                description = @"Inline a variable, replacing all usages with its value.

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
                description = @"Extract an expression to a local variable.

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

METRICS: cyclomatic (decision points), nesting (max depth), loc (lines), parameters (count), cognitive (Sonar-style)
USAGE: get_complexity_metrics(filePath) for file, or add line/column for specific method
OUTPUT: Per-method breakdown with all requested metrics
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Optional: zero-based line for specific method" },
                        column = new { type = "integer", description = "Optional: zero-based column" },
                        metrics = new { type = "array", items = new { type = "string" }, description = "Optional: specific metrics [cyclomatic, nesting, loc, parameters, cognitive]" }
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
                description = @"Add ArgumentNullException.ThrowIfNull guard clauses for nullable parameters.

USAGE: Address the method by typeName+memberName (or position cursor on its declaration).
OUTPUT: Generated guard clauses inserted at method start.
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
OUTPUT: Generated equality members comparing all instance fields and properties.
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
OUTPUT: Results for each type with members, or error if type not found
BENEFIT: One call instead of multiple - reduces context usage for AI agents",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeNames = new { type = "array", items = new { type = "string" }, description = "Array of type names to look up" },
                        includeInherited = new { type = "boolean", description = "Include inherited members (default: false)" },
                        memberKind = new { type = "string", description = "Filter: 'Method', 'Property', 'Field', 'Event'" },
                        verbosity = new { type = "string", description = "'summary', 'compact' (default), or 'full'" },
                        maxResultsPerType = new { type = "integer", description = "Max members per type (default: 50)" }
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
                description = @"Find all types and members decorated with a specific attribute.

USAGE: find_attribute_usages(attributeName: ""Authorize"")
USAGE: find_attribute_usages(attributeName: ""HttpGet"", projectName: ""MyApi"")

OUTPUT: List of symbols with the attribute, their kind, arguments, and source location.
Use for: finding all API endpoints, authorization points, serialization config, test fixtures.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        attributeName = new { type = "string", description = "Attribute name (e.g. 'Authorize', 'HttpGet', 'Obsolete')" },
                        projectName = new { type = "string", description = "Filter to specific project" },
                        maxResults = new { type = "integer", description = "Maximum results (default: 100)" }
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
                description = @"Scan for dependency injection service registrations (AddScoped, AddTransient, AddSingleton, etc.).

USAGE: get_di_registrations()
USAGE: get_di_registrations(projectName: ""MyApi"")

OUTPUT: List of DI registrations with lifetime, service type, implementation type, and location.
Use for: understanding service wiring, finding missing registrations, auditing lifetimes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Filter to specific project" }
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

OUTPUT: List of reflection API calls with the API used, context, and location.
Use for: finding hidden dependencies before refactoring, security audits, understanding dynamic behavior.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Filter to specific project" },
                        maxResults = new { type = "integer", description = "Maximum results (default: 100)" }
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

USAGE: find_circular_dependencies() — project-level cycles
USAGE: find_circular_dependencies(level: ""namespace"") — namespace-level cycles

OUTPUT: Dependency graph with any detected cycles listed.
Use for: architecture analysis, identifying tightly coupled components.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        level = new { type = "string", description = "'project' (default) or 'namespace'" }
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
                description = @"List NuGet package references per project with versions.

USAGE: get_nuget_dependencies()
USAGE: get_nuget_dependencies(projectName: ""MyApp"")

OUTPUT: List of projects with their NuGet packages, versions, and asset settings.
Use for: dependency audits, version checks, understanding external dependencies.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Filter to specific project" }
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

OUTPUT: List of generators with their assembly info and generated files.
Use for: understanding generated code, debugging generator issues.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Filter to specific project" }
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

OUTPUT: Full source code of the generated file.
Use get_source_generators first to discover available generated files.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Project containing the generated file" },
                        generatedFileName = new { type = "string", description = "Name of the generated file" }
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
                description = "Composite audit dashboard for one project: aggregates diagnostics (with analyzers), unused code, god-object candidates, untested public surface, and async-hygiene issues. Returns counts + top-N hotspots per dimension. One call replaces 5+ separate tool calls for a project audit overview.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Project to audit. Required." },
                        includeAnalyzers = new { type = "boolean", description = "Run configured DiagnosticAnalyzers in addition to compiler diagnostics (default: true)." },
                        topN = new { type = "integer", description = "Hotspots per dimension (default: 5)." }
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
                description = "Detect over-coupled types ('god objects'). For each type computes efferent coupling (distinct types it depends on), afferent coupling (distinct types that depend on it), and member count; reports types meeting both threshold filters, scored as efferent*0.4 + members*0.4 + afferent*0.2. AUDIT-TIME tool — O(types*references); don't call in tight agent loops.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Limit scan to one project (default: scan all projects)" },
                        minEfferentCoupling = new { type = "integer", description = "Minimum outbound type dependencies to qualify (default: 20)" },
                        minMembers = new { type = "integer", description = "Minimum member count to qualify (default: 20)" },
                        maxResults = new { type = "integer", description = "Cap on candidates returned (default: 20). totalCount reflects unbounded match count." }
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
                description = "Find public/internal surface in a project that no test method (xUnit [Fact]/[Theory], NUnit [Test]/[TestCase], MSTest [TestMethod]) transitively reaches. Sorted by cyclomatic complexity so the riskiest untested code is reported first. Audit-time tool; runs solution-wide BFS.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Project to audit (default: first project in solution)" },
                        includeProperties = new { type = "boolean", description = "Include properties (default: true)" },
                        includeInternal = new { type = "boolean", description = "Include internal members (default: false; only public)" },
                        maxResults = new { type = "integer", description = "Cap on reported items (default: 50). totalCount reflects the true unbounded count." }
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
                description = "Multi-hop transitive call graph (callers, callees, or both) from a method. Replaces N round-trips of find_callers/get_outgoing_calls when tracing impact or understanding a subsystem. Returns nodes + edges (not a nested tree, to handle diamond shapes), with cycle detection and a mandatory maxDepth bound. IMPORTANT: Uses ZERO-BASED coordinates.",
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
                        maxNodes = new { type = "integer", description = "Max total nodes (default: 100). Sets truncatedByNodes when hit." }
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
            new ToolDefinition("get_external_type_info", ReadOnly: true,
            (object)new
            {
                name = "get_external_type_info",
                description = "Inspect types from external assemblies (NuGet packages, BCL, closed-source) the solution references. Returns members, signatures, and structured XML docs (summary/returns/remarks/params/typeParams/exceptions) for types whose source isn't in the solution. Eliminates the need for agents to guess at dependency APIs.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Fully-qualified type name. Use backtick-arity for generics, e.g. 'System.Collections.Generic.List`1'." },
                        assemblyName = new { type = "string", description = "Optional: restrict resolution to a specific assembly when the type name is ambiguous." },
                        includeInherited = new { type = "boolean", description = "Include members from base types (default: true). Stops before System.Object." },
                        includeXmlDocs = new { type = "boolean", description = "Include XML doc summaries when available (default: true)." },
                        maxMembers = new { type = "integer", description = "Cap on member entries (default: 200). totalCount reflects the unbounded member count." }
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

USAGE: move_type_to_file(typeName: ""MySecondClass"") - the type's usings and namespace shape (file-scoped or block) are preserved in both contents.
OUTPUT: newFilePath plus the full newFileContent and updatedOriginContent. GENERATION-ONLY: never writes - YOU write both files (Write/Edit), then call sync_documents. A type already alone in its file is a no-op success.
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
OUTPUT: newFilePath, movedMembers, partialModifierAdded, plus the full newFileContent and updatedOriginContent. Both halves carry the `partial` modifier. GENERATION-ONLY: never writes - YOU write both files (Write/Edit), then call sync_documents.
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
                description = @"Public-API breaking-change report against a git ref - the release-prep tool. Rebuilds the baseline API surface from git history (read-only git ls-tree/show) and diffs it against the current code.

USAGE: diff_api_surface(baselineRef: ""v1.5.3"") or any ref (""HEAD~5"", a branch, a commit). Optional projectName restricts to one project.
OUTPUT: breaking[] (removed symbols and changed signatures, including accessibility reductions) and added[] - locations on the current side. Members of a wholly removed or added type collapse into the single type entry.
REQUIRES: the solution must live in a git repository and the ref must exist locally.
NOTE: compares declaration signatures; base-list and attribute changes are not reported.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        baselineRef = new { type = "string", description = "Git ref to diff against: tag, branch, or commit (e.g. 'v1.5.3', 'HEAD~5')" },
                        projectName = new { type = "string", description = "Optional: restrict the comparison to one project (default: every project in the solution)" }
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
OUTPUT: escaping[] (exceptionType + a sample throw site) and caught[] (exceptionType, the catch location in the analyzed method, and the originating throw site). Bare `throw;` counts as the enclosing catch's type.
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
                description = @"Find methods structurally similar to a query method or snippet - the anti-duplication check before implementing a helper that may already exist. Fingerprints are 4-gram shingles over normalized token kinds (identifiers and literals are wildcards), scored by Jaccard similarity, so renamed variables and different constants still match.

USAGE: find_similar_code(typeName: ""OrderService"", methodName: ""Validate"") or find_similar_code(snippet: ""<code>"") - exactly one query mode.
OUTPUT: matching methods with score (0-1), location, and statement count, best first. Candidates are source methods with at least 3 statements.
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
                        maxResults = new { type = "integer", description = "Maximum matches to return, best first (default: 10)" }
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
                description = @"Paste a .NET stack trace - frames come back mapped to file/line/symbol with source snippets, compiler name mangling undone: async state machines (Type+<Method>d__N.MoveNext), lambdas (<Method>b__N_M incl. <>c containers), and local functions (<Method>g__Local|N_M).

USAGE: resolve_stack_trace(stackTrace: ""<ex.ToString() or log paste>"").
OUTPUT: ordered frames {raw, symbol, frameKind (method/async/lambda/localFunction/text), resolved, location, snippet}. Frames with file:line pointing into the solution resolve to that exact statement; frames without resolve to the declaration by name. BCL/third-party frames pass through with resolved: false. Lines are ZERO-based like every other tool (trace's 1-based numbers are converted).",
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
OUTPUT: removed[] and skipped[] per symbol, and files[] with each touched file's FULL updatedContent. GENERATION-ONLY: never writes - YOU write each updatedContent (Write/Edit), then sync_documents and get_diagnostics. Re-run to confirm nothing remains.
NOTE: inherits find_unused_code's heuristics - verify with find_references before deleting anything public.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Restrict the sweep to one project (default: all projects)" },
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
                description = @"Generate a compilable test skeleton for a target method - pairs with find_untested_code. The test framework is detected from the solution's test projects (xUnit/NUnit/MSTest attribute types resolving in a compilation; none found defaults to xUnit with frameworkDetected: false). The SUT is constructed via the public constructor with the fewest parameters; interface-typed arguments become TODO-marked default! placeholders; a Task-returning target produces an async Task test.

USAGE: generate_test_stub(typeName: ""OrderService"", methodName: ""Submit"").
OUTPUT: testCode, testClassName, suggestedFilePath (the existing {Type}Tests.cs when present, else the test project root), framework. GENERATION-ONLY: never writes - YOU write the stub and fill in the TODOs.",
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
                description = @"Enforce dependency rules over the real type-dependency graph: every type-to-type edge (signature types plus body usages - object creation, casts, typeof, call receivers, local declarations) is checked against your deny rules.

USAGE: check_architecture(rules: [{ ""from"": ""MyApp.Domain*"", ""deny"": ""MyApp.Infrastructure*"" }]). Globs (* and ?) match namespaces by default; level=""project"" matches project names instead.
OUTPUT: violations[] with fromType, toType, the matched rule, and a representative source location (null when the dependency is signature-only), plus rulesEvaluated and edgesChecked.",
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
                ParseArchitectureRules(p.Raw("rules"), p.ToolName),
                p.Optional("level", "namespace"),
                cancellationToken)),
            new ToolDefinition("find_unused_dependencies", ReadOnly: true,
            (object)new
            {
                name = "find_unused_dependencies",
                description = @"Which declared PackageReference / ProjectReference entries the compiler never actually needs - Roslyn's own GetUsedAssemblyReferences verdict per project.

USAGE: find_unused_dependencies() or find_unused_dependencies(projectName: ""MyApp"").
OUTPUT: per project - unusedPackages, unusedProjectReferences, and skipped entries with reasons (PrivateAssets=all build assets; packages contributing no compile-time assemblies like analyzers/source generators cannot be judged this way).
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
OUTPUT: deadBranches[] with the owning method, the first unreachable statement's location, and a snippet; methodsAnalyzed for scan scope. Top-level bodies only - a local function's or lambda's interior flow is not walked.",
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
                description = @"Compute the using directives that fix a file's CS0246/CS0103 diagnostics - compiler-accurate import suggestion over every namespace the compilation can see (source and referenced metadata).

USAGE: add_missing_imports(filePath: ""src/MyService.cs"").
OUTPUT: added[] (each name with its namespace and the using line) plus updatedContent with the usings inserted at their sorted position WITHOUT reordering existing ones; ambiguous[] names report their candidate namespaces and change nothing; unresolvable[] names have no candidate. GENERATION-ONLY: never writes - YOU write updatedContent, then sync_documents and get_diagnostics.",
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
                description = @"Naming-convention audit honoring the solution's own .editorconfig dotnet_naming_* rules; when none are configured it falls back to conventional defaults (PascalCase types/methods/properties, camelCase parameters/locals, _camelCase private instance fields - const/static fields exempt).

USAGE: find_naming_violations() or find_naming_violations(projectName: ""MyApp"").
OUTPUT: violations[] {symbol, kind, rule, expected, location}; rulesSource says 'editorconfig' or 'defaults'; unsupportedRules lists configured rules outside the v1 scope rather than dropping them silently.
V1 SCOPE: kind + accessibility filters, pascal_case/camel_case (incl. word_separator), required prefix/suffix. required_modifiers and other capitalizations are reported unsupported; editorconfig rule precedence is approximated and severity is ignored.",
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
OUTPUT: groups[] {containingClass, members[] {name, style: 'classic'|'extensionBlock', kind, signature, location}}. Source-declared extensions only; an extension block applies when the target type is (or implicitly reference-converts to) its receiver type.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Target type (fully-qualified, simple, or unique partial name)" },
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
                description = @"Surface [InterceptsLocation] call rerouting - the hidden control flow behind ASP.NET minimal APIs, EF compiled models, and hand-written interceptors. Scans every tree of the generator-augmented compilation (generated code included, where most interceptors live), matching the attribute by simple name.

USAGE: find_interceptors() or find_interceptors(projectName: ""MyApi"").
OUTPUT: interceptors[] {interceptorMethod, interceptorLocation, target, inGeneratedCode}. Legacy (filePath, line, character) targets convert to ZERO-based line/column and include the intercepted line's text when the file is a solution document; (version, data) targets surface the raw data string undecoded.",
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
                description = @"Full XML documentation for a symbol with <inheritdoc> resolved recursively - other tools return the raw tag. A member-level <inheritdoc> (bare or cref) is expanded element-by-element: inherited content fills the sections, params, and exceptions the local doc omits.

USAGE: get_documentation(typeName: ""OrderService"") for a type, or with memberName (+ overloadIndex) for a member.
OUTPUT: documentation {summary, returns, remarks, params[], typeParams[], exceptions[]}, inheritedFrom (the symbol docs were inherited from, or null), and unresolved ('cycle' | 'no-inherited-member' | 'unresolved-cref' when an inheritdoc could not be fully resolved). A bare inheritdoc follows the overridden member, the explicit interface member, or the implicitly implemented one.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        typeName = new { type = "string", description = "Type to document (fully-qualified, simple, or unique partial name)" },
                        memberName = new { type = "string", description = "Member to document; omit to document the type itself" },
                        overloadIndex = new { type = "integer", description = "Zero-based overload index when memberName is overloaded (see get_method_overloads)" }
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

USAGE: fix_all(diagnosticId: ""CS0219"", scope: ""solution""). scope 'file' requires filePath; 'project' requires projectName. fixIndex picks among the available fixes (default 0; see get_code_fixes).
OUTPUT: files[] with each touched file's full updatedContent, diagnosticsMatched, fixTitle, and fallbackMode. GENERATION-ONLY: never writes - YOU write each updatedContent, then sync_documents and get_diagnostics.
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
                description = @"Which tests cover a symbol - the inverse of find_untested_code. Each test method's transitive reach is computed by BFS over in-source callees; a test covers the target when its reach includes it.

USAGE: find_tests(typeName: ""OrderService"", memberName: ""Submit""). Omit memberName to ask which tests touch ANY member of the type. overloadIndex disambiguates overloads.
OUTPUT: coveringTests[] {testMethod, containingType, testProject, location} and totalTests.
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
OUTPUT: overrides[] (the OverriddenX chain, root-ward) and implements[] (interface members), each {signature, containingType, location|inMetadata}.
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
OUTPUT: matches[] {location, lineText, kind} over source code (no generated trees, no metadata).",
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
                description = @"Where a type appears in a type test or pattern - `x is T`, `x as T`, declaration/type/recursive patterns against T.

USAGE: find_pattern_usages(typeName: ""HttpException"").
OUTPUT: matches[] {location, lineText, kind} where kind is isExpression/asExpression/declarationPattern/typePattern/recursivePattern.",
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
OUTPUT: matches[] {location, lineText, kind} over source code.",
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
                description = @"Where an exception type is caught - `catch (T ...)` clauses. With includeDerived (default true), also catch clauses whose declared type is a base of T (which would catch a T).

USAGE: find_catch_blocks(typeName: ""IOException"").
OUTPUT: matches[] {location, lineText, kind} over source code.",
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
                description = @"Async-hygiene audit of the three highest-signal problems: async void that isn't an event handler (its exceptions crash the process), blocking on async work (.Result / .Wait() / .GetAwaiter().GetResult() on a Task), and a CancellationToken parameter that isn't forwarded to an awaited call that could take one.

USAGE: find_async_issues() or find_async_issues(projectName: ""MyApp"").
OUTPUT: issues[] {category: asyncVoid|blockingCall|tokenNotForwarded, symbol, detail, location} and countByCategory. The total also appears in get_project_health.",
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

    private static List<SignatureChange> ParseSignatureChanges(JsonNode? changesNode, string toolName)
    {
        var changes = new List<SignatureChange>();
        if (changesNode is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject obj)
                {
                    var change = new SignatureChange
                    {
                        Action = OptionalString(obj, "action", toolName) ?? "",
                        Name = OptionalString(obj, "name", toolName),
                        Type = OptionalString(obj, "type", toolName),
                        NewName = OptionalString(obj, "newName", toolName),
                        DefaultValue = OptionalString(obj, "defaultValue", toolName),
                        Position = OptionalInt(obj, "position", toolName)
                    };

                    if (obj["order"] is JsonArray orderArray)
                    {
                        change.Order = orderArray.Select((o, i) => ElementString(o, "order", i, toolName)).ToList();
                    }

                    changes.Add(change);
                }
            }
        }
        return changes;
    }

    private static List<ArchitectureRule> ParseArchitectureRules(JsonNode? rulesNode, string toolName)
    {
        var rules = new List<ArchitectureRule>();
        if (rulesNode is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject obj)
                {
                    rules.Add(new ArchitectureRule
                    {
                        From = OptionalString(obj, "from", toolName) ?? "",
                        Deny = OptionalString(obj, "deny", toolName) ?? ""
                    });
                }
            }
        }
        return rules;
    }

    private static List<Dictionary<string, object>> ParseMethodBatchRequests(JsonNode? methodsNode, string toolName)
    {
        var methods = new List<Dictionary<string, object>>();
        if (methodsNode is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject obj)
                {
                    var method = new Dictionary<string, object>();
                    if (OptionalString(obj, "typeName", toolName) is { } typeName) method["typeName"] = typeName;
                    if (OptionalString(obj, "methodName", toolName) is { } methodName) method["methodName"] = methodName;
                    if (OptionalInt(obj, "overloadIndex", toolName) is { } overloadIndex) method["overloadIndex"] = overloadIndex;
                    methods.Add(method);
                }
            }
        }
        return methods;
    }

    // Typed extraction for nested object-array params. A field that is PRESENT but the
    // wrong type surfaces as a structured -32602 (JsonRpcInvalidParamsException) instead
    // of a raw JSON exception that would escape the dispatcher as -32603. Absent fields
    // return null. The message names the tool, matching the scalar accessors in
    // JsonRpcParameters so every -32602 reads alike.
    private static string? OptionalString(JsonObject obj, string key, string toolName)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        try { return node.GetValue<string>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new JsonRpcInvalidParamsException($"Parameter '{key}' for tool '{toolName}' must be a string");
        }
    }

    private static int? OptionalInt(JsonObject obj, string key, string toolName)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null) return null;
        try { return node.GetValue<int>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            throw new JsonRpcInvalidParamsException($"Parameter '{key}' for tool '{toolName}' must be an integer");
        }
    }

    private static string ElementString(JsonNode? node, string key, int index, string toolName)
    {
        if (node is null) return "";
        try { return node.GetValue<string>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new JsonRpcInvalidParamsException($"Parameter '{key}[{index}]' for tool '{toolName}' must be a string");
        }
    }
}
