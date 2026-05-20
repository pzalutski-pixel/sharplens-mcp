# Changelog

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
