# Tool test contract

Rules every test in this project must follow. Codifies the lessons from our test-integrity audit so the "vacuous test" failure mode doesn't recur.

## The contract

1. **Exact-content assertions, not shape-only.** A test must assert at least one specific value — a name, a count, a substring, a kind. `Should().NotBeNull()` and `Should().NotBeEmpty()` are allowed *in addition* to a content check, not in place of one.

2. **No silent skips.** Never write `if (results?.Count > 0) { ...assertions... }`. The fixture must guarantee the precondition holds; if it can't (e.g. the underlying tool returns success on some branch and failure on another), assert both branches explicitly.

3. **No placebo bodies.** A test body that ends with `var json = JObject.FromObject(result); // Just verify no crash` is not a test. Either delete it or make it assert something the implementation can break.

4. **Response field names live in code.** When a test reads `data["someField"]`, that field must exist in the implementation's `CreateSuccessResponse` payload. Mismatches mean the test never observes anything and silently passes. (See: the v1.5.3 audit, where 25 tests gated on `data["symbols"]` while the impl returned `data["results"]`.)

5. **Use `SemanticAssertions` for shared patterns.** `SemanticAssertions.AssertSymbolFound`, `AssertReferencesInclude`, etc. lower the friction of writing a real assertion to the same length as writing a vacuous one. Prefer them over hand-rolled `.Any(...).Should().BeTrue()` when the pattern repeats.

6. **Tests that modify disk must restore.** Tools like `change_signature` and `rename_symbol` apply edits to source files. Tests for those tools snapshot the file content in `InitializeAsync` and restore it in `DisposeAsync` so xUnit's per-test isolation actually holds.

## Anti-patterns to grep for in review

These are smoke signals that a test isn't a test:

```csharp
// SMOKE: silent skip if search returns nothing
var symbols = GetData(searchResult)["results"] as JArray;
if (symbols?.Count > 0)
{
    ...
}

// SMOKE: foreach-with-fallback hides empty case
foreach (var diag in (data["diagnostics"] as JArray) ?? new JArray())
{
    ...
}

// SMOKE: assertion that always passes
var json = JObject.FromObject(result);
// Verify no crash

// SMOKE: shape-only
data["someField"].Should().NotBeNull();
```

Reviewers: if you see any of these, ask the author what concrete failure the test would catch.

## Two test styles, two when-to-use rules

**Solution-loaded integration tests** (`RoslynServiceTestBase`-derived). The whole `SharpLensMcp.sln` is loaded via `MSBuildWorkspace`. Use for tools that need real project references, real source generators, or where end-to-end "this works against our actual codebase" is the assertion. Cost: ~30s solution load amortized across all tests in the run.

**In-memory tests** (`TestHelpers.CreateWorkspaceWithCode` + `RoslynService.LoadFromWorkspaceForTesting`). An `AdhocWorkspace` with a single hand-crafted document. Use for tools designed to work on incomplete code (`get_missing_members`, `validate_code`, `add_null_checks`) or anywhere you want exact control over input shape and don't need cross-file references. Cost: ~5ms per test. See `GetMissingMembersTests.cs` for the canonical pattern.

**Why both:** Some tools must be tested on broken code (CS0535 etc. are hardwired compiler errors; the file can't sit on disk in our test project without suppression, which the project doesn't allow). The in-memory path is also the established Roslyn community pattern (`dotnet/roslyn`, `dotnet-format`). The solution-loaded path catches integration regressions the in-memory tests can't.

## When you can't pin to a specific value

Some tools (Roslyn refactorings, code actions) legitimately return success-or-clear-error depending on cursor position quirks. For those, write a *behavior contract* test:

```csharp
var json = JObject.FromObject(result);
(json["success"]?.Value<bool>() == true || json["error"] != null)
    .Should().BeTrue("tool must return either a structured success or a structured error");
```

That still catches: null returns, raw exceptions, malformed JSON. Don't dilute it to "no crash."
