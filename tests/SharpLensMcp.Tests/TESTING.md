# Testing Charter (C1–C9)

The standing contract for **every** test in this project. A test that cannot
fail on a wrong answer is a defect, not coverage — "it returned something" is
never a test. These rules codify the lessons from two test-integrity audits;
they are binding on all existing and future tests, and every new tool's commit
must carry both test levels (C4).

---

## C1 — Lock a specific known value

Every test asserts at least one concrete expected value: an exact name, an exact
count, an exact substring, an exact error code, an exact `(line, column)`.

Banned **as the sole assertion** on the value a test is named for:
`NotBeNull()` / `NotBeNullOrEmpty()`, `BeGreaterThan(0)` / `BeGreaterOrEqualTo(0)`
on counts or coordinates, `.Type.Should().Be(JTokenType.Integer/Boolean)`
(type-only), and `(success or error).Should().BeTrue()`. They may appear *in
addition to* a content lock, never in place of one.

## C2 — No null-conditional in assertions

`data["x"]?.Value<T>().Should().Be(y)` silently skips the entire assertion when
the key is absent — the `?.` short-circuits `.Should()`. Instead:

```csharp
data["x"].Should().NotBeNull();          // names the missing field on failure
data["x"]!.Value<T>().Should().Be(y);    // throws if the type is wrong
```

The bare `?.` is fine inside `.Select`/`.Any`/`.Where` projections (it isn't an
assertion there) — only the `?.…Should()` chain is banned. Note the regex
`?.Value<T>().Should()` does **not** catch `["x"]?.Type.Should()` or
`?.Count.Should()`; find those by reading, not pattern-matching.

## C3 — Field names verified against the actual serializer on the test's path

Two serializers, two casings. Confirm against the implementation before asserting.

| Read path | Serializer | Casing |
|---|---|---|
| In-process `JObject.FromObject(result)` | Newtonsoft | envelope `success`/`data`/`meta` are **lowercase** (anonymous object)… |
| …but `meta` (a `ResponseMetadata` class) | Newtonsoft | **PascalCase** `TotalCount` / `ReturnedCount` / `Truncated` |
| …and `error` (a `RoslynError` class) | Newtonsoft | **PascalCase** `Code` / `Message` / `Hint` / `Context` |
| Over the MCP wire (`content[0].text`) | System.Text.Json | **camelCase** everywhere: `code` / `message` / `hint`, `totalCount`, … |

So an in-process error test reads `json["error"]!["Code"]`; the same assertion
over the wire (via `McpTestBase`) reads `error["code"]`. Mismatched casing makes
the read null and (with C2 violated) silently passes.

## C4 — Two mandatory levels for EVERY tool

Both ship in the tool's own commit (retroactive for existing tools):

- **Unit level** — at least one content-locked test of the `RoslynService` method
  against a deterministic fixture.
- **Integration level** — at least one test that drives the tool **through the MCP
  dispatcher** (JSON-RPC), unwraps `content[0].text`, and asserts exact
  pre-determined values, **plus** at least one error path through the wire (which
  validates the camelCase `error.code` mapping). A direct `Service.XAsync()` call
  is a unit test, never integration; "integration" means through the
  dispatcher/wire (`McpTestBase.CallAndGetDataAsync` / `CallAndGetErrorAsync`).

Wire tests live in `Mcp/*ViaMcpTests.cs` and `Mcp/WireCoverageViaMcpTests.cs`.
For tools whose main path needs inputs that can't exist in the shared clean
solution (in-memory-only fixtures, deliberate diagnostics, C# 14 extension
blocks), wire-cover the deterministic error path and keep the value-pinned main
path at the unit level — and say so in a comment.

## C5 — Deterministic fixtures, never live source

Expected values come from fixed `Fixtures/*.cs`, not the evolving repo. No
inequality thresholds against live source (`totalReferences >= 2`). Positions are
pinned **exactly** by resolving a line from a marker string and asserting
equality — never merely `> 0`:

```csharp
var line = Array.FindIndex(File.ReadAllLines(fixturePath),
    l => l.Contains("return ComputeAsync().Result;"));
result["location"]!["line"]!.Value<int>().Should().Be(line); // 0-based, Roslyn's convention
```

Re-grounding examples: `ReferenceCountFixture.Anchor` (exactly 5 references / 5
callers), `ComplexityFixture` (cyclomatic 1 for `Trivial`, 6 for `Branchy`),
`GodObjectFixtureClass` (efferent 25 / members 25 / afferent 0 / score 20).
Documented exception: tools whose subject **is** the repo (the `diff_api_surface`
zero-breaking-vs-`HEAD` canary).

## C6 — Out-of-process spine

`StdioIntegrationTests` spawns the **real built binary** and drives JSON-RPC over
stdin/stdout. It value-pins at least one representative tool **per category**
(not just the session contract) and runs the `tools/list` golden `DeepEquals`
over the actual stdio pipe — not only in-process. This is the only layer that
covers wire serialization, stdout purity, `DOTNET_SOLUTION_PATH` auto-load, and
clean EOF shutdown.

## C7 — The run is itself deterministic

No pass may depend on timing or scheduling.

- **Serialization:** `xunit.runner.json` sets `parallelizeTestCollections: false`,
  so no two whole-solution MSBuild loads or on-disk fixture mutations overlap.
- **Crash-safe mutation:** a fixture-mutating test snapshots content, restores it
  in `DisposeAsync` (recreating a deleted file), and **asserts the restore
  landed**; a test that writes during `InitializeAsync` restores in a `catch` so
  an init-time failure can't leave the tree dirty (xUnit skips `DisposeAsync`
  after a failed init). Prefer a temp-dir copy over mutating a compiled fixture.
- **No timing seams:** the tool-call timeout test uses the forced-cancellation
  seam — `internal McpServer(TimeSpan, CancellationToken)` links a pre-cancelled
  token into the per-call CTS and is checked before dispatch, yielding `TIMEOUT`
  deterministically — never a `1 ms`-budget race.

## C8 — Global invariant canaries stay reliable

Keep the solution-wide canaries in `SolutionHealthInvariantTests` (a clean
solution ⇒ 0 compiler errors; each generated tree appears exactly once) and keep
them immune to fixture-mutation races (C7). They catch pipeline regressions
(e.g. a generator pass running twice) regardless of which tool would surface it.

## C9 — Pre-commit gate

Build-clean + green is necessary, **not** sufficient. Before staging: re-read
every changed test end-to-end; for each assertion ask *"what specific wrong
answer does this catch?"* If the answer is "none / it crashed / the field was
missing," it fails the gate. Reason through whether the test would fail if the
expected value were wrong.

---

## Two test styles — when to use which

- **Solution-loaded** (`RoslynServiceTestBase`-derived): the whole
  `SharpLensMcp.sln` is loaded via `MSBuildWorkspace`. Use when the tool needs
  real project references, source generators, or genuine "works against our
  codebase" coverage. The ~30 s load is amortized across the run.
- **In-memory** (`TestHelpers.CreateWorkspaceWithCode` +
  `RoslynService.LoadFromWorkspaceForTesting`): an `AdhocWorkspace` with one
  hand-crafted document. Use for tools designed for incomplete/broken code
  (`get_missing_members`, `validate_code`, `find_dead_branches`,
  `find_naming_violations`) or when you need exact control over input shape — and
  for inputs that can't sit on disk without breaking the clean-solution canary
  (CS0535, dead-branch CS0162, C# 14 extension blocks the SDK 8 compiler rejects).
  The established Roslyn-community pattern; see `GetMissingMembersTests.cs`.

## Anti-patterns — smoke signals a "test" isn't one

```csharp
// SMOKE: null-conditional silently skips the assertion (C2)
data["x"]?.Value<int>().Should().Be(5);

// SMOKE: shape-only / type-only, the sole assertion (C1)
data["x"].Should().NotBeNull();
data["x"]!.Type.Should().Be(JTokenType.Integer);

// SMOKE: passes on the wrong outcome — the forbidden "behavior contract"
(json["success"]?.Value<bool>() == true || json["error"] != null).Should().BeTrue();

// SMOKE: silent skip / foreach-with-fallback hides the empty case
if (symbols?.Count > 0) { ...assertions... }
foreach (var d in (data["diagnostics"] as JArray) ?? new JArray()) { ... }

// SMOKE: inequality against live, evolving source (C5)
data["totalReferences"]!.Value<int>().Should().BeGreaterThan(1);
```

A cursor-position-sensitive refactoring tool must either use a fixture position
where the action is **always** offered and lock specific preview content, or a
position where it's **never** offered and lock the exact `ErrorCodes.*` value. If
neither is reliably reproducible, the tool needs better engineering — not a
weaker test.
