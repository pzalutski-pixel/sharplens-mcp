# SharpLensMcp

[![NuGet](https://img.shields.io/nuget/v/SharpLensMcp.svg)](https://www.nuget.org/packages/SharpLensMcp)
[![npm](https://img.shields.io/npm/v/sharplens-mcp.svg)](https://www.npmjs.com/package/sharplens-mcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Model Context Protocol (MCP) server providing **92 AI-optimized tools** for .NET/C# semantic code analysis, navigation, refactoring, and code generation using Microsoft Roslyn.

Built for AI coding agents - provides compiler-accurate code understanding that AI cannot infer from reading source files alone.

## Installation

### Via NuGet (Recommended)
```bash
dotnet tool install -g SharpLensMcp
```

Then run with:
```bash
sharplens
```

### Via npm
```bash
npx -y sharplens-mcp
```

### Build from Source
```bash
dotnet build -c Release
dotnet publish -c Release -o ./publish
```

## Claude Code Setup

1. **Install the tool** (pick one):
```bash
dotnet tool install -g SharpLensMcp
# or
npx -y sharplens-mcp
```

2. **Create `.mcp.json` in your project root**:
```json
{
  "mcpServers": {
    "sharplens": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "sharplens-mcp"],
      "env": {
        "DOTNET_SOLUTION_PATH": "/path/to/your/Solution.sln (or .slnx)"
      }
    }
  }
}
```

3. **Restart Claude Code** to load the MCP server

4. **Verify** by asking Claude to run a health check on the Roslyn server

### Why Use This with Claude Code?

Claude Code has native LSP support for basic navigation (go-to-definition, find references). SharpLensMcp adds **deep semantic analysis**:

| Capability | Native LSP | SharpLensMcp |
|------------|------------|--------------|
| Go to definition | ✅ | ✅ |
| Find references | ✅ | ✅ |
| Find async methods missing CancellationToken | ❌ | ✅ |
| Impact analysis (what breaks?) | ❌ | ✅ |
| Dead code detection | ❌ | ✅ |
| Complexity metrics | ❌ | ✅ |
| Safe refactoring with preview | ❌ | ✅ |
| Batch operations | ❌ | ✅ |

## Configuration

| Environment Variable | Description | Default |
|---------------------|-------------|---------|
| `DOTNET_SOLUTION_PATH` | Path to `.sln` or `.slnx` file to auto-load on startup | None (must call `load_solution`) |
| `SHARPLENS_ABSOLUTE_PATHS` | Use absolute paths instead of relative | `false` (relative paths save tokens) |
| `SHARPLENS_LOG_LEVEL` | Logging verbosity: `Trace`, `Debug`, `Information`, `Warning`, `Error` | `Information` |
| `SHARPLENS_TIMEOUT_SECONDS` | Timeout for long-running operations | `30` |
| `SHARPLENS_MAX_DIAGNOSTICS` | Maximum diagnostics to return | `100` |
| `SHARPLENS_ENABLE_SEMANTIC_CACHE` | Enable semantic model caching | `true` (set to `false` to disable) |

The pre-1.6.0 `ROSLYN_*` spellings of the last four variables are still read as a fallback for one release; the `SHARPLENS_*` spelling wins when both are set.

If `DOTNET_SOLUTION_PATH` is not set, you must call the `load_solution` tool before using other tools.

## Migrating from 1.5.x tool names

Tool names no longer carry the `roslyn:` prefix — the colon violates the MCP tool-name pattern (`^[a-zA-Z0-9_-]{1,64}$`), which some clients enforce. Every tool keeps its name minus the prefix:

| 1.5.x name | 1.6.0 name |
|------------|------------|
| `roslyn:load_solution` | `load_solution` |
| `roslyn:get_diagnostics` | `get_diagnostics` |
| `roslyn:rename_symbol` | `rename_symbol` |
| ...same rule for all tools... | drop the `roslyn:` prefix |

`tools/list` publishes only the new names. Calls using the old prefixed names are still accepted as aliases for one release and will be removed in the following one.

## AI Agent Configuration Tips

AI models may have trained bias toward using their native tools (Grep, Read, LSP) instead of MCP server tools, even when SharpLensMcp provides better capabilities.

**To ensure optimal tool usage:**

1. **Claude Code**: Add to your project's `CLAUDE.md`:
   ```
   For C# code analysis, prefer SharpLensMcp tools over native tools:
   - Use `search_symbols` instead of Grep for finding symbols
   - Use `get_method_source` instead of Read for viewing methods
   - Use `find_references` for semantic (not text) references
   ```

2. **Other MCP clients**: Configure tool priority in your agent's system prompt

The semantic analysis from Roslyn is more accurate than text-based search, especially for overloaded methods, partial classes, and inheritance hierarchies.

## Agent Responsibility: Document Synchronization

**Important:** SharpLensMcp maintains an in-memory representation of your solution for fast queries. When files are modified externally (via Edit/Write tools), the agent is responsible for synchronizing changes.

### When to call `sync_documents`:

| Action | Call sync_documents? |
|--------|---------------------|
| Used Edit tool to modify .cs files | ✅ **Yes** |
| Used Write tool to create new .cs files | ✅ **Yes** |
| Deleted .cs files | ✅ **Yes** |
| Used SharpLensMcp refactoring tools (rename, extract, etc.) | ❌ No (auto-updated) |
| Modified .csproj files | ❌ No (use `load_solution` instead) |

### Usage:

```
# After editing specific files
sync_documents(filePaths: ["src/MyClass.cs", "src/MyService.cs"])

# After bulk changes - sync all documents
sync_documents()
```

### Why this design?

This mirrors how LSP (Language Server Protocol) works - the client (editor) notifies the server of changes. This approach:
- Eliminates race conditions (agent controls timing)
- Avoids file watcher complexity and platform quirks
- Is faster than full solution reload
- Gives agents explicit control over workspace state

**If you don't sync:** Queries may return stale data (old method signatures, missing new files, etc.)

## Features

- **92 Semantic Analysis Tools** - Navigation, refactoring, code generation, diagnostics, discovery, audit/quality
- **AI-Optimized Descriptions** - Clear USAGE/OUTPUT/WORKFLOW patterns
- **Structured Responses** - Consistent `success/error/data` format with `suggestedNextTools`
- **Zero-Based Coordinates** - Clear warnings to prevent off-by-one errors
- **Preview Mode** - Safe refactoring with preview before apply
- **Batch Operations** - Multiple lookups in one call to reduce context usage

## Tool Categories

### Navigation & Discovery (24 tools)
| Tool | Description |
|------|-------------|
| `get_symbol_info` | Semantic info at position |
| `go_to_definition` | Jump to symbol definition |
| `find_references` | All references; each classified read/write/invocation/cast/typeof/nameof/attribute; optional `kind` filter |
| `find_implementations` | Interface/abstract implementations |
| `find_callers` | Impact analysis - who calls this? |
| `get_call_graph` | Multi-hop callers/callees graph with depth bound + cycle detection |
| `find_path_between` | Reachability + the connecting call path(s) between two methods; follows dispatch, with barriers and a checkpoint |
| `get_type_hierarchy` | Inheritance chain |
| `search_symbols` | Glob pattern search (`*Handler`, `Get*`) |
| `semantic_query` | Multi-filter search (async, public, etc.) |
| `get_type_members` | All members by type name |
| `get_type_members_batch` | Multiple types in one call |
| `get_method_signature` | Detailed signature by name |
| `get_derived_types` | Find all subclasses |
| `get_base_types` | Full inheritance chain |
| `get_attributes` | List attributes on a symbol |
| `get_containing_member` | Enclosing symbol at position |
| `get_method_overloads` | All overloads of a method |
| `find_attribute_usages` | Find types/members by attribute |
| `get_external_type_info` | Inspect NuGet/BCL/external assembly types — members + XML docs |
| `resolve_stack_trace` | Map a pasted stack trace to file/line/symbol, mangling undone |
| `get_extension_methods` | Extensions applying to a type — classic and C# 14 blocks |
| `get_documentation` | Full XML docs for a symbol with `<inheritdoc>` expanded |
| `get_super_method` | Navigate to the base member / interface members a member implements |

### Analysis (17 tools)
| Tool | Description |
|------|-------------|
| `get_diagnostics` | Compiler errors/warnings + configured analyzer findings (StyleCop, Roslynator, NetAnalyzers); matches CI |
| `diff_api_surface` | Public-API breaking-change report vs a git ref |
| `get_exception_flow` | Which exceptions can escape a method, and where they're caught |
| `find_similar_code` | Structural similarity search (token-shingle fingerprints) |
| `remove_unused_code` | Compute dead-code removals + newly unused usings (generation-only) |
| `find_dead_branches` | Unreachable basic blocks per method (real CFG, not heuristics) |
| `add_missing_imports` | Compute the usings that fix CS0246/CS0103 (generation-only) |
| `analyze_data_flow` | Variable assignments and usage |
| `analyze_control_flow` | Branching/reachability |
| `analyze_change_impact` | What breaks if changed? |
| `check_type_compatibility` | Can A assign to B? |
| `get_outgoing_calls` | What does this method call? |
| `find_unused_code` | Dead code detection |
| `validate_code` | Compile check without writing |
| `get_complexity_metrics` | Cyclomatic, nesting, LOC, cognitive |
| `find_circular_dependencies` | Project and namespace cycle detection |
| `get_missing_members` | Unimplemented interface/abstract members |

### Refactoring (16 tools)
| Tool | Description |
|------|-------------|
| `rename_symbol` | Safe rename across solution |
| `change_signature` | Add/remove/reorder parameters |
| `extract_method` | Extract with data flow analysis |
| `extract_interface` | Generate interface from class |
| `generate_constructor` | From fields/properties |
| `move_type_to_file` | Compute the contents to move a type into its own file (generation-only) |
| `split_type` | Compute a partial-class split for selected members (generation-only) |
| `organize_usings` | Sort and remove unused |
| `organize_usings_batch` | Batch organize multiple files |
| `format_document_batch` | Batch format files in project |
| `get_code_actions_at_position` | All Roslyn refactorings at position |
| `apply_code_action_by_title` | Apply any refactoring by title |
| `implement_missing_members` | Generate interface stubs |
| `encapsulate_field` | Field to property |
| `inline_variable` | Inline temp variable |
| `extract_variable` | Extract expression to variable |

### Code Generation (3 tools)
| Tool | Description |
|------|-------------|
| `add_null_checks` | Generate ArgumentNullException guards |
| `generate_equality_members` | Equals/GetHashCode/operators |
| `generate_test_stub` | Compilable test skeleton for a method (framework auto-detected) |

### Compound Tools (7 tools)
| Tool | Description |
|------|-------------|
| `get_type_overview` | Full type info in one call |
| `analyze_method` | Signature + callers + outgoing calls + location |
| `get_file_overview` | File summary with diagnostics |
| `get_method_source` | Source code by name |
| `get_method_source_batch` | Multiple method sources in one call |
| `get_instantiation_options` | How to create a type |
| `get_project_health` | Composite audit dashboard: diagnostics + unused + coupling + coverage per project |

### Audit & Quality (10 tools)
| Tool | Description |
|------|-------------|
| `find_god_objects` | Detect over-coupled types via efferent + afferent coupling + member-count thresholds |
| `find_untested_code` | Find public surface not reached by any [Fact]/[Theory]/[Test]/[TestMethod] |
| `find_tests` | Which tests cover a symbol — the inverse of find_untested_code |
| `find_type_instantiations` | Where a type is constructed (`new T`) |
| `find_pattern_usages` | Where a type appears in is/as/pattern matches |
| `find_throw_sites` | Where an exception type is thrown (optionally derived) |
| `find_catch_blocks` | Where an exception type is caught (optionally via a base clause) |
| `find_async_issues` | async void / blocking-on-async / unforwarded CancellationToken |
| `check_architecture` | Enforce namespace/project dependency rules over the type graph |
| `find_naming_violations` | Naming audit honoring .editorconfig rules, with conventional defaults |

### Discovery (3 tools)
| Tool | Description |
|------|-------------|
| `get_di_registrations` | Scan DI service registrations |
| `find_reflection_usage` | Detect reflection/dynamic usage |
| `find_interceptors` | Surface [InterceptsLocation] call rerouting, generated code included |

### Infrastructure (12 tools)
| Tool | Description |
|------|-------------|
| `health_check` | Server status |
| `find_unused_dependencies` | PackageReferences/ProjectReferences the compiler never needs |
| `fix_all` | Compute the fix for every instance of a diagnostic id (generation-only) |
| `load_solution` | Load .sln/.slnx for analysis |
| `sync_documents` | Sync file changes into loaded solution |
| `get_project_structure` | Solution structure |
| `dependency_graph` | Project dependencies |
| `get_code_fixes` | Available fixes for a diagnostic |
| `apply_code_fix` | Apply a specific code fix |
| `get_nuget_dependencies` | NuGet package listing per project |
| `get_source_generators` | List active source generators |
| `get_generated_code` | View generated source code |

## Other MCP Clients

For MCP clients other than Claude Code, add to your configuration:

```json
{
  "mcpServers": {
    "sharplens": {
      "command": "sharplens",
      "args": [],
      "env": {
        "DOTNET_SOLUTION_PATH": "/path/to/your/Solution.sln (or .slnx)"
      }
    }
  }
}
```

## Usage

1. **Load a solution**: Call `load_solution` with path to `.sln` or `.slnx` file (or set `DOTNET_SOLUTION_PATH`)
2. **Analyze code**: Use any of the 92 tools for navigation, analysis, refactoring, audit
3. **Refactor safely**: Preview changes before applying with `preview: true`

## Architecture

```
MCP Client (AI Agent)
        | stdin/stdout (JSON-RPC 2.0)
        v
   SharpLensMcp
   - Protocol handling
   - 92 AI-optimized tools
        |
        v
Microsoft.CodeAnalysis (Roslyn)
  - MSBuildWorkspace
  - SemanticModel
  - SymbolFinder
```

## Requirements

- **.NET 8.0 SDK or later** — works with .NET 8, 9, 10, and future versions. Analyzes any .NET 8+ project/solution.
- MCP-compatible AI agent

## FAQ

**Why does the tool target `net8.0` — can it analyze my .NET 9 / .NET 10 project?**

Yes. `net8.0` is the tool's own runtime floor — the Roslyn 5.x packages it builds on require it — not a ceiling on what it can analyze. `RollForward` lets the installed tool run on newer .NET runtimes, and `MSBuildWorkspace` loads each project's real target framework from its csproj, so one install analyzes solutions targeting .NET 8, 9, 10, and beyond.

## Development

### Adding New Tools

1. **Add the method to the matching `src/RoslynService.*.cs` partial** (Navigation, Analysis, Refactoring, CallAnalysis, …) and return through the shared response envelope:
```csharp
public async Task<object> YourToolAsync(string param1, int? param2 = null,
    CancellationToken cancellationToken = default)
{
    EnsureSolutionLoaded();
    // Your logic...
    return CreateSuccessResponse(
        data: new { /* results */ },
        suggestedNextTools: new[] { "next_tool_hint" }
    );
}
```

2. **Register one `ToolDefinition` in `src/ToolRegistry.cs`** — its name, description, input schema, the `ReadOnly` flag (mutating tools pass `ReadOnly: false` and receive a `destructiveHint` annotation), and a handler that binds arguments through `JsonRpcParameters` and calls your method. The registry drives both `tools/list` and dispatch; there is no separate switch to edit. Two tests keep it honest: `ToolsListGoldenTests` locks the published schema byte-for-byte (re-capture the golden when a schema change is intentional), and `ToolSchemaParityTests` asserts every parameter the handler reads is declared in the schema.

3. **Build and publish**:
```bash
dotnet build -c Release
dotnet publish -c Release -o ./publish
```

4. **Add both test levels** — a unit test of the `RoslynService` method against a deterministic fixture, AND a wire test through the MCP dispatcher (in `tests/SharpLensMcp.Tests/Mcp/`) with exact value locks plus an error path. This is non-negotiable; see [Testing](#testing).

### Testing

Every test must satisfy the **Testing Charter (C1–C9)** in [`tests/SharpLensMcp.Tests/TESTING.md`](tests/SharpLensMcp.Tests/TESTING.md) — the standing contract. The headline rules:

- **Lock exact values** (C1): assert a concrete name / count / substring / error code / `(line, column)` — never `NotBeNull` / `> 0` / a type-only check as the *sole* assertion.
- **Both levels per tool** (C4): a unit test against a `Fixtures/*.cs` fixture **and** a dispatcher (wire) test that unwraps `content[0].text`, plus an error path.
- **Right casing** (C3): in-process Newtonsoft yields PascalCase `error.Code` / `meta.TotalCount`; the MCP wire yields camelCase. Read the casing your test's path actually produces.
- **Deterministic** (C7): the suite is serialized via `xunit.runner.json`; fixture mutators always restore; the timeout test uses a forced-cancellation seam, not a timing race.
- **Out-of-process spine** (C6): `StdioIntegrationTests` value-pins one tool per category over the real binary and runs the `tools/list` golden over the stdio pipe.
- **Pre-commit gate** (C9): build-clean + green is necessary but **not** sufficient — re-read each changed test and confirm it fails on a wrong answer.

Run the suite:
```bash
dotnet test -c Release
```

### Key Files

| File | Purpose |
|------|---------|
| `src/RoslynService.cs` + the `src/RoslynService.*.cs` partials | Tool implementations split by concern across ~30 partials (Navigation, Analysis, Refactoring, Inspection, Validation, TypeDiscovery, Discovery, ExternalApi, Quality, Metrics, CodeActions, CodeGeneration, Compound, CallAnalysis, ExceptionFlow, StackTrace, ApiSurface, SimilarCode, …) — each file's name predicts its contents |
| `src/McpServer.cs` | MCP protocol mechanics: JSON-RPC parse loop, `initialize` negotiation, per-call timeout, in-band vs protocol error mapping |
| `src/ToolRegistry.cs` + `src/ToolDefinition.cs` | The tool surface: one `ToolDefinition` record per tool (name, schema, `ReadOnly` flag, handler). Drives `tools/list` order and dispatch lookup |
| `src/JsonRpcParameters.cs` + `JsonRpcInvalidParamsException.cs` | Typed JSON-RPC argument accessors and the `-32602 Invalid params` exception they raise |
| `src/*Data.cs` / `*Entry.cs` records, `ConstructorMember.cs`, `SignatureChange.cs` | Typed records used by the audit composite, constructor generator, and signature-change parser (one type per file) |

## License

MIT - See [LICENSE](LICENSE) for details.
<!-- mcp-name: io.github.pzalutski-pixel/sharplens -->
