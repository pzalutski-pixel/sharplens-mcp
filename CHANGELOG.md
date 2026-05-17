# Changelog

## [Unreleased]

### Added
- **`get_external_type_info`** — inspect types from NuGet packages, BCL, and closed-source assemblies the solution references. Returns members, signatures, and XML doc summaries via `GetTypeByMetadataName` + `GetDocumentationCommentXml`. Eliminates the AI-hallucination class against dependency APIs.
- **`get_call_graph`** — multi-hop transitive call graph (callers, callees, or both) with mandatory `maxDepth` bound (1-10), `maxNodes` cap, and cycle detection. Returns nodes + edges (not a nested tree) so diamond-shaped graphs don't explode. Replaces the N-round-trip pattern of iterative `find_callers`/`get_outgoing_calls` calls.
- **`find_untested_code`** — public/internal surface not transitively reachable from any test method. Detects xUnit (`[Fact]`/`[Theory]`), NUnit (`[Test]`/`[TestCase]`), and MSTest (`[TestMethod]`) attributes; BFS the call graph from tests; sorts uncovered items by cyclomatic complexity so the riskiest gaps come first.
- **`find_god_objects`** — over-coupled types via efferent (outbound) + afferent (inbound) coupling + member-count thresholds, scored and ranked. Audit-time tool; O(types × references).
- **`get_project_health`** — composite audit dashboard for a single project: aggregates diagnostics (with analyzers), unused-code, god-object candidates, and untested-surface counts plus top-N hotspots per dimension. One call replaces 4+ separate tool calls.

### Changed
- **`find_references` reports the real reference `kind`.** Now distinguishes `read`, `write`, `invocation`, `cast`, `typeof`, `nameof`, `attribute`. Adds an optional `kind` filter parameter so callers can request only writes / only invocations server-side, with response fields `totalReferences` (unfiltered) and `totalReferencesAfterFilter` for clarity.
- **`get_diagnostics` is now analyzer-aware by default.** Runs configured `DiagnosticAnalyzer`s (StyleCop, Roslynator, NetAnalyzers, custom analyzers; editorconfig-resolved severities) in addition to compiler diagnostics. Response includes `analyzersRan` and `analyzerCount` fields. Set `runAnalyzers: false` for the old compiler-only behavior when speed matters; default `true` matches what CI will fail on.
- **Test suite hardened: 127 → 195 tests, zero dead, zero vacuous.** 25 tests across `NavigationTests`, `AnalysisTests`, `RefactoringTests`, `CodeActionTests`, `CustomToolTests` were reading `data["symbols"]` (the actual field is `data["results"]`) and gated on `if (symbols?.Count > 0)`, so their bodies never ran. 18 more had no assertions or explicit `// Just verify no crash` comments. All converted to fixture-driven semantic-grade assertions. New suites: `ChangeSignatureTests` (8), `JsonRpcParametersTests` (14), `GetMissingMembersTests` (2), `CallGraphTests` (7), `GodObjectTests` (4), `UntestedCodeTests` (1), `UntestedCodeFrameworkTests` (6+4), `ProjectHealthTests` (4), `ExternalApiTests` (5), `FindReferencesKindTests` (3), `AnalyzerDiagnosticsTests` (2).
- **New test infrastructure.** `tests/SharpLensMcp.Tests/Fixtures/` directory with `RecordFixture`, `InterfaceHierarchyFixture`, `RefactoringFixture`, `ReferenceKindsFixture`, `ChangeSignatureFixture`, `CallGraphFixture`, `GodObjectFixture`, `TestCoverageFixture`. New `SemanticAssertions` helper. New `TESTING.md` documenting the tool-test contract and the two test styles (solution-loaded vs in-memory). New `tests/SharpLensMcp.Tests.TestAnalyzers/` netstandard2.0 project hosting a real `DiagnosticAnalyzer` (`AlwaysFiresAnalyzer`) so the analyzer-integration tests exercise the actual `WithAnalyzers().GetAllDiagnosticsAsync()` pipeline.
- **`RoslynService._workspace` field type widened to base `Workspace?`** (was `MSBuildWorkspace?`), plus new internal `LoadFromWorkspaceForTesting(Workspace)` seam. Enables in-memory `AdhocWorkspace`-based tests for tools that operate on incomplete code (`get_missing_members`'s populated-list path now tested in `GetMissingMembersTests`), and opens the door for fast (~5ms) hermetic tests of any tool that doesn't need cross-file references. Production code path (`LoadSolutionAsync` → `MSBuildWorkspace`) unchanged.
- **`McpServer.HandleToolCallAsync` dispatcher refactored.** 370-line switch with ~80 inline `arguments?["x"]?.GetValue<T>() ?? throw new Exception("x required")` blocks replaced with a typed `JsonRpcParameters` helper. Missing/wrong-type arguments now produce `-32602 Invalid params` per JSON-RPC spec instead of a bare `Exception`.
- **`RoslynService.Refactoring.cs` split into 3 partial files** (2912 → 1167 + 1772 + 268 lines after subsequent feature work). New `RoslynService.Inspection.cs` holds the read-only analysis tools that were misfiled under "Refactoring" — `find_callers`, `find_unused_code`, `get_outgoing_calls`, `get_method_overloads`, `get_containing_member`, `get_dependency_graph`, `get_missing_members`, `get_instantiation_options`, `analyze_change_impact`, `get_method_source`, `get_method_source_batch`, and (added later) `get_call_graph`. New `RoslynService.Validation.cs` holds `validate_code` and `check_type_compatibility`. Pure code movement at split time; later additions accumulated into `Inspection.cs`.
- **All `dynamic` usage eliminated from `src/`.** 13 sites across 4 files (`CodeActions.cs`, `Inspection.cs`, `TypeDiscovery.cs`, `Refactoring.cs`) replaced with three centralized reflection helpers (`IsSuccessResponse`, `GetResponseData`, `GetResponseError` in `RoslynService.cs`) plus typed records (`QualityAuditData.cs`, `ConstructorMember.cs`). Late-bound member access through anonymous-typed responses no longer scattered through the codebase.

### Fixed
- **Records misclassified as `Class`.** `typeKind` output and `kind:` filter now correctly report `record class` as `"Record"` and `record struct` as `"RecordStruct"`. Affected 11 sites across `get_symbol_info`, `get_type_overview`, `get_type_members`, `get_base_types`, `find_implementations`, `semantic_query`, and `search_symbols`. New `GetTypeKindString` helper in `RoslynService.cs`.
- **`find_references.kind` was hardcoded to `"read"`.** Now distinguishes `read`, `write`, `invocation`, `cast`, `typeof`, `nameof`, and `attribute` based on the syntax context. Write detection covers assignment LHS, `++`/`--`, and `out`/`ref` arguments.
- **`change_signature` was a no-op that lied.** With `preview: false` it returned `applied: true` while editing nothing, and only worked on `MethodDeclarationSyntax`. Now fully implemented via `SolutionEditor`: rewrites the declaration plus every call site (`Foo(...)`, `new Foo(...)`, `: this(...)`, `: base(...)`, `new(...)`), updates named-argument labels on rename, and supports methods, constructors, and local functions.
- **`extract_method` claimed `preview: false` would apply the refactoring** but the implementation never modified the workspace. Response now exposes `appliesEditsAutomatically: false` and the suggested next-steps direct the caller to apply the generated `extractedCode` + `replacementCode` via `Edit`/`Write` themselves.
- **`rename_symbol` accepted invalid C# identifiers.** Names like `123invalid` now return `INVALID_PARAMETER` instead of attempting the rename.
- **JSON-RPC notification handling was non-compliant.** Per spec, *any* request without an `id` member is a notification and must produce no response. Previously only `method.StartsWith("notifications/")` was treated as a notification — `{"jsonrpc":"2.0","method":"initialize"}` was incorrectly answered with `{"id":null,...}`. Notifications now route through the method switch (so any side-effecting handler runs) with the response discarded at the wire layer.
- **`totalCount` lied in two discovery tools.** `find_attribute_usages` and `find_reflection_usage` previously set `totalCount` to the truncated list size, hiding when `maxResults` was hit. Both now track an unbounded `totalFound` counter (matching the `get_attributes` pattern).
- **`search_symbols.totalCount` was capped at `maxResults + 100`.** Removed the `+100` buffer so paginated callers see the true match count.
- **`AnalyzeDataFlow` / `AnalyzeControlFlow` threw "statements not within the same statement list"** when given a region spanning a `BlockSyntax`. Replaced `DescendantNodesAndSelf().OfType<StatementSyntax>()` with sibling-aware resolution that walks to the enclosing block.
- **Tool callers that returned relative paths couldn't pass them back in.** `GetDocumentAsync` (and the parallel `TryFindDocument`) now resolve solution-relative paths against the solution directory, so paths from `FormatPath` round-trip without manual absolutization.
- **`FindTypeByName` returned symbols `SymbolFinder` didn't recognize.** When a project used source generators, the helper returned a symbol from our augmented compilation, which `FindDerivedClassesAsync` and friends rejected. Now prefers the project's base compilation for symbol lookup and only falls back to the augmented one for purely source-generated types.
- **`find_untested_code` matched test attributes by simple name only**, so a user-defined `MyApp.Attributes.FactAttribute` was treated as xUnit's `[Fact]` — inflating the reachable set and hiding genuine coverage gaps. Detection now matches on `(namespace, simple name)` tuples drawn from a fixed set of known test-framework attributes.
- **`find_untested_code` ignored the `includeProperties` parameter** when collecting uncovered surface — properties were always excluded. Now honored consistently across the BFS and candidate-collection passes.
- **`find_god_objects` over-counted efferent coupling** by treating every `IdentifierNameSyntax` that resolved to a type as a dependency, which inflated counts with `nameof()` arguments, using-directive-brought-into-scope types, and generic constraint references. Tightened to a precise pass that only counts `ObjectCreation`, `ImplicitObjectCreation`, `TypeOf`, `CastExpression`, `VariableDeclaration`, and the receiver of `InvocationExpressionSyntax`.
- **`get_call_graph` cycle detection used dynamic dispatch on anonymous-typed edge records** for the ancestor-reachability check (`IsAncestor`). Replaced with a typed `Dictionary<int, List<int>>` adjacency map kept in sync by the new `AddEdge` helper — O(V+E) BFS, no boxing, no late-bound dispatch.

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
