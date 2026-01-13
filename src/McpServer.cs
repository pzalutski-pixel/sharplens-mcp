using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpLensMcp;

public class McpServer
{
    private readonly ProcessManager _processManager;
    private readonly JsonSerializerOptions _jsonOptions;

    public McpServer(ProcessManager processManager)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public async Task RunAsync()
    {
        Log(LogLevel.Information, "Roslyn MCP Server starting...");

        // Auto-load solution from environment variable
        var solutionPath = Environment.GetEnvironmentVariable("DOTNET_SOLUTION_PATH");
        if (!string.IsNullOrEmpty(solutionPath))
        {
            try
            {
                // If it's a directory, try to find a .sln or .slnx file
                if (Directory.Exists(solutionPath))
                {
                    var slnFiles = Directory.GetFiles(solutionPath, "*.sln")
                        .Concat(Directory.GetFiles(solutionPath, "*.slnx"))
                        .ToArray();
                    if (slnFiles.Length > 0)
                    {
                        solutionPath = slnFiles[0];
                    }
                }

                if (File.Exists(solutionPath))
                {
                    Log(LogLevel.Information, $"Auto-loading solution: {solutionPath}");
                    var proxy = await _processManager.EnsureWorkerAsync();
                    await proxy.InvokeToolAsync("load_solution", new Dictionary<string, object?>
                    {
                        ["solutionPath"] = solutionPath
                    });
                    _processManager.LastLoadedSolutionPath = solutionPath;
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Failed to auto-load solution: {ex.Message}");
            }
        }

        // Main event loop - read from stdin, write to stdout
        using var reader = Console.In;
        using var writer = Console.Out;

        while (true)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                if (line == null)
                {
                    Log(LogLevel.Information, "Received EOF on stdin, shutting down");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Log(LogLevel.Debug, $"Received request: {line}");

                var response = await HandleRequestAsync(line);

                var responseJson = JsonSerializer.Serialize(response, _jsonOptions);
                await writer.WriteLineAsync(responseJson);
                await writer.FlushAsync();

                Log(LogLevel.Debug, $"Sent response: {responseJson}");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Error in main loop: {ex}");
            }
        }
    }

    private async Task<object> HandleRequestAsync(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<JsonObject>(requestJson);
            if (request == null)
            {
                return CreateErrorResponse(null, -32700, "Parse error");
            }

            var id = request["id"]?.GetValue<int>();
            var method = request["method"]?.GetValue<string>();
            var paramsNode = request["params"];

            if (string.IsNullOrEmpty(method))
            {
                return CreateErrorResponse(id, -32600, "Invalid Request: missing method");
            }

            return method switch
            {
                "initialize" => await HandleInitializeAsync(id),
                "tools/list" => await HandleListToolsAsync(id),
                "tools/call" => await HandleToolCallAsync(id, paramsNode?.AsObject()),
                _ => CreateErrorResponse(id, -32601, $"Method not found: {method}")
            };
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Error handling request: {ex}");
            return CreateErrorResponse(null, -32603, $"Internal error: {ex.Message}");
        }
    }

    private Task<object> HandleInitializeAsync(int? id)
    {
        var response = CreateSuccessResponse(id, new
        {
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = "Roslyn MCP Server",
                version = "1.0.0"
            }
        });
        return Task.FromResult(response);
    }

    private Task<object> HandleListToolsAsync(int? id)
    {
        var tools = new List<object>
        {
            (object)new
            {
                name = "roslyn:health_check",
                description = "Check the health and status of the Roslyn MCP server and workspace. Returns: server status, solution loaded state, project count, and memory usage. Call this first to verify the server is ready.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { }
                }
            },
            (object)new
            {
                name = "roslyn:load_solution",
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
            (object)new
            {
                name = "roslyn:unload_solution",
                description = @"Unload solution and release all file locks (including source generator DLLs). Call before rebuilding projects with source generators. Must call load_solution again to continue analysis.

WORKFLOW:
1. Call unload_solution before running builds
2. Perform your build (dotnet build, etc.)
3. Call load_solution to resume analysis

This terminates the worker process to release all file handles held by MSBuildWorkspace.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        force = new { type = "boolean", description = "Force kill worker if graceful shutdown times out (default: true)" }
                    }
                }
            },
            (object)new
            {
                name = "roslyn:sync_documents",
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
            (object)new
            {
                name = "roslyn:get_symbol_info",
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
            (object)new
            {
                name = "roslyn:go_to_definition",
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
            (object)new
            {
                name = "roslyn:find_references",
                description = "Find all references to a symbol across the entire solution. Returns file paths, line numbers, and code context for each reference. IMPORTANT: Uses ZERO-BASED coordinates (editor line 10 = pass line 9).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file containing the symbol" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" },
                        maxResults = new { type = "integer", description = "Maximum number of references to return (default: 100). Results are truncated with a hint if limit is exceeded." }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (object)new
            {
                name = "roslyn:find_implementations",
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
            (object)new
            {
                name = "roslyn:get_type_hierarchy",
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
            (object)new
            {
                name = "roslyn:search_symbols",
                description = "Search for types, methods, properties, etc. by name across the solution. Supports glob patterns (e.g., '*Handler' finds classes ending with 'Handler', 'Get*' finds symbols starting with 'Get'). Use ? for single character wildcard. PAGINATION: Returns totalCount and hasMore. Use offset to paginate through results.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "Search query - supports wildcards: * (any characters), ? (single character). Examples: 'Handler', '*Handler', 'Get*', 'I?Service'. Case-insensitive." },
                        kind = new { type = "string", description = "Optional: filter by symbol kind. For types use: Class, Interface, Struct, Enum, Delegate. For members use: Method, Property, Field, Event. Other: Namespace. Case-insensitive." },
                        maxResults = new { type = "integer", description = "Maximum number of results per page (default: 50)" },
                        namespaceFilter = new { type = "string", description = "Optional: filter by namespace (supports wildcards). Examples: 'MyApp.Core.*', '*.Services', 'MyApp.*.Handlers'. Case-insensitive." },
                        offset = new { type = "integer", description = "Offset for pagination (default: 0). Use pagination.nextOffset from previous response to get next page." }
                    },
                    required = new[] { "query" }
                }
            },
            (object)new
            {
                name = "roslyn:semantic_query",
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
                        accessibility = new { type = "string", description = "Optional: filter by accessibility. Values: Public, Private, Internal, Protected, ProtectedInternal, PrivateProtected" },
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
            (object)new
            {
                name = "roslyn:get_diagnostics",
                description = "Get compiler errors, warnings, and info messages for a file or entire project. Returns: list of diagnostics with id, message, severity, and location. Use before committing to catch issues.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Optional: path to specific file, omit for all files" },
                        projectPath = new { type = "string", description = "Optional: path to specific project" },
                        severity = new { type = "string", description = "Optional: filter by severity (Error, Warning, Info)" },
                        includeHidden = new { type = "boolean", description = "Include hidden diagnostics (default: false)" }
                    }
                }
            },
            (object)new
            {
                name = "roslyn:get_code_fixes",
                description = "Get available code fixes for a specific diagnostic. Returns list of fix titles and descriptions. WORKFLOW: (1) get_diagnostics to find issues, (2) get_code_fixes to see options, (3) apply_code_fix to fix.",
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
            (object)new
            {
                name = "roslyn:apply_code_fix",
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
            (object)new
            {
                name = "roslyn:get_project_structure",
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
            (object)new
            {
                name = "roslyn:organize_usings",
                description = "Sort and remove unused using directives in a file. Returns the modified file content. Automatically removes unused usings and sorts alphabetically.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" }
                    },
                    required = new[] { "filePath" }
                }
            },
            (object)new
            {
                name = "roslyn:organize_usings_batch",
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
            (object)new
            {
                name = "roslyn:format_document_batch",
                description = "Format multiple documents in a project using Roslyn's NormalizeWhitespace. Ensures consistent indentation, spacing, and line breaks. PREVIEW mode by default - set preview=false to apply changes.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        projectName = new { type = "string", description = "Optional: Project name to format. If omitted, formats all projects in solution." },
                        includeTests = new { type = "boolean", description = "Include test projects (default: true). Set to false to skip projects with 'Test' in the name." },
                        preview = new { type = "boolean", description = "Preview mode (default: true). Set to false to apply changes to disk. ALWAYS preview first!" }
                    },
                    required = new string[] { }
                }
            },
            (object)new
            {
                name = "roslyn:get_method_overloads",
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
            (object)new
            {
                name = "roslyn:get_containing_member",
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
            (object)new
            {
                name = "roslyn:find_callers",
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
            (object)new
            {
                name = "roslyn:find_unused_code",
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
                        symbolKindFilter = new { type = "string", description = "Optional: filter by symbol kind (Class, Method, Property, Field)" },
                        maxResults = new { type = "integer", description = "Maximum results to return (default: 50, helps manage large outputs)" }
                    }
                }
            },
            (object)new
            {
                name = "roslyn:rename_symbol",
                description = "Safely rename a symbol (type, method, property, etc.) across the entire solution. Uses Roslyn's semantic analysis to ensure all references are updated. SUPPORTS PREVIEW MODE - always preview first! IMPORTANT: Uses ZERO-BASED coordinates. Default shows first 20 files with summary verbosity.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file containing the symbol" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" },
                        newName = new { type = "string", description = "New name for the symbol" },
                        preview = new { type = "boolean", description = "Preview changes without applying (default: true). ALWAYS preview first!" },
                        maxFiles = new { type = "integer", description = "Max files to show in preview (default: 20, prevents large outputs)" },
                        verbosity = new { type = "string", description = "Output detail level: 'summary' (default, file paths + counts only ~200 tokens/file), 'compact' (add locations ~500 tokens/file), 'full' (include old/new text ~3000+ tokens/file)" }
                    },
                    required = new[] { "filePath", "line", "column", "newName" }
                }
            },
            (object)new
            {
                name = "roslyn:extract_interface",
                description = @"Generate an interface from a class or struct. Extracts all public instance members (methods, properties, events).

USAGE: Position on class declaration, provide interfaceName=""IMyService"".
OUTPUT: Generated interface code ready to insert. Useful for dependency injection and testability.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file containing the class" },
                        line = new { type = "integer", description = "Zero-based line number (editor line - 1)" },
                        column = new { type = "integer", description = "Zero-based column number (editor column - 1)" },
                        interfaceName = new { type = "string", description = "Name for the new interface (e.g., 'IMyService')" },
                        includeMemberNames = new { type = "array", items = new { type = "string" }, description = "Optional: specific member names to include (omit to include all public members)" }
                    },
                    required = new[] { "filePath", "line", "column", "interfaceName" }
                }
            },
            (object)new
            {
                name = "roslyn:dependency_graph",
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

            // ============ NEW TOOLS: Name-Based Type Discovery ============

            (object)new
            {
                name = "roslyn:get_type_members",
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
            (object)new
            {
                name = "roslyn:get_method_signature",
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
            (object)new
            {
                name = "roslyn:get_attributes",
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
            (object)new
            {
                name = "roslyn:get_derived_types",
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
            (object)new
            {
                name = "roslyn:get_base_types",
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
            (object)new
            {
                name = "roslyn:analyze_data_flow",
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
            (object)new
            {
                name = "roslyn:analyze_control_flow",
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

            // ============ COMPOUND TOOLS ============

            (object)new
            {
                name = "roslyn:get_type_overview",
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
            (object)new
            {
                name = "roslyn:analyze_method",
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
                        maxOutgoingCalls = new { type = "integer", description = "Max outgoing calls to return (default: 50)" }
                    },
                    required = new[] { "typeName", "methodName" }
                }
            },
            (object)new
            {
                name = "roslyn:get_file_overview",
                description = @"Get comprehensive file overview in ONE CALL: diagnostics summary + type declarations + namespace + line count.

USAGE: get_file_overview(""path/to/MyClass.cs"")",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" }
                    },
                    required = new[] { "filePath" }
                }
            },

            // Phase 2: AI-Focused Tools
            (object)new
            {
                name = "roslyn:get_missing_members",
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
            (object)new
            {
                name = "roslyn:get_outgoing_calls",
                description = "Get all methods and properties that a method calls. Helps understand method dependencies and behavior. Returns list of called symbols with locations. IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number inside the method" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        maxDepth = new { type = "integer", description = "How deep to trace calls (1 = direct only, default: 1)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (object)new
            {
                name = "roslyn:validate_code",
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
            (object)new
            {
                name = "roslyn:check_type_compatibility",
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
            (object)new
            {
                name = "roslyn:get_instantiation_options",
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
            (object)new
            {
                name = "roslyn:analyze_change_impact",
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
            (object)new
            {
                name = "roslyn:get_method_source",
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
            (object)new
            {
                name = "roslyn:get_method_source_batch",
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
            (object)new
            {
                name = "roslyn:generate_constructor",
                description = @"Generate a constructor from fields and/or properties of a type.

USAGE: Position on class/struct declaration. Use includeProperties=true for auto-properties.
OUTPUT: constructorCode string ready to paste, parameter list, and field assignments.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file containing the type" },
                        line = new { type = "integer", description = "Zero-based line number on the type declaration" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        includeProperties = new { type = "boolean", description = "Include properties with setters (default: false)" },
                        initializeToDefault = new { type = "boolean", description = "Use ?? default for nullable types (default: false)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (object)new
            {
                name = "roslyn:change_signature",
                description = @"Change a method signature and preview impact on all call sites.

ACTIONS: add (new param), remove, rename, reorder parameters.
WORKFLOW: (1) Call with preview=true (default) to see affected call sites, (2) Review changes, (3) Call with preview=false to apply.
OUTPUT: oldSignature, newSignature, list of call sites needing updates.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file containing the method" },
                        line = new { type = "integer", description = "Zero-based line number on the method" },
                        column = new { type = "integer", description = "Zero-based column number" },
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
                    required = new[] { "filePath", "line", "column", "changes" }
                }
            },
            (object)new
            {
                name = "roslyn:extract_method",
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
            (object)new
            {
                name = "roslyn:get_code_actions_at_position",
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
            (object)new
            {
                name = "roslyn:apply_code_action_by_title",
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
            (object)new
            {
                name = "roslyn:implement_missing_members",
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
            (object)new
            {
                name = "roslyn:encapsulate_field",
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
            (object)new
            {
                name = "roslyn:inline_variable",
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
            (object)new
            {
                name = "roslyn:extract_variable",
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
            (object)new
            {
                name = "roslyn:get_complexity_metrics",
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
            (object)new
            {
                name = "roslyn:add_null_checks",
                description = @"Add ArgumentNullException.ThrowIfNull guard clauses for nullable parameters.

USAGE: Position cursor on a method with reference type parameters.
OUTPUT: Generated guard clauses inserted at method start.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number on the method" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        preview = new { type = "boolean", description = "Preview mode (default: true)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (object)new
            {
                name = "roslyn:generate_equality_members",
                description = @"Generate Equals, GetHashCode, and == / != operators for a type.

USAGE: Position cursor on a class or struct declaration.
OUTPUT: Generated equality members comparing all instance fields and properties.
IMPORTANT: Uses ZERO-BASED coordinates (editor line - 1).",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        filePath = new { type = "string", description = "Absolute path to source file" },
                        line = new { type = "integer", description = "Zero-based line number on the type" },
                        column = new { type = "integer", description = "Zero-based column number" },
                        includeOperators = new { type = "boolean", description = "Include == and != operators (default: true)" },
                        preview = new { type = "boolean", description = "Preview mode (default: true)" }
                    },
                    required = new[] { "filePath", "line", "column" }
                }
            },
            (object)new
            {
                name = "roslyn:get_type_members_batch",
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
            }
        };

        return Task.FromResult(CreateSuccessResponse(id, new { tools }));
    }

    private async Task<object> HandleToolCallAsync(int? id, JsonObject? paramsNode)
    {
        try
        {
            var name = paramsNode?["name"]?.GetValue<string>();
            var arguments = paramsNode?["arguments"]?.AsObject();

            if (string.IsNullOrEmpty(name))
            {
                return CreateErrorResponse(id, -32602, "Invalid params: missing tool name");
            }

            object result;

            // Handle unload_solution locally (terminates worker)
            if (name == "roslyn:unload_solution")
            {
                var force = arguments?["force"]?.GetValue<bool>() ?? true;
                await _processManager.ShutdownWorkerAsync(force);
                result = new
                {
                    success = true,
                    data = new
                    {
                        message = "Solution unloaded. Worker process terminated. File locks released.",
                        lastSolutionPath = _processManager.LastLoadedSolutionPath
                    },
                    metadata = new
                    {
                        suggestedNextTools = new[] { "load_solution" }
                    }
                };
            }
            // Handle health_check with worker status
            else if (name == "roslyn:health_check")
            {
                var workerInfo = new
                {
                    workerRunning = _processManager.IsWorkerRunning,
                    workerPid = _processManager.WorkerProcessId,
                    workerUptime = _processManager.WorkerUptime?.ToString(@"hh\:mm\:ss"),
                    lastLoadedSolution = _processManager.LastLoadedSolutionPath
                };

                if (_processManager.IsWorkerRunning)
                {
                    // Get health check from worker
                    var proxy = await _processManager.EnsureWorkerAsync();
                    var workerHealth = await proxy.InvokeToolAsync("health_check", null);

                    // Merge worker info into result
                    result = new
                    {
                        success = true,
                        data = new
                        {
                            workerProcess = workerInfo,
                            workerStatus = workerHealth
                        }
                    };
                }
                else
                {
                    result = new
                    {
                        success = true,
                        data = new
                        {
                            workerProcess = workerInfo,
                            workerStatus = (object?)null,
                            message = "Worker not running. Call any tool to start worker, or load_solution to load a solution."
                        }
                    };
                }
            }
            // Track solution path on load_solution
            else if (name == "roslyn:load_solution")
            {
                var solutionPath = arguments?["solutionPath"]?.GetValue<string>();
                var proxy = await _processManager.EnsureWorkerAsync();
                result = await proxy.InvokeToolAsync(
                    StripRoslynPrefix(name),
                    ConvertArgumentsToDict(arguments));
                _processManager.LastLoadedSolutionPath = solutionPath;
            }
            // All other tools: forward to worker
            else
            {
                var proxy = await _processManager.EnsureWorkerAsync();
                result = await proxy.InvokeToolAsync(
                    StripRoslynPrefix(name),
                    ConvertArgumentsToDict(arguments));
            }

            // Wrap result in MCP content format
            var mpcResult = new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(result, _jsonOptions)
                    }
                }
            };

            return CreateSuccessResponse(id, mpcResult);
        }
        catch (FileNotFoundException ex)
        {
            Log(LogLevel.Error, $"File not found: {ex.Message}");
            return CreateErrorResponse(id, -32602, $"File not found: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            Log(LogLevel.Error, $"Worker timeout: {ex.Message}");
            return CreateErrorResponse(id, -32603, $"Worker timeout: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Error executing tool: {ex}");
            return CreateErrorResponse(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private static string StripRoslynPrefix(string toolName)
    {
        return toolName.StartsWith("roslyn:") ? toolName.Substring(7) : toolName;
    }

    private static Dictionary<string, object?>? ConvertArgumentsToDict(JsonObject? arguments)
    {
        if (arguments == null) return null;

        var dict = new Dictionary<string, object?>();
        foreach (var prop in arguments)
        {
            dict[prop.Key] = ConvertJsonNode(prop.Value);
        }
        return dict;
    }

    private static object? ConvertJsonNode(JsonNode? node)
    {
        if (node == null) return null;

        return node switch
        {
            JsonValue value => value.TryGetValue<bool>(out var b) ? b :
                               value.TryGetValue<int>(out var i) ? i :
                               value.TryGetValue<double>(out var d) ? d :
                               value.TryGetValue<string>(out var s) ? s :
                               value.ToString(),
            JsonArray array => array.Select(ConvertJsonNode).ToList(),
            JsonObject obj => obj.ToDictionary(p => p.Key, p => ConvertJsonNode(p.Value)),
            _ => node.ToString()
        };
    }

    private object CreateSuccessResponse(int? id, object result)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            result
        };
    }

    private object CreateErrorResponse(int? id, int code, string message)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message
            }
        };
    }

    private static void Log(LogLevel level, string message) => Logger.Log("McpServer", level, message);
}
