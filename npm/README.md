# sharplens-mcp

[![NuGet](https://img.shields.io/nuget/v/SharpLensMcp.svg)](https://www.nuget.org/packages/SharpLensMcp)
[![npm](https://img.shields.io/npm/v/sharplens-mcp.svg)](https://www.npmjs.com/package/sharplens-mcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/pzalutski-pixel/sharplens-mcp/blob/main/LICENSE)

An MCP server providing **67 semantic analysis tools** for .NET/C#, built on Microsoft Roslyn for compiler-accurate code understanding.

## Requirements

- **.NET 8.0 SDK or later** — works with .NET 8, 9, 10, and future versions. Analyzes any .NET 8+ project/solution.

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

### Navigation & Discovery (19 tools)
Symbol info, go to definition, find references (now reports `read`/`write`/`invocation`/`cast`/`typeof`/`nameof`/`attribute` kind with optional server-side filter), find implementations, find callers, multi-hop call graph with depth bound + cycle detection, type hierarchy, symbol search, semantic query, type members, batch type members, method signatures, derived types, base types, attributes, containing member, method overloads, attribute usage search, **external type inspection** (NuGet/BCL/closed-source assemblies with members + XML doc summaries).

### Analysis (11 tools)
Diagnostics (now runs configured `DiagnosticAnalyzer`s — StyleCop, Roslynator, NetAnalyzers, custom — by default; matches CI output), data flow analysis, control flow analysis, change impact analysis, type compatibility, outgoing calls, unused code detection, code validation, complexity metrics, circular dependency detection, missing members.

### Refactoring (14 tools)
Rename, change signature (fully applies declaration + every call site across `Foo(...)` / `new Foo(...)` / `: this(...)` / `: base(...)` / `new(...)`), extract method, extract interface, generate constructor, organize usings, batch organize, batch format, code actions, apply code action by title, implement missing members, encapsulate field, inline variable, extract variable.

### Code Generation (2 tools)
Null check generation, equality member generation.

### Compound Tools (7 tools)
Type overview, method analysis, file overview, method source, batch method source, instantiation options, **project health audit** (composite dashboard: diagnostics + unused + coupling + coverage in one call) — combining multiple queries to reduce round-trips.

### Audit & Quality (2 tools)
**Detect god-objects** via efferent + afferent coupling + member-count thresholds (audit-time scoring). **Find untested code** — public/internal surface not transitively reachable from any `[Fact]`/`[Theory]`/`[Test]`/`[TestMethod]`, sorted by cyclomatic complexity so the riskiest gaps surface first.

### Discovery (2 tools)
DI registration scanning, reflection usage detection.

### Infrastructure (10 tools)
Health check, solution loading, document synchronization, project structure, dependency graph, code fixes, apply code fix, NuGet dependency listing, source generator inspection, generated code viewer.

## Configuration

| Environment Variable | Description | Default |
|---------------------|-------------|---------|
| `DOTNET_SOLUTION_PATH` | Auto-load .sln or .slnx on startup | (none) |
| `ROSLYN_LOG_LEVEL` | Trace/Debug/Information/Warning/Error | Information |
| `ROSLYN_TIMEOUT_SECONDS` | Operation timeout | 30 |
| `ROSLYN_MAX_DIAGNOSTICS` | Maximum diagnostics to return | 100 |
| `ROSLYN_ENABLE_SEMANTIC_CACHE` | Enable semantic model caching | true |
| `SHARPLENS_ABSOLUTE_PATHS` | Use absolute paths instead of relative | false |

## Uninstall

If installed globally: `npm uninstall -g sharplens-mcp` (automatically removes the .NET tool)

If used via npx: `dotnet tool uninstall --global SharpLensMcp`

## Documentation

Full documentation, tool reference, and architecture details: [GitHub](https://github.com/pzalutski-pixel/sharplens-mcp)
