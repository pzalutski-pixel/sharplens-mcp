# Changelog

## [1.6.4] - 2026-10-17

### Added
- Multi-targeted projects are one project. A project built for several target frameworks is reported once by every tool, with a `targetFrameworks` list where a result exists only under some of them; `projectName` accepts the plain name (`Multi`) for all frameworks or a framework-qualified name (`Multi(net8.0)`) for one. Diagnostics and post-apply error checks cover every framework.
- `find_tests` entries carry `kind` (`test`, `stepBinding`, `hook`) and a `dispatchOnlyTests` list; `find_untested_code` reports `entryPointCounts` and labels members reached only through interface or virtual dispatch instead of counting them as tested.
- By-name tools return `notice` when a simple type name resolved to a solution type that shadows a framework type of the same name.
- Call tools mark each edge with `kind` and `viaDispatch`.
- `load_solution` and `health_check` report `loadWarnings`; `health_check` adds `lastLoadFailure` and the effective configuration. `.slnf` solution filters load.
- Apply tools refuse to overwrite a file that changed on disk since it was read (`STALE_DOCUMENT`) or to write a file whose bytes its encoding cannot reproduce (`UNSUPPORTED_ENCODING`, e.g. a BOM-less windows-1252 file on Linux), and the MCP `ping` request is answered.

### Changed
- One call model behind `get_outgoing_calls`, `get_call_graph`, `find_callers`, `find_path_between`, `get_exception_flow`, `analyze_method` and the coverage tools: extension calls, constructors and initializers, accessors, indexers, operators, conversions, partial implementations, lambdas and local functions are all followed, and interface/virtual calls reach the implementations the receiver's static type can run.
- Test entry points are detected the way each framework discovers them — xUnit v2/v3, NUnit, MSTest, TUnit, Reqnroll and SpecFlow, including derived attributes such as `[SkippableFact]`, setup/teardown, test-class constructors, fixtures and step bindings.
- Type names resolve in every documented spelling: dotted nested types, generic display names, keyword aliases and framework simple names (`HttpClient`, `IOException`). A partial name returns suggestions instead of picking a type, and `overloadIndex` means the same overload in every tool.
- Results come back in a deterministic order, so capped and paged results are reproducible. Limits and enumerated parameters are validated (`INVALID_PARAMETER`), and malformed array arguments are rejected as invalid params instead of being treated as empty.
- Member listings exclude accessors and compiler-generated members and use one member-kind vocabulary.
- Package, target-framework and compile-item facts come from MSBuild evaluation (`get_nuget_dependencies`, `find_unused_dependencies`, `get_project_structure`, the `diff_api_surface` baseline).
- `find_naming_violations` reproduces the compiler's IDE1006 verdicts. `find_path_between` counts hops in edges, `get_call_graph` direction `both` is the callers tree plus the callees tree, and `dependency_graph` returns `nodes`/`edges`.

### Fixed
- `find_tests` and `find_untested_code` no longer miss tests in another project when that project's package versions or target framework differ from the code under test; `get_call_graph`, `get_outgoing_calls`, `find_god_objects` and `check_architecture` had the same cross-project identity defect.
- `find_tests` binds each method body once per call and stops a test's walk once it reaches the target, instead of re-analyzing shared code for every test.
- Apply and sync: an apply no longer rewrites other files with stale content after `sync_documents`, keeps each file's encoding, BOM and line endings, never adds a duplicate `<Compile>` item to a project file, writes only the documents an edit changed, and reports minimal change spans. `sync_documents` detects real changes, adds new files the project includes, and rejects files no project compiles.
- Edit tools produce code that compiles and keeps its meaning: `change_signature` (parameter renames, attributes, defaults, optional and `params` arguments, method groups, overrides and implementations, recursion), `rename_symbol` conflicts in the preview, `extract_method` (static context, async loops, written parameters, control flow), `add_null_checks`, `generate_equality_members`, `generate_constructor`, `extract_interface`, `generate_test_stub`, `move_type_to_file`, `split_type`, `remove_unused_code`, `organize_usings`, `add_missing_imports`, `encapsulate_field`, `extract_variable`, `fix_all`.
- `load_solution`: MSBuild warnings no longer mark a complete load as partial; restore failures, projects that were never restored and projects that loaded without references do; shared projects are not counted as dropped; a failed load leaves the previous solution usable; startup auto-load no longer blocks `initialize`; non-ASCII input is read as UTF-8; file and project paths written with `\` resolve on Linux and macOS too.
- Analysis tools: `get_diagnostics` project paths and DiagnosticSuppressors, code fixes for analyzer diagnostics and at the requested position, complexity metrics, async-issue coverage, dead branches after `return`, similar-code fingerprints, `validate_code` contexts, `diff_api_surface` baselines, stack-trace frames (lambdas, iterators, nested types, overloads, Mono), pattern and catch searches, reference kinds, type hierarchies with interfaces, missing members, containing members, documentation inheritance, attribute targets, DI registrations, reflection usage, namespace and project cycles, and `find_unused_code` entry points.

## [1.6.3] - 2026-08-16

### Fixed
- `load_solution` no longer silently drops projects it can't load. When MSBuild can't resolve a `ProjectReference` (or a project fails to load for any reason), the response now reports `partial: true`, `declaredProjectCount` (from the solution file) alongside the loaded count, and `loadFailures` naming what failed — so a partial load isn't mistaken for a clean one (`success: true` with a silently smaller `projectCount`). `health_check` exposes the same fidelity without a reload. `get_diagnostics` gains `analysisMode: "live-compilation"` to signal its counts are Roslyn's live diagnostics, which can differ from a full `dotnet build`.

## [1.6.2] - 2026-08-08

### Added
- `find_path_between` — point-to-point reachability over the call graph: can any source method reach any target method, and through what call chain? Fills the gap between `get_call_graph` (single-source) and `find_callers`/`get_outgoing_calls` (one hop). A bounded bidirectional search that follows virtual/interface dispatch, with optional `avoidThrough` barriers and a single `mustPassThrough` checkpoint. An unreachable pair reports whether the search hit its bounds (raise `maxHops`) or the graph is genuinely disconnected. A neutral mechanism — the caller names the endpoints and any barriers; it has no built-in notion of what matters.

## [1.6.1] - 2026-08-05

### Fixed
- `get_missing_members` no longer reports an inherited abstract member as missing when an intermediate abstract base already overrides it. Satisfaction is resolved by walking the base chain — the most-derived member occupying each abstract slot decides — so a concrete type that compiles (e.g. `Square : Polygon : Shape` where `Polygon` overrides `Shape`'s abstract member) reports nothing missing. Abstract properties and events are resolved the same way, and a re-abstracted (`abstract override`) slot is reported once, attributed to the base that declared it.
- `analyze_data_flow` and `analyze_control_flow` return a structured `INVALID_PARAMETER` error for a line range that is out of range or inverted (`startLine` past end-of-file, or `startLine > endLine`), instead of an internal `-32603`. The shared range guard also covers `extract_method`, and the code-action tools reject a selection whose end position precedes its start.

## [1.6.0] - 2026-06-14

### Added
Tool count: 67 → 91. 24 new tools:

- `move_type_to_file` — compute the file split that moves a type into its own correctly-named file
- `split_type` — compute the partial-class split that moves named members into a new file
- `remove_unused_code` — bulk cleanup for `find_unused_code` results, with the using directives the removals make unnecessary
- `add_missing_imports` — the `using` directives that resolve a file's CS0246/CS0103, compiler-accurate
- `fix_all` — apply a code fix to every instance of a diagnostic id across a file, project, or solution
- `generate_test_stub` — a compilable test skeleton for a method, framework detected from the test projects
- `diff_api_surface` — public-API breaking-change report against a git ref
- `get_exception_flow` — which exception types can escape a method (transitively) and where each is caught
- `find_similar_code` — methods structurally similar to a query method or snippet (anti-duplication)
- `resolve_stack_trace` — map a .NET stack trace to file/line/symbol with mangling undone
- `check_architecture` — enforce dependency rules over the real type-dependency graph
- `find_unused_dependencies` — PackageReference/ProjectReference entries the compiler never needs
- `find_dead_branches` — unreachable basic blocks per method from the real control-flow graph
- `find_naming_violations` — naming-convention audit honoring `.editorconfig`, with conventional defaults
- `find_async_issues` — async-hygiene audit: `async void`, blocking on async, unforwarded `CancellationToken`
- `get_super_method` — navigate from an override/implementation to what it overrides or implements
- `get_extension_methods` — extension members applicable to a type, classic and C# 14 `extension` blocks
- `get_documentation` — full XML docs for a symbol with `<inheritdoc>` resolved recursively
- `find_interceptors` — surface `[InterceptsLocation]` call rerouting, generated code included
- `find_tests` — which tests cover a symbol (the inverse of `find_untested_code`)
- `find_type_instantiations` — every `new T(...)` / target-typed `new(...)` construction site
- `find_pattern_usages` — `is`/`as`/declaration/type/recursive patterns against a type
- `find_throw_sites` — `throw new T(...)` sites for an exception type (optionally including derived)
- `find_catch_blocks` — `catch (T ...)` clauses for an exception type (optionally including base types)

### Changed
- Upgraded `Microsoft.CodeAnalysis.*` to 5.3.0 with C# 14 readiness (`extension` blocks, the `field` keyword, null-conditional assignment)
- `rename_symbol`, `change_signature`, `add_null_checks`, `generate_constructor`, `generate_equality_members`, and `extract_interface` accept `typeName`(+`memberName`) name-based addressing as an alternative to zero-based file/line/column
- `search_symbols` adds `matchMode: "fuzzy"` for IDE-grade camel-hump matching (`RSvc` → `RoslynService`); glob remains the default
- `get_file_overview` lists each type's members with signatures and line ranges
- `find_references`, `find_callers`, and `find_implementations` now also report references inside generator output, flagged `inGeneratedCode`
- Tool names dropped the `roslyn:` prefix to satisfy the MCP tool-name pattern; the prefixed spellings remain accepted as aliases for one release
- Tools carry `readOnlyHint`/`destructiveHint` annotations derived from whether they mutate source
- `SHARPLENS_TIMEOUT_SECONDS` is enforced per call via a cancellation token threaded into the Roslyn calls (previously a no-op)
- The code-generation tools build syntax with `SyntaxGenerator` instead of concatenating strings

### Fixed
- `format_document_batch` default mode (`whitespace`) preserves the author's structure — null-suppression spacing, blank lines, single-line auto-properties; the whole-tree re-printer that expands minified code is opt-in via `mode: "canonical"`
- Unified, atomic apply path: a rejected `TryApplyChanges` leaves files and in-memory state untouched; `rename_symbol` and `apply_code_action_by_title` no longer leave stale state; refactoring tools no longer need a follow-up `sync_documents`
- Unexpected tool-handler exceptions return an in-band `isError` result instead of a JSON-RPC `-32603`, so the model sees the failure; `-32602`/`-32601`/`-32700` stay reserved for protocol-level faults
- `initialize` negotiates the client's `protocolVersion` from the supported set instead of answering with a hardcoded version
- `get_diagnostics` computes error/warning counts before the result cap; `find_unused_code` reports a symbol unused only at zero references and no longer skips disposable types
- `extract_method` keeps only statements from the selection's own block (no nested-block duplication) and emits an `async Task` signature when the selection awaits
- `add_null_checks` skips already-nullable-annotated and already-guarded parameters; `generate_equality_members` filters compiler backing fields and hashes all members consistently
- `check_type_compatibility` resolves both types within one compilation; `analyze_data_flow` / `analyze_control_flow` honor a sub-range instead of the whole block
- `get_method_signature` errors on an out-of-range overload index; `get_project_structure` reports the real target framework; `validate_code` maps error lines back to the submitted snippet
- `find_attribute_usages` and `find_untested_code` scan only the project's own assembly; `apply_code_action_by_title` requires an exact or unique title match; ambiguous `typeName` resolution returns a structured `AMBIGUOUS_TYPE` error listing candidates

## [1.5.3] - 2026-05-19

### Added
- `get_external_type_info` — inspect NuGet/BCL/external-assembly types: members, signatures, XML doc summaries
- `get_call_graph` — multi-hop callers/callees with depth/node caps and cycle detection
- `find_untested_code` — public surface not reached by any xUnit/NUnit/MSTest test method
- `find_god_objects` — over-coupled types via efferent + afferent coupling and member count
- `get_project_health` — composite audit dashboard: diagnostics + unused + coupling + coverage

### Changed
- Tool count: 62 → 67
- `get_diagnostics` runs configured `DiagnosticAnalyzer`s by default (StyleCop, Roslynator, NetAnalyzers, custom). Pass `runAnalyzers: false` for compiler-only output
- `find_references` reports a `cast` kind and accepts an optional `kind` filter
- `health_check` response now uses the standard `{success, data, meta}` envelope. Callers must read `data.status` instead of `status`
- `generate_constructor` response carries `appliesEditsAutomatically: false` to make its generation-only semantics explicit
- Bad JSON-RPC params return `-32602 Invalid params` (per spec), not a generic error
- Added `Microsoft.CodeAnalysis.CSharp.Features` so C#-specific refactorings load (inline temporary, expression-body, invert-if, ~60 more)
- Test count: 127 → 543; every tool has an end-to-end MCP-layer integration test against a deterministic fixture

### Fixed
- `change_signature` no longer reports `applied: true` while editing nothing; it now updates the declaration and every call site, including constructors, local functions, and named arguments
- `extract_method` actually applies edits on `preview: false`
- `apply_code_fix` post-apply `get_diagnostics` no longer reports the just-fixed diagnostic as still present (stale-cache bug)
- `generate_equality_members`, `organize_usings_batch`, and `format_document_batch` now invalidate the compilation cache after writing to disk; subsequent compilation reads served pre-edit content otherwise
- `get_code_actions_at_position` and `apply_code_action_by_title` return real Roslyn refactorings instead of empty lists
- `get_source_generators` surfaces the underlying generator type instead of `IncrementalGeneratorWrapper`
- `get_instantiation_options` returns external factories on projects with source generators
- `analyze_data_flow`, `analyze_control_flow`, and `get_file_overview` return a structured `FileNotInSolution` error for unknown paths instead of an unstructured rethrow
- `analyze_data_flow` / `analyze_control_flow` work on regions that span block boundaries
- `find_attribute_usages` and `find_reflection_usage` report accurate `totalCount` when `maxResults` is hit
- `search_symbols` pagination reports the true match count (no `+100` cap)
- `find_god_objects` no longer over-counts efferent coupling via `nameof()`, using-directives, or types from referenced projects
- `find_untested_code` matches test attributes by `(namespace, name)` so look-alikes in user namespaces aren't treated as test methods; honors `includeProperties`
- `find_references.kind` reports actual usage (read/write/invocation/typeof/nameof/attribute/cast), not hardcoded `read`
- Records report `Record` / `RecordStruct` in `typeKind` and `kind:` filters instead of `Class`
- `rename_symbol` rejects invalid C# identifiers with `INVALID_PARAMETER`
- JSON-RPC notifications produce no response (per spec §4.3) — no more `{"id": null, ...}` answers to `initialize` or other no-id requests
- Relative paths from tool responses round-trip back through subsequent calls
- `FindTypeByName` returns symbols `SymbolFinder` recognizes on generator-using projects
- `sync_documents.filePaths` rejects non-string array elements with `-32602 Invalid params`

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
