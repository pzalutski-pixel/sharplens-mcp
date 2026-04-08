# sharplens-mcp

[![NuGet](https://img.shields.io/nuget/v/SharpLensMcp.svg)](https://www.nuget.org/packages/SharpLensMcp)
[![npm](https://img.shields.io/npm/v/sharplens-mcp.svg)](https://www.npmjs.com/package/sharplens-mcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/pzalutski-pixel/sharplens-mcp/blob/main/LICENSE)

An MCP server providing **62 semantic analysis tools** for .NET/C#, built on Microsoft Roslyn for compiler-accurate code understanding.

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

### Navigation & Discovery (17 tools)
Symbol info, go to definition, find references, find implementations, find callers, type hierarchy, symbol search, semantic query, type members, method signatures, derived types, base types, attributes, containing member, method overloads, attribute usage search.

### Analysis (11 tools)
Diagnostics, data flow analysis, control flow analysis, change impact analysis, type compatibility, outgoing calls, unused code detection, code validation, complexity metrics, circular dependency detection, missing members.

### Refactoring (14 tools)
Rename, change signature, extract method, extract interface, generate constructor, organize usings, batch format, code actions, implement missing members, encapsulate field, inline variable, extract variable.

### Code Generation (2 tools)
Null check generation, equality member generation.

### Compound Tools (6 tools)
Type overview, method analysis, file overview, method source, batch method source, instantiation options — combining multiple queries to reduce round-trips.

### Discovery (2 tools)
DI registration scanning, reflection usage detection.

### Infrastructure (10 tools)
Health check, solution loading, document synchronization, project structure, dependency graph, code fixes, NuGet dependency listing, source generator inspection, generated code viewer.

## Configuration

| Environment Variable | Description | Default |
|---------------------|-------------|---------|
| `DOTNET_SOLUTION_PATH` | Auto-load .sln or .slnx on startup | (none) |
| `ROSLYN_LOG_LEVEL` | Trace/Debug/Information/Warning/Error | Information |
| `ROSLYN_TIMEOUT_SECONDS` | Operation timeout | 30 |
| `ROSLYN_MAX_DIAGNOSTICS` | Maximum diagnostics to return | 100 |
| `SHARPLENS_ABSOLUTE_PATHS` | Use absolute paths instead of relative | false |

## Uninstall

If installed globally: `npm uninstall -g sharplens-mcp` (automatically removes the .NET tool)

If used via npx: `dotnet tool uninstall --global SharpLensMcp`

## Documentation

Full documentation, tool reference, and architecture details: [GitHub](https://github.com/pzalutski-pixel/sharplens-mcp)
