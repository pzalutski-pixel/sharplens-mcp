# SharpLensMcp

[![NuGet](https://img.shields.io/nuget/v/SharpLensMcp.svg)](https://www.nuget.org/packages/SharpLensMcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A Model Context Protocol (MCP) server providing **57 AI-optimized tools** for .NET/C# semantic code analysis, navigation, refactoring, and code generation using Microsoft Roslyn.

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

### Build from Source
```bash
dotnet build -c Release
dotnet publish -c Release -o ./publish
```

## Features

- **57 Semantic Analysis Tools** - Navigation, refactoring, code generation, diagnostics
- **AI-Optimized Descriptions** - Clear USAGE/OUTPUT/WORKFLOW patterns
- **Structured Responses** - Consistent `success/error/data` format with `suggestedNextTools`
- **Zero-Based Coordinates** - Clear warnings to prevent off-by-one errors
- **Preview Mode** - Safe refactoring with preview before apply
- **Batch Operations** - Multiple lookups in one call to reduce context usage

## Tool Categories

### Navigation & Discovery (13 tools)
| Tool | Description |
|------|-------------|
| `get_symbol_info` | Semantic info at position |
| `go_to_definition` | Jump to symbol definition |
| `find_references` | All references across solution |
| `find_implementations` | Interface/abstract implementations |
| `find_callers` | Impact analysis - who calls this? |
| `get_type_hierarchy` | Inheritance chain |
| `search_symbols` | Glob pattern search (`*Handler`, `Get*`) |
| `semantic_query` | Multi-filter search (async, public, etc.) |
| `get_type_members` | All members by type name |
| `get_type_members_batch` | Multiple types in one call |
| `get_method_signature` | Detailed signature by name |
| `get_derived_types` | Find all subclasses |
| `get_base_types` | Full inheritance chain |

### Analysis (9 tools)
| Tool | Description |
|------|-------------|
| `get_diagnostics` | Compiler errors/warnings |
| `analyze_data_flow` | Variable assignments and usage |
| `analyze_control_flow` | Branching/reachability |
| `analyze_change_impact` | What breaks if changed? |
| `check_type_compatibility` | Can A assign to B? |
| `get_outgoing_calls` | What does this method call? |
| `find_unused_code` | Dead code detection |
| `validate_code` | Compile check without writing |
| `get_complexity_metrics` | Cyclomatic, nesting, LOC, cognitive |

### Refactoring (13 tools)
| Tool | Description |
|------|-------------|
| `rename_symbol` | Safe rename across solution |
| `change_signature` | Add/remove/reorder parameters |
| `extract_method` | Extract with data flow analysis |
| `extract_interface` | Generate interface from class |
| `generate_constructor` | From fields/properties |
| `organize_usings` | Sort and remove unused |
| `organize_usings_batch` | Batch organize multiple files |
| `get_code_actions_at_position` | All Roslyn refactorings at position |
| `apply_code_action_by_title` | Apply any refactoring by title |
| `implement_missing_members` | Generate interface stubs |
| `encapsulate_field` | Field to property |
| `inline_variable` | Inline temp variable |
| `extract_variable` | Extract expression to variable |

### Code Generation (2 tools)
| Tool | Description |
|------|-------------|
| `add_null_checks` | Generate ArgumentNullException guards |
| `generate_equality_members` | Equals/GetHashCode/operators |

### Compound Tools (5 tools)
| Tool | Description |
|------|-------------|
| `get_type_overview` | Full type info in one call |
| `analyze_method` | Signature + callers + location |
| `get_file_overview` | File summary with diagnostics |
| `get_method_source` | Source code by name |
| `get_instantiation_options` | How to create a type |

### Infrastructure (5 tools)
| Tool | Description |
|------|-------------|
| `health_check` | Server status |
| `load_solution` | Load .sln for analysis |
| `get_project_structure` | Solution structure |
| `dependency_graph` | Project dependencies |
| `get_code_fixes` / `apply_code_fix` | Automated fixes |

## Configure MCP Client

Add to your MCP client configuration:

```json
{
  "mcpServers": {
    "sharplens": {
      "command": "sharplens",
      "args": [],
      "env": {
        "DOTNET_SOLUTION_PATH": "/path/to/your/Solution.sln"
      }
    }
  }
}
```

## Usage

1. **Load a solution**: Call `roslyn:load_solution` with path to `.sln` file (or set `DOTNET_SOLUTION_PATH`)
2. **Analyze code**: Use any of the 57 tools for navigation, analysis, refactoring
3. **Refactor safely**: Preview changes before applying with `preview: true`

## Architecture

```
MCP Client (AI Agent)
        | stdin/stdout (JSON-RPC 2.0)
        v
   SharpLensMcp
   - Protocol handling
   - 57 AI-optimized tools
        |
        v
Microsoft.CodeAnalysis (Roslyn)
  - MSBuildWorkspace
  - SemanticModel
  - SymbolFinder
```

## Requirements

- .NET 8.0 SDK/Runtime
- MCP-compatible AI agent

## Development

### Adding New Tools

1. **Add method to `src/RoslynService.cs`**:
```csharp
public async Task<object> YourToolAsync(string param1, int? param2 = null)
{
    EnsureSolutionLoaded();
    // Your logic...
    return CreateSuccessResponse(
        data: new { /* results */ },
        suggestedNextTools: new[] { "next_tool_hint" }
    );
}
```

2. **Add tool definition to `src/McpServer.cs`** in `HandleListToolsAsync`

3. **Add routing to `src/McpServer.cs`** in `HandleToolCallAsync` switch

4. **Build and publish**:
```bash
dotnet build -c Release
dotnet publish -c Release -o ./publish
```

### Key Files

| File | Purpose |
|------|---------|
| `src/RoslynService.cs` | Tool implementations (57 methods) |
| `src/McpServer.cs` | MCP protocol, tool definitions, routing |

## License

MIT - See [LICENSE](LICENSE) for details.
