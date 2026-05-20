# Changelog

## [1.5.4] - 2026-05-19

### Fixed
- `apply_code_fix` left stale entries in the document and compilation caches after applying a fix to disk. Follow-up tools — most visibly `get_diagnostics` on the same file — saw the pre-fix `Document` from the cache and reported the just-fixed diagnostic as still present. The solution swap and cache clears are now hoisted to a single post-write step, matching the contract `change_signature` already used
- `get_source_generators` reported `Microsoft.CodeAnalysis.IncrementalGeneratorWrapper` for every entry. Roslyn wraps `IIncrementalGenerator` instances in an internal wrapper when surfaced through `ISourceGenerator`, so `g.GetType().FullName` collapsed every generator to the same wrapper type and made them indistinguishable to callers. The response now unwraps via reflection on the wrapper's `Generator` property and emits the underlying generator's real `typeName` and `assemblyName`
- `get_instantiation_options` returned an empty `externalFactories` list for projects with source generators. The factory-method match compared the target type from the base compilation against return-type symbols from the augmented compilation; `SymbolEqualityComparer.Default` returns `false` across compilations even for the same source type. The target type is now re-resolved per compilation via `GetTypeByMetadataName` before the comparison
- `analyze_data_flow`, `analyze_control_flow`, and `get_file_overview` had dead `if (document == null)` branches after `GetDocumentAsync` calls. `GetDocumentAsync` throws `FileNotFoundException`, never returns null — so an unknown file path produced an unstructured rethrow instead of the documented `FileNotInSolution` error. Each call site now wraps `GetDocumentAsync` in `try/catch` and emits the structured error
- `find_god_objects` over-counted efferent coupling by traversing into types declared in referenced source projects. The project filter now restricts coupling counts to types declared in the target project, so a small project that references a large library no longer inherits the library's outgoing edges
- `McpServer.HandleToolCallAsync` had a private `ParseStringArray` helper with the same non-string-element bug as `JsonRpcParameters.OptionalStringArray`. The helper is deleted and `sync_documents.filePaths` now routes through the typed `OptionalStringArray` accessor
- `JsonRpcParameters.OptionalStringArray` silently dropped non-string elements from a JSON array (passing through whatever the elements coerced to). Mixed-type and non-string arrays now produce `-32602 Invalid params` with a distinct missing-array-parameter message

### Changed
- Test suite hardened: 310 → 543 tests. Every silent-pass null-conditional pattern (`data["X"]?.Value<T>().Should().Be(Y)` short-circuits when the field is missing or the response shape changes) was tracked down across both the direct-method and MCP-layer test suites and replaced with a `NotBeNull`-first lock or a direct cast. The audit surfaced the impl-side bugs listed above plus several MCP-layer response-shape inconsistencies that had been hidden for releases (notably `organize_usings_batch` / `format_document_batch` `fileCount` and three field-name mismatches in `find_references` / `find_implementations` / `find_overrides` assertions, all corrected end-to-end)
- `GetCommonFixSuggestions` and `GetNestedActionsOrEmpty` are now `internal` (covered by `InternalsVisibleTo` to the test project) so the per-CS-ID suggestion contract and the nested-menu descent helper can be locked by direct unit tests. Production behavior is unchanged
- Every documented branch across `RoslynService.Refactoring.cs`, `RoslynService.CodeGeneration.cs`, `RoslynService.Quality.cs`, `RoslynService.Inspection.cs`, `RoslynService.CodeActions.cs`, and `RoslynService.Discovery.cs` is now exercised by at least one test against a deterministic fixture. New AdhocWorkspace tests cover the framework-interface and framework-attribute skips in `find_unused_code`, the type-level branch of `find_attribute_usages`, the `Activator` and `Delegate.DynamicInvoke` branches of `find_reflection_usage`, the actual registration detection of `get_di_registrations`, the cyclomatic-complexity formula (1 + branches across `if`/`while`/`for`/`foreach`/`case`/conditional/`catch`) and descending-sort contract of `find_untested_code`, and the `Constructor` branch of `IsCandidate`. New snapshot/restore disk-mutation tests cover `format_document_batch`, `apply_code_fix` (against a real compiler-emitted `CS0219`), `add_null_checks`, `generate_equality_members`, and `organize_usings_batch` apply paths

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
- Test suite hardened: 127 → 310 tests, zero dead, zero vacuous, zero smoke. 25 tests had been reading `data["symbols"]` (actual field is `data["results"]`) so their bodies never ran; 18 more had no assertions; the entire MCP-layer suite was content-locked tool-by-tool against deterministic fixtures (no `(success or error)` behavior-contract pattern remains). `TESTING.md` now forbids the smoke-contract pattern outright. New fixtures cover every cursor-fragile refactoring tool and the extract_method apply path. Separate `SharpLensMcp.Tests.TestAnalyzers` netstandard2.0 project hosts a real `DiagnosticAnalyzer` for end-to-end analyzer integration
- Every tool has an end-to-end MCP-layer integration test in addition to its direct-method test. The `tests/Mcp/` suite exercises every `tools/call` through `McpServer.HandleRequestAsync`, locking down specific known content per tool — exact error codes (`ErrorCodes.*`), exact response field names, exact reference counts where deterministic. Shared `McpServerFixture` (xUnit `ICollectionFixture`) loads the solution once via `tools/call roslyn:load_solution` and is reused across every MCP test class
- `roslyn:health_check` response now wraps its payload in the standard `{success, data, meta}` envelope used by every other tool. Previously returned a flat object with `status`/`solution`/etc. at the top level, the only tool to bypass `CreateSuccessResponse`. Callers must now read `data.status` instead of `status`
- `roslyn:generate_constructor` response now carries `appliesEditsAutomatically: false` to make its generation-only semantics explicit. The tool returns `constructorCode` for the caller to insert via `Edit`/`Write` — it does not mutate the workspace itself

### Fixed
- Records misclassified as `Class`. `typeKind` output and `kind:` filter now report `Record` / `RecordStruct` correctly (11 sites). New `GetTypeKindString` helper
- `find_references.kind` was hardcoded to `"read"`. Now reflects actual usage; write detection covers assignment LHS, `++`/`--`, and `out`/`ref` args
- `change_signature` returned `applied: true` while editing nothing, and only matched `MethodDeclarationSyntax`. Now applies via `SolutionEditor` to the declaration plus every call site (`Foo(...)`, `new Foo(...)`, `: this(...)`, `: base(...)`, `new(...)`); supports methods, constructors, and local functions; updates named-argument labels on rename
- `extract_method` claimed `preview: false` would apply edits but never did. Now fully implemented via `SolutionEditor`: inserts the extracted method as a sibling member after the source method, replaces the selected statements with the call expression, commits via `_workspace.TryApplyChanges`, and clears caches. Response shape: `preview: true` returns `extractedCode`/`replacementCode` without modifying disk; `preview: false` returns `applied: true` plus `filesModified`. Snapshot/restore test fixture in `ExtractMethodApplyFixture.cs`
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
- `HandleRequestAsync` returned an error response (id: null) for any exception during dispatch, including exceptions that escaped a notification handler. Per JSON-RPC 2.0 §4.3 notifications must produce no response even on internal error. Outer catch now distinguishes parse-failure (respond), request-with-exception (respond with the id), and notification-with-exception (no response)
- `InfrastructureTests.DependencyGraph_ReturnsMermaidFormat` asserted on a non-existent `data["mermaid"]` field via `?.Value<string>()` short-circuit, so the test silently passed forever. Repaired to read the actual `data["graph"]` field and assert it starts with `"graph TD"`. Same null-conditional short-circuit pattern surfaced and fixed in `AnalysisTests.GetMethodSource_ReturnsSourceCode` (was reading `data["source"]`; actual field is `data["fullSource"]`)
- `get_code_actions_at_position` and `apply_code_action_by_title` returned empty action lists for every position. The provider loader at `Analysis.cs:GetBuiltInCodeRefactoringProviders` used reflection with a parameterless-public-constructor filter — Roslyn's refactoring providers are MEF-exported with `[ImportingConstructor]` constructors that take service injections, so every one of them was filtered out. Replaced with `System.Composition` MEF discovery from the Roslyn Workspaces + Features assembly set (80+ refactoring providers + 15 fix providers now load). The MSBuildWorkspace is now constructed with `MefHostServices.Create(GetRoslynMefAssemblies())` so the language services those providers depend on at runtime (e.g. `IExtractMethodService<TStatementSyntax>`) are wired up — without that half, providers fire but their language-service lookups return null and they emit no actions
- Added `Microsoft.CodeAnalysis.CSharp.Features` (5.0.0) package reference. The previously-referenced `Microsoft.CodeAnalysis.Features` package ships only language-neutral refactorings (ExtractInterface, IntroduceVariable); C#-specific refactorings (`CSharpInlineTemporaryCodeRefactoringProvider`, `CSharpUseExpressionBodyCodeRefactoringProvider`, `CSharpInvertIfCodeRefactoringProvider`, ~60 more) live in the separate `.CSharp.Features` package

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
