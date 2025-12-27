# Changelog

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
