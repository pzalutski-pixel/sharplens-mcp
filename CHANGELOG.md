# Changelog

## [1.5.3] - 2026-05-19

### Added
- `get_external_type_info` — inspect NuGet/BCL/external-assembly types: members, signatures, XML doc summaries
- `get_call_graph` — multi-hop transitive callers/callees with `maxDepth` (1-10), `maxNodes` cap, and cycle detection
- `find_untested_code` — public/internal surface not transitively reached by any xUnit/NUnit/MSTest test method, sorted by cyclomatic complexity
- `find_god_objects` — over-coupled types via efferent + afferent coupling + member-count thresholds
- `get_project_health` — composite audit dashboard: diagnostics + unused + coupling + coverage per project

### Changed
- Tool count: 62 → 67
- `get_diagnostics` now runs configured `DiagnosticAnalyzer`s by default (StyleCop, Roslynator, NetAnalyzers, custom; editorconfig-resolved severities). Set `runAnalyzers: false` for compiler-only output
- `find_references` reports `cast` kind in addition to `read`/`write`/`invocation`/`typeof`/`nameof`/`attribute` and accepts an optional `kind` filter
- `McpServer.HandleToolCallAsync` dispatcher refactored to a typed `JsonRpcParameters` helper. Bad params now produce `-32602 Invalid params` per JSON-RPC spec
- All `dynamic` dispatch eliminated from `src/`; replaced with centralized reflection helpers and typed records
- `RoslynService` exposes an internal `LoadFromWorkspaceForTesting` seam, enabling in-memory `AdhocWorkspace` tests (~5ms) for tools that don't need cross-file references
- `roslyn:health_check` response now wraps its payload in the standard `{success, data, meta}` envelope. Callers must read `data.status` instead of `status`
- `roslyn:generate_constructor` response now carries `appliesEditsAutomatically: false` to make its generation-only semantics explicit
- Added `Microsoft.CodeAnalysis.CSharp.Features` (5.0.0) so C#-specific refactorings (inline temporary, expression-body, invert-if, ~60 more) actually load
- Every tool has an end-to-end MCP-layer integration test in addition to its direct-method test, content-locked against deterministic fixtures
- Test suite hardened: 127 → 543 tests. Eliminated every silent-pass pattern (`data["X"]?.Value<T>()...` short-circuits when the field is missing); the audit pass surfaced both impl bugs (see Fixed below) and MCP-layer response-shape mismatches (`organize_usings_batch`/`format_document_batch` `fileCount`, three field-name bugs in `find_references`/`find_implementations`/`find_overrides`)
- `GetCommonFixSuggestions` and `GetNestedActionsOrEmpty` exposed as `internal` so the per-CS-ID suggestion contract and nested-menu descent helper can be locked by direct unit tests

### Fixed
- Records misclassified as `Class`; `typeKind` and `kind:` filter now report `Record` / `RecordStruct` correctly
- `find_references.kind` was hardcoded to `"read"`; now reflects actual usage with full write detection (assignment LHS, `++`/`--`, `out`/`ref`)
- `change_signature` returned `applied: true` while editing nothing and only matched `MethodDeclarationSyntax`; now applies via `SolutionEditor` to the declaration and every call site, supports methods/constructors/local functions, and updates named-argument labels on rename
- `extract_method` claimed `preview: false` would apply edits but never did; now fully implemented via `SolutionEditor` with snapshot/restore test coverage
- `apply_code_fix` left stale entries in the document/compilation caches after `preview: false`; follow-up `get_diagnostics` saw the pre-fix document and reported the just-fixed diagnostic as still present. Solution swap and cache clears are now hoisted to a single post-write step
- `get_source_generators` reported `IncrementalGeneratorWrapper` for every entry (Roslyn's internal wrapper hides `IIncrementalGenerator`'s real type). Response now unwraps via reflection on the wrapper's `Generator` property and surfaces the actual generator typeName/assembly
- `get_instantiation_options` returned an empty `externalFactories` list for projects with source generators (`SymbolEqualityComparer.Default` returns false across base vs augmented compilations). Target type is now re-resolved per compilation before the comparison
- `analyze_data_flow`, `analyze_control_flow`, and `get_file_overview` had dead `if (document == null)` branches after `GetDocumentAsync` (which throws, never returns null); unknown file paths produced an unstructured rethrow instead of `FileNotInSolution`. Each call site now wraps `GetDocumentAsync` in `try/catch`
- `rename_symbol` accepted invalid C# identifiers like `123invalid`; now returns `INVALID_PARAMETER`
- JSON-RPC notification handling was non-compliant: only `method.StartsWith("notifications/")` was treated as a notification. Per spec §4.3, any request without `id` is a notification — `{"jsonrpc":"2.0","method":"initialize"}` was incorrectly answered with `{"id":null,...}`
- `HandleRequestAsync` returned an error response (id: null) for any exception during dispatch, including exceptions that escaped a notification handler. Outer catch now distinguishes parse-failure, request-with-exception, and notification-with-exception
- `find_attribute_usages` and `find_reflection_usage` lied about `totalCount` when `maxResults` was hit; both now track an unbounded counter
- `search_symbols.totalCount` was capped at `maxResults + 100`; paginated callers now see the true match count
- `AnalyzeDataFlow` / `AnalyzeControlFlow` threw "statements not within the same statement list" on regions spanning a `BlockSyntax`; fixed with sibling-aware resolution
- Relative paths from `FormatPath` didn't round-trip through `GetDocumentAsync`; now resolves solution-relative paths against the solution directory
- `FindTypeByName` returned symbols `SymbolFinder` didn't recognize on generator-using projects; now prefers the base compilation, falling back to augmented only for purely generated types
- `find_untested_code` matched test attributes by simple name (so user-defined `MyApp.Attributes.FactAttribute` looked like xUnit's `[Fact]`); now matches `(namespace, simple name)` tuples
- `find_untested_code` ignored the `includeProperties` parameter; now honored across both the BFS and candidate-collection passes
- `find_god_objects` over-counted efferent coupling — a loose `IdentifierNameSyntax` pass included `nameof()` args, using-directive types, and generic constraints, plus traversed into types declared in referenced source projects. Tightened to object-creation/typeof/cast/var-decl/invocation-receiver and to the target project's own types
- `get_call_graph` cycle detection used dynamic dispatch on anonymous-typed edges; replaced with typed `Dictionary<int, List<int>>` adjacency
- `get_code_actions_at_position` and `apply_code_action_by_title` returned empty action lists for every position. The provider loader used reflection with a parameterless-public-constructor filter, which filtered out every MEF-exported `[ImportingConstructor]` provider. Replaced with `System.Composition` MEF discovery across the Roslyn Workspaces + Features assemblies (80+ refactoring providers + 15 fix providers now load); the workspace's `MefHostServices` is wired to the same assembly set so the providers' runtime language-service lookups (e.g. `IExtractMethodService`) resolve
- `McpServer` had a private `ParseStringArray` helper with the same non-string-element bug as `JsonRpcParameters.OptionalStringArray`; helper deleted and `sync_documents.filePaths` routed through the typed accessor
- `JsonRpcParameters.OptionalStringArray` silently dropped non-string elements from a JSON array; mixed-type arrays now produce `-32602 Invalid params`

## [1.5.2] - 2026-04-17

### Fixed
- Source generators weren't being run, so tools queried a pre-generator compilation. This caused phantom `CS0117` errors, missed references, and false unused-code results for any project using generators (#7)
- `get_source_generators` now returns actual generated file names (was empty due to broken file-path heuristic)
- `get_generated_code` now finds generator output (same root cause)

### Changed
- Added internal `GetProjectCompilationAsync` helper that runs source generators and caches the result per project. All 17 internal compilation callers migrated to use it.
- Compilation cache invalidated alongside document cache on `load_solution`, `sync_documents`, and after code-writing tools (`add_null_checks`, `generate_equality_members`).

## [1.5.1] - 2026-04-16

### Fixed
- Server sent invalid `{"id": null}` error response for `notifications/initialized`, breaking Claude Code and mcp-proxy connections (#6)
- Notifications (messages without `id`) now correctly produce no response per JSON-RPC spec

## [1.5.0] - 2026-04-08

### Added
- `find_attribute_usages` — find all types/members decorated with a specific attribute
- `get_di_registrations` — scan for DI service registrations (AddScoped, AddTransient, etc.)
- `find_reflection_usage` — detect dynamic/reflection-based usage invisible to static analysis
- `find_circular_dependencies` — detect cycles in project or namespace dependency graphs
- `get_nuget_dependencies` — list NuGet package references per project with versions
- `get_source_generators` — list active source generators and their generated output
- `get_generated_code` — view source code produced by a source generator
- 16 new tests for discovery tools

### Changed
- Tool count increased from 55 to 62
- Refactored RoslynService into 9 partial class files by concern (Navigation, Analysis, Refactoring, TypeDiscovery, Compound, CodeActions, CodeGeneration, Discovery)

## [1.4.2] - 2026-04-08

### Changed
- Split release workflow into separate jobs (build-and-test, publish-nuget, publish-npm, publish-mcp-registry) for reliability and proper dependency ordering

## [1.4.1] - 2026-04-08

### Added
- MCP Registry publishing — listed on registry.modelcontextprotocol.io
- server.json metadata for registry discovery
- Verification strings for NuGet and npm packages

## [1.4.0] - 2026-04-04

### Fixed
- JSON-RPC `id` field now accepts string and integer per spec (was integer-only). Fixes issue reported in PR #5 where clients sending GUID/string IDs would crash.

### Changed
- Added `RequestId` struct with proper `IEquatable`, JSON converter, and spec-compliant validation (string or integer only)
- Extracted `ErrorCodes`, `RoslynError`, `ResponseMetadata`, `SignatureChange` into own files (were inline in RoslynService.cs)
- Server version in initialize response now reads from assembly instead of hardcoded "1.0.0"
- Protocol version and log levels are now constants, log level read once at startup
- Replaced obsolete `WorkspaceFailed` event with `RegisterWorkspaceFailedHandler`
- Fixed all nullable reference warnings (proper null checks instead of suppression)
- Added `InternalsVisibleTo` for test project, added McpServer integration tests
- npm launcher now pins .NET tool version to match npm package version, preventing version drift
- npm launcher uses `dotnet tool update` for install/upgrade in one step
- Added `preuninstall` script for global npm installs to clean up .NET tool

## [1.3.2] - 2026-03-25

### Fixed
- Added README for npm package (was missing in v1.3.1)
- Added npm badge to main README

## [1.3.1] - 2026-03-25

### Added
- npm wrapper package — install via `npx -y sharplens-mcp` for standard MCP server config
- Release workflow now publishes to both NuGet and npm

## [1.3.0] - 2026-03-25

### Fixed
- MSBuild locator fails with "No instances of MSBuild could be detected" when only .NET 9/10+ SDK is installed (GitHub issue #3)

### Changed
- Added `RollForward=LatestMajor` so the tool runs on any .NET 8+ runtime
- Upgraded Microsoft.Build.Locator from 1.6.10 to 1.11.2 for cross-SDK compatibility
- Improved error message when no compatible SDK is found

## [1.2.3] - 2025-12-28

### Fixed
- Removed erroneous `TryApplyChanges` call in `sync_documents` that caused Roslyn to add `<Compile Include>` entries to SDK-style .csproj files (known Roslyn issue #36781)

## [1.2.2] - 2025-12-28

### Added
- `sync_documents` infrastructure tool - Synchronize file changes from disk into the loaded solution
  - Faster than `load_solution` - only updates changed documents, doesn't re-parse projects
  - Handles modified files, new files, and deleted files
  - Supports specific file list or sync all documents
- Agent Responsibility documentation in README explaining sync workflow

### Changed
- Agents are now responsible for calling `sync_documents` after using Edit/Write tools
- SharpLensMcp refactoring tools continue to auto-update (no sync needed)

## [1.2.1] - 2025-12-27

### Added
- Relative paths by default - saves tokens in AI agent context (configurable via `SHARPLENS_ABSOLUTE_PATHS=true`)
- Cross-platform path normalization (forward slashes on all platforms)
- Platform-aware file path comparison (case-insensitive on Windows, case-sensitive on Linux/macOS)

### Changed
- All file paths now return relative to solution directory by default
- Paths use forward slashes consistently across Windows/Linux/macOS
- File lookups now match file system behavior on each platform

## [1.2.0] - 2025-12-27

### Added
- `get_method_source_batch` - Get source code for multiple methods in one call (reduces round trips when tracing code flows)
- `includeOutgoingCalls` parameter for `analyze_method` - Now returns what methods a method calls, not just who calls it
- AI Agent Configuration Tips section in README

### Changed
- Tool count increased from 57 to 58
- `analyze_method` now supports comprehensive method analysis in a single call (signature + callers + outgoing calls)

## [1.1.1] - 2025-12-27

### Changed
- Added Claude Code setup guide with step-by-step instructions
- Added configuration reference for all environment variables
- Added comparison table: SharpLensMcp vs native LSP capabilities

## [1.1.0] - 2025-12-27

### Added
- 10 new tools (47 → 57 total):
  - `get_code_actions_at_position` - List all available refactorings at position
  - `apply_code_action_by_title` - Apply any Roslyn code action by title
  - `implement_missing_members` - Generate interface/abstract stubs
  - `encapsulate_field` - Convert field to property
  - `inline_variable` - Inline temporary variable
  - `extract_variable` - Extract expression to variable
  - `generate_equality_members` - Generate Equals/GetHashCode/operators
  - `add_null_checks` - Generate ArgumentNullException guards
  - `get_complexity_metrics` - Cyclomatic, nesting, LOC, cognitive metrics
  - `get_type_members_batch` - Batch lookup for multiple types

### Changed
- Improved tool descriptions with clearer USAGE/OUTPUT patterns
- README improvements with badges and tables

## [1.0.0] - 2025-12-26

### Added
- Initial release with 47 semantic analysis tools
- Navigation tools: symbol info, definitions, references, implementations
- Analysis tools: diagnostics, data flow, control flow
- Refactoring tools: rename, extract method, change signature
- Compound tools: type overview, method analysis
- Infrastructure: health check, solution loading, project structure
