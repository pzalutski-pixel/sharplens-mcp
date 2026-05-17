# Changelog

## [1.5.3] - 2026-05-17

### Added
- `get_external_type_info` — inspect NuGet/BCL/external-assembly types: members, signatures, XML doc summaries via `GetTypeByMetadataName` + `GetDocumentationCommentXml`
- `get_call_graph` — multi-hop transitive callers/callees with mandatory `maxDepth` (1-10), `maxNodes` cap, and cycle detection. Returns nodes + edges (not a tree) so diamond graphs don't explode
- `find_untested_code` — public/internal surface not transitively reached by any xUnit (`[Fact]`/`[Theory]`), NUnit (`[Test]`/`[TestCase]`), or MSTest (`[TestMethod]`) method. Sorted by cyclomatic complexity
- `find_god_objects` — over-coupled types via efferent + afferent coupling + member-count thresholds. Audit-time scoring; O(types × references)
- `get_project_health` — composite audit dashboard: diagnostics + unused + coupling + coverage per project in one call

### Changed
- Tool count: 62 → 67
- `get_diagnostics` now runs configured `DiagnosticAnalyzer`s by default (StyleCop, Roslynator, NetAnalyzers, custom; editorconfig-resolved severities). Response includes `analyzersRan` and `analyzerCount`. Set `runAnalyzers: false` for compiler-only output
- `find_references` reports `cast` kind in addition to `read`/`write`/`invocation`/`typeof`/`nameof`/`attribute`. Adds optional `kind` filter for server-side filtering with `totalReferences` + `totalReferencesAfterFilter`
- `McpServer.HandleToolCallAsync` dispatcher refactored: 370-line switch with ~80 inline `arguments?["x"]?.GetValue<T>() ?? throw new Exception("x required")` blocks → typed `JsonRpcParameters` helper. Bad params now produce `-32602 Invalid params` per JSON-RPC spec
- `RoslynService.Refactoring.cs` split into 3 partial files by concern: `Refactoring.cs`, `Inspection.cs`, `Validation.cs`. Pure code movement, no behavior change
- All `dynamic` dispatch eliminated from `src/`. 13 sites across 4 files replaced with 3 centralized reflection helpers (`IsSuccessResponse`/`GetResponseData`/`GetResponseError`) and typed records
- `RoslynService._workspace` widened to base `Workspace?` + internal `LoadFromWorkspaceForTesting` seam. Enables in-memory `AdhocWorkspace`-based tests (~5ms) for tools that don't need cross-file references. Production path unchanged
- Test suite hardened: 127 → 288 tests, zero dead, zero vacuous. 25 tests had been reading `data["symbols"]` (actual field is `data["results"]`) so their bodies never ran; 18 more had no assertions. All converted to fixture-driven semantic-grade assertions. New `tests/Fixtures/`, `SemanticAssertions` helper, `TESTING.md` contract, separate `SharpLensMcp.Tests.TestAnalyzers` netstandard2.0 project hosting a real `DiagnosticAnalyzer` for end-to-end analyzer integration
- Every tool now has an end-to-end MCP-layer integration test in addition to its direct-method test. New `tests/Mcp/` suite (93 tests) exercises every `tools/call` through `McpServer.HandleRequestAsync`, asserting on the full JSON-RPC envelope: `id` round-trip, `result.content[0].text` shape, parameter dispatch through `JsonRpcParameters`, `-32602 Invalid params` error mapping, and the inner tool response shape. Shared `McpServerFixture` (xUnit `ICollectionFixture`) loads the solution once via `tools/call roslyn:load_solution` and is reused across every MCP test class
- `roslyn:health_check` response now wraps its payload in the standard `{success, data, meta}` envelope used by every other tool. Previously returned a flat object with `status`/`solution`/etc. at the top level, the only tool to bypass `CreateSuccessResponse`. Callers must now read `data.status` instead of `status`

### Fixed
- Records misclassified as `Class`. `typeKind` output and `kind:` filter now report `Record` / `RecordStruct` correctly (11 sites). New `GetTypeKindString` helper
- `find_references.kind` was hardcoded to `"read"`. Now reflects actual usage; write detection covers assignment LHS, `++`/`--`, and `out`/`ref` args
- `change_signature` returned `applied: true` while editing nothing, and only matched `MethodDeclarationSyntax`. Now applies via `SolutionEditor` to the declaration plus every call site (`Foo(...)`, `new Foo(...)`, `: this(...)`, `: base(...)`, `new(...)`); supports methods, constructors, and local functions; updates named-argument labels on rename
- `extract_method` claimed `preview: false` would apply edits but never did. Response renamed to `appliesEditsAutomatically: false`; suggested next-steps direct the caller to apply manually via `Edit`/`Write`
- `rename_symbol` accepted invalid C# identifiers like `123invalid`. Now returns `INVALID_PARAMETER`
- JSON-RPC notification handling was non-compliant: only `method.StartsWith("notifications/")` was treated as a notification. Per spec §4.3, *any* request without `id` is a notification — `{"jsonrpc":"2.0","method":"initialize"}` was incorrectly answered with `{"id":null,...}`. Fixed
- `find_attribute_usages` and `find_reflection_usage` lied about `totalCount` when `maxResults` was hit. Both now track an unbounded counter
- `search_symbols.totalCount` was capped at `maxResults + 100`. Buffer removed; paginated callers now see the true match count
- `AnalyzeDataFlow` / `AnalyzeControlFlow` threw "statements not within the same statement list" on regions spanning a `BlockSyntax`. Fixed with sibling-aware resolution that walks to the enclosing block
- Relative paths from `FormatPath` didn't round-trip through `GetDocumentAsync`. Now resolves solution-relative paths against the solution directory
- `FindTypeByName` returned symbols `SymbolFinder` didn't recognize on generator-using projects. Now prefers the base compilation; falls back to the augmented one only for purely source-generated types
- `find_untested_code` matched test attributes by simple name, so user-defined `MyApp.Attributes.FactAttribute` was treated as xUnit's `[Fact]`. Now matches `(namespace, simple name)` tuples
- `find_untested_code` ignored the `includeProperties` parameter. Now honored across the BFS and candidate-collection passes
- `find_god_objects` over-counted efferent coupling via a loose `IdentifierNameSyntax` pass that included `nameof()` args, using-directive types, and generic constraints. Tightened to object-creation, typeof, cast, var-decl, and invocation receiver only
- `get_call_graph` cycle detection used dynamic dispatch on anonymous-typed edges. Replaced with typed `Dictionary<int, List<int>>` adjacency kept in sync by the new `AddEdge` helper

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
