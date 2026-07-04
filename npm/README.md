# sharplens-mcp

[![NuGet](https://img.shields.io/nuget/v/SharpLensMcp.svg)](https://www.nuget.org/packages/SharpLensMcp)
[![npm](https://img.shields.io/npm/v/sharplens-mcp.svg)](https://www.npmjs.com/package/sharplens-mcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/pzalutski-pixel/sharplens-mcp/blob/main/LICENSE)

An MCP server providing **91 semantic analysis tools** for .NET/C#, built on Microsoft Roslyn for compiler-accurate code understanding.

## Requirements

- **.NET 8.0 SDK or later** — works with .NET 8, 9, 10, and future versions. Analyzes any .NET 8+ project/solution.

**Why does the tool target `net8.0` — can it analyze my .NET 9 / .NET 10 project?** Yes. `net8.0` is the tool's own runtime floor — the Roslyn 5.x packages it builds on require it — not a ceiling on what it can analyze. `RollForward` lets the installed tool run on newer .NET runtimes, and `MSBuildWorkspace` loads each project's real target framework from its csproj, so one install analyzes solutions targeting .NET 8, 9, 10, and beyond.

## Quick Start

```json
{
  "mcpServers": {
    "sharplens": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "sharplens-mcp"],
      "env": {
        "DOTNET_SOLUTION_PATH": "/path/to/your/Solution.sln"
      }
    }
  }
}
```

## What This Package Does

This npm package is a thin wrapper around the [SharpLensMcp](https://www.nuget.org/packages/SharpLensMcp) .NET global tool. It:

1. Checks that the .NET SDK is installed
2. Installs or updates the SharpLensMcp .NET tool to the exact version matching this npm package
3. Launches `sharplens` and pipes stdin/stdout for MCP protocol communication

The npm package version and .NET tool version are always kept in sync.

## Why SharpLens?

AI systems need compiler-accurate insights that reading source files cannot provide. When an AI uses `grep` to find usages of a method, it cannot distinguish overloaded methods, partial classes, or interface implementations.

| Approach | Result |
|----------|--------|
| `grep "Save("` | 34 matches including `SaveAsync()`, `SaveButton`, XML comments |
| `find_references` | Exactly 8 calls to `UserService.Save()` |

## Features

### Navigation & Discovery (23 tools)
Symbol info, go to definition, find references (reports `read`/`write`/`invocation`/`cast`/`typeof`/`nameof`/`attribute` kind with optional server-side filter), find implementations, find callers, multi-hop call graph with depth bound + cycle detection, type hierarchy, symbol search (glob or fuzzy camel-hump matching), semantic query, type members, batch type members, method signatures, derived types, base types, attributes, containing member, method overloads, attribute usage search, **external type inspection** (NuGet/BCL/closed-source assemblies with members + XML doc summaries), **stack-trace resolution** (map a .NET trace to source with compiler mangling undone), **applicable extension methods** (classic `this`-parameter and C# 14 `extension` blocks), **symbol documentation** with `<inheritdoc>` resolved recursively, and **super-method navigation** (jump to the overridden or implemented member).

### Analysis (17 tools)
Diagnostics (runs configured `DiagnosticAnalyzer`s — StyleCop, Roslynator, NetAnalyzers, custom — by default; matches CI output), **API-surface diff** against a git ref, **exception-flow analysis** (which exceptions can escape a method and where each is caught), **structural similar-code search**, **remove-unused-code** computation (generation-only), **dead-branch detection** (unreachable code from the real control-flow graph), **add-missing-imports** computation (compiler-accurate), data flow analysis, control flow analysis, change impact analysis, type compatibility, outgoing calls, unused code detection, code validation, complexity metrics, circular dependency detection, and missing members.

### Refactoring (16 tools)
Rename, change signature (fully applies declaration + every call site across `Foo(...)` / `new Foo(...)` / `: this(...)` / `: base(...)` / `new(...)`), extract method, extract interface, generate constructor, **move type to file**, **split type** into a new partial, organize usings, batch organize, batch format, code actions, apply code action by title, implement missing members, encapsulate field, inline variable, and extract variable. Refactoring tools also accept `typeName`/`memberName` name-based addressing as an alternative to zero-based coordinates.

### Code Generation (3 tools)
Null check generation, equality member generation, **test-stub generation** (test framework auto-detected from the solution).

### Compound Tools (7 tools)
Type overview, method analysis, file overview, method source, batch method source, instantiation options, **project health audit** (composite dashboard: diagnostics + unused + coupling + coverage in one call) — combining multiple queries to reduce round-trips.

### Audit & Quality (10 tools)
**Detect god-objects** via efferent + afferent coupling + member-count thresholds (audit-time scoring). **Find untested code** — public/internal surface not transitively reachable from any `[Fact]`/`[Theory]`/`[Test]`/`[TestMethod]`, sorted by cyclomatic complexity so the riskiest gaps surface first. **Find tests** that cover a symbol. Fine-grained type-usage searches — **type instantiations**, **pattern usages** (`is`/`as`/patterns), **throw sites**, and **catch blocks** for a type. **Find async issues** (`async void`, blocking-on-async, unforwarded `CancellationToken`). **Check architecture** — enforce dependency rules over the real type-dependency graph. **Find naming violations** honoring the solution's `.editorconfig`, with conventional defaults.

### Discovery (3 tools)
DI registration scanning, reflection usage detection, and **interceptor discovery** (`[InterceptsLocation]` call rerouting, generated code included).

### Infrastructure (12 tools)
Health check, **unused dependency detection** (PackageReference/ProjectReference the compiler never needs), **fix-all** for a diagnostic across a file, project, or solution, solution loading, document synchronization, project structure, dependency graph, code fixes, apply code fix, NuGet dependency listing, source generator inspection, and generated code viewer.

## Configuration

| Environment Variable | Description | Default |
|---------------------|-------------|---------|
| `DOTNET_SOLUTION_PATH` | Auto-load .sln or .slnx on startup | (none) |
| `SHARPLENS_LOG_LEVEL` | Trace/Debug/Information/Warning/Error | Information |
| `SHARPLENS_TIMEOUT_SECONDS` | Operation timeout | 30 |
| `SHARPLENS_MAX_DIAGNOSTICS` | Maximum diagnostics to return | 100 |
| `SHARPLENS_ENABLE_SEMANTIC_CACHE` | Enable semantic model caching | true |
| `SHARPLENS_ABSOLUTE_PATHS` | Use absolute paths instead of relative | false |

The legacy `ROSLYN_*` spellings (e.g. `ROSLYN_LOG_LEVEL`) remain accepted as a one-release migration fallback.

## Uninstall

If installed globally: `npm uninstall -g sharplens-mcp` (automatically removes the .NET tool)

If used via npx: `dotnet tool uninstall --global SharpLensMcp`

## Documentation

Full documentation, tool reference, and architecture details: [GitHub](https://github.com/pzalutski-pixel/sharplens-mcp)
