# sharplens-mcp

[![NuGet](https://img.shields.io/nuget/v/SharpLensMcp.svg)](https://www.nuget.org/packages/SharpLensMcp)
[![npm](https://img.shields.io/npm/v/sharplens-mcp.svg)](https://www.npmjs.com/package/sharplens-mcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/pzalutski-pixel/sharplens-mcp/blob/main/LICENSE)

A Model Context Protocol (MCP) server providing **58 AI-optimized tools** for .NET/C# semantic code analysis, navigation, refactoring, and code generation using Microsoft Roslyn.

## Requirements

- **.NET 8.0 SDK or later** — works with .NET 8, 9, 10, and future versions

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

This is a thin npm wrapper around the [SharpLensMcp](https://www.nuget.org/packages/SharpLensMcp) .NET global tool. It:

1. Checks that the .NET SDK is installed
2. Installs or updates the SharpLensMcp .NET tool to the exact version matching this npm package
3. Launches `sharplens` and pipes stdin/stdout for MCP protocol communication

The npm package version and .NET tool version are always kept in sync.

## Features

- **58 Semantic Analysis Tools** — navigation, refactoring, code generation, diagnostics
- **AI-Optimized** — structured responses with suggested next tools
- **Safe Refactoring** — preview changes before applying
- **Batch Operations** — multiple lookups in one call

## Uninstall

If installed globally: `npm uninstall -g sharplens-mcp` (automatically removes the .NET tool)

If used via npx: `dotnet tool uninstall --global SharpLensMcp`

For full documentation, see the [GitHub repository](https://github.com/pzalutski-pixel/sharplens-mcp).
