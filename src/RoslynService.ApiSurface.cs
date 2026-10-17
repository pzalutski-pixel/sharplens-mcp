using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;

namespace SharpLensMcp;

// diff_api_surface: public-API breaking-change report against a git ref.
// The baseline surface is rebuilt from the files the project compiled at the ref: the
// project file as it read at the ref, evaluated by MSBuild, decides which .cs files of
// the ref's tree are Compile items (SDK globs, Compile Remove, linked Compile Include —
// pure git reads: ls-tree / cat-file). They compile into an in-memory compilation that
// borrows the CURRENT compilation's references and options (declarations are all that
// matter), plus the current build-generated sources that are no Compile item (implicit
// global usings, assembly info), and the project's source generators run on it as they
// run on the current compilation — so generated API is compared like authored API. Both
// surfaces snapshot to signature strings keyed by DocumentationCommentId, so "removed",
// "changed signature", and "added" fall out of a dictionary diff. A multi-targeted project
// is compared per target framework and each change reported once with the frameworks it
// occurs in. Compares declaration signatures only — base-list and attribute changes are
// not reported.
public partial class RoslynService
{
    internal sealed record ApiSurfaceEntry(
        string Key,
        string Signature,
        string? ContainingTypeKey,
        string? FilePath,
        int? Line);

    public async Task<object> DiffApiSurfaceAsync(
        string baselineRef, string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(baselineRef))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "baselineRef must be a git ref (tag, branch, or commit), e.g. 'v1.5.3' or 'HEAD~5'",
                context: new { baselineRef });
        }

        var (scope, scopeError) = ResolveProjectScope(projectName);
        if (scope == null) return scopeError!;

        var model = GetLogicalProjects();
        var projects = scope.Where(p => p.FilePath != null).ToList();
        if (projects.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotFound,
                projectName == null
                    ? "The solution has no projects with a file path to compare"
                    : $"Project '{projectName}' has no file path to compare",
                hint: $"Available projects: {string.Join(", ", model.ValidNames)}",
                context: new { projectName });
        }

        var solutionDir = Path.GetDirectoryName(_solution!.FilePath)!;
        var (rootOk, rootOut, rootErr) = await RunGitAsync(
            solutionDir, new[] { "rev-parse", "--show-toplevel" }, cancellationToken);
        if (!rootOk)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "The solution directory is not inside a git repository",
                hint: "diff_api_surface rebuilds the baseline from git history and needs the repository available.",
                context: new { solutionDir = FormatPath(solutionDir), gitError = rootErr.Trim() });
        }
        var repoRoot = Path.GetFullPath(rootOut.Trim());

        var (refOk, _, refErr) = await RunGitAsync(
            repoRoot, new[] { "rev-parse", "--verify", "--quiet", $"{baselineRef}^{{commit}}" }, cancellationToken);
        if (!refOk)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"baselineRef '{baselineRef}' does not resolve to a commit",
                hint: "Use a tag, branch, or commit reachable locally (e.g. 'HEAD~5'); fetch first if it only exists on the remote.",
                context: new { baselineRef, gitError = refErr.Trim() });
        }

        var (lsOk, lsOut, lsErr) = await RunGitAsync(
            repoRoot, new[] { "ls-tree", "-r", "--name-only", "-z", baselineRef }, cancellationToken);
        if (!lsOk)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"git ls-tree failed for '{baselineRef}'",
                context: new { baselineRef, gitError = lsErr.Trim() });
        }
        var baselineRepoFiles = lsOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        var breakingRaw = new List<(string ChangeKind, string Signature, string? BaselineSignature, object? Location, string Project, string? TargetFramework)>();
        var addedRaw = new List<(string Signature, object? Location, string Project, string? TargetFramework)>();
        var projectsCompared = new List<object>();
        var comparedProjects = new HashSet<LogicalProject>();
        // The project file text at the ref, read once per project file.
        var projectXmlAtRef = new Dictionary<string, string?>(LoadFidelityClassifier.PathComparer);
        using var evaluator = new MSBuildProjectEvaluator();

        foreach (var project in projects)
        {
            var currentCompilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (currentCompilation == null) continue;
            var logical = model.Of(project);
            var targetFramework = LogicalProject.TargetFrameworkOf(project);

            if (!projectXmlAtRef.TryGetValue(project.FilePath!, out var xml))
            {
                var relativeProjectFile = Path.GetRelativePath(repoRoot, project.FilePath!).Replace('\\', '/');
                var blobs = await ReadBaselineBlobsAsync(repoRoot, baselineRef, new[] { relativeProjectFile }, cancellationToken);
                xml = blobs.GetValueOrDefault(relativeProjectFile);
                projectXmlAtRef[project.FilePath!] = xml;
            }

            List<string> baselineFiles;
            try
            {
                baselineFiles = BaselineCompileFiles(evaluator, project.FilePath!, xml, targetFramework, repoRoot, baselineRepoFiles);
            }
            catch (Microsoft.Build.Exceptions.InvalidProjectFileException ex)
            {
                return CreateErrorResponse(
                    ErrorCodes.AnalysisFailed,
                    $"The project file of '{logical.Name}' at '{baselineRef}' cannot be evaluated: {ex.Message}",
                    hint: "diff_api_surface evaluates the project file as it read at the baseline ref to know which files it compiled.",
                    context: new { baselineRef, projectName = logical.Name });
            }

            var parseOptions = project.ParseOptions as CSharpParseOptions
                ?? new CSharpParseOptions(LanguageVersion.Latest);
            var baselineTrees = new List<SyntaxTree>(baselineFiles.Count);
            // One `git cat-file --batch` reads every baseline blob in a single
            // subprocess (vs. a `git show` per file) and decodes as UTF-8.
            var baselineBlobs = await ReadBaselineBlobsAsync(repoRoot, baselineRef, baselineFiles, cancellationToken);
            foreach (var file in baselineFiles)
            {
                if (!baselineBlobs.TryGetValue(file, out var content)) continue; // symlink/submodule entry
                baselineTrees.Add(CSharpSyntaxTree.ParseText(content, parseOptions,
                    path: Path.GetFullPath(Path.Combine(repoRoot, file)), cancellationToken: cancellationToken));
            }
            var baselineGitFileCount = baselineTrees.Count;

            // Build-generated documents (GlobalUsings.g.cs, AssemblyInfo.cs) are not in
            // git, but without the implicit global usings the baseline trees cannot
            // resolve common types and every such symbol would render as a bogus
            // signature change. The current build's generated documents stand in.
            foreach (var document in BuildGeneratedDocuments(evaluator, project, targetFramework))
            {
                var text = await document.GetTextAsync(cancellationToken);
                baselineTrees.Add(CSharpSyntaxTree.ParseText(text.ToString(), parseOptions, path: document.FilePath!,
                    cancellationToken: cancellationToken));
            }

            Compilation baselineCompilation = CSharpCompilation.Create(
                $"{project.AssemblyName}.Baseline",
                baselineTrees,
                currentCompilation.References,
                (CSharpCompilationOptions)currentCompilation.Options);
            baselineCompilation = RunProjectGenerators(project, baselineCompilation, cancellationToken);

            var baselineSurface = SnapshotApiSurface(baselineCompilation);
            var currentSurface = SnapshotApiSurface(currentCompilation);
            var (projectBreaking, projectAdded) = DiffApiSurfaces(baselineSurface, currentSurface);

            // Locations are reported on the CURRENT side only: a removed entry's
            // path points into the baseline tree (a repo-relative git path at the
            // ref), which no longer corresponds to anything on disk.
            breakingRaw.AddRange(projectBreaking.Select(b => (
                b.changeKind,
                b.signature,
                b.baselineSignature,
                b.changeKind == "changed" && b.entry is { FilePath: not null, Line: not null }
                    ? (object?)new { filePath = FormatPath(b.entry.FilePath), line = b.entry.Line }
                    : null,
                logical.Name,
                targetFramework)));
            addedRaw.AddRange(projectAdded.Select(a => (
                a.Signature,
                a is { FilePath: not null, Line: not null }
                    ? (object?)new { filePath = FormatPath(a.FilePath), line = a.Line }
                    : null,
                logical.Name,
                targetFramework)));
            if (comparedProjects.Add(logical))
            {
                projectsCompared.Add(new
                {
                    project = logical.Name,
                    targetFrameworks = logical.IsMultiTargeted ? logical.TargetFrameworks : null,
                    baselineFileCount = baselineGitFileCount,
                    currentSurfaceCount = currentSurface.Count,
                    baselineSurfaceCount = baselineSurface.Count
                });
            }
        }

        var breaking = TargetFrameworkDeduplication.By(
                breakingRaw, b => (b.Project, b.ChangeKind, b.Signature, b.BaselineSignature ?? ""),
                EqualityComparer<(string, string, string, string)>.Default, b => b.TargetFramework)
            .OrderBy(b => b.Item.Signature, StringComparer.Ordinal)
            .Select(b => new
            {
                changeKind = b.Item.ChangeKind,
                signature = b.Item.Signature,
                baselineSignature = b.Item.BaselineSignature,
                location = b.Item.Location,
                project = b.Item.Project,
                targetFrameworks = FrameworksOrNull(b.TargetFrameworks)
            })
            .ToList();
        var added = TargetFrameworkDeduplication.By(
                addedRaw, a => (a.Project, a.Signature), EqualityComparer<(string, string)>.Default, a => a.TargetFramework)
            .OrderBy(a => a.Item.Signature, StringComparer.Ordinal)
            .Select(a => new
            {
                signature = a.Item.Signature,
                location = a.Item.Location,
                project = a.Item.Project,
                targetFrameworks = FrameworksOrNull(a.TargetFrameworks)
            })
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                baselineRef,
                projectName,
                projectsCompared,
                breakingCount = breaking.Count,
                addedCount = added.Count,
                breaking,
                added
            },
            ResultCounts.Complete(breaking.Count + added.Count),
            suggestedNextTools: breaking.Count > 0
                ? new[]
                {
                    "Review each breaking entry; a major-version bump (or compatibility shims) is required to ship them",
                    "find_references on changed symbols to gauge caller impact"
                }
                : new[] { "No breaking changes: the release can ship as a minor/patch version for API purposes" });
    }

    // The compilation with the project's source generators run on it, exactly as the
    // generator-aware current compilation runs them; unchanged when the project has none.
    private static Compilation RunProjectGenerators(Project project, Compilation compilation, CancellationToken cancellationToken)
    {
        var generators = project.AnalyzerReferences
            .SelectMany(r => r.GetGenerators(LanguageNames.CSharp))
            .ToImmutableArray();
        if (generators.IsEmpty) return compilation;
        CreateGeneratorDriver(project, generators).RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _, cancellationToken);
        return updated;
    }

    // The repo-relative .cs paths of the ref's tree that the project compiled at the ref:
    // the Compile items of the project file as it read there, evaluated by MSBuild (its
    // imports resolve against the files on disk now). Empty when the project file did not
    // exist at the ref.
    private static List<string> BaselineCompileFiles(
        MSBuildProjectEvaluator evaluator, string projectFile, string? projectXmlAtRef, string? targetFramework,
        string repoRoot, IEnumerable<string> baselineRepoFiles)
    {
        if (projectXmlAtRef == null) return new List<string>();
        var project = evaluator.EvaluateText(projectFile, projectXmlAtRef, targetFramework);
        return baselineRepoFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => MSBuildProjectEvaluator.CompileIncludes(project, Path.GetFullPath(Path.Combine(repoRoot, f))))
            .ToList();
    }

    // The current project's documents the build generates rather than the project file
    // listing them (implicit global usings, assembly info): documents that are not Compile
    // items of the project file evaluated now. They stand in for the baseline's own, which
    // git does not hold. None when the project file cannot be evaluated.
    private static List<Document> BuildGeneratedDocuments(MSBuildProjectEvaluator evaluator, Project project, string? targetFramework)
    {
        try
        {
            var evaluated = evaluator.Evaluate(project.FilePath!, targetFramework);
            return project.Documents
                .Where(d => d.FilePath != null && !MSBuildProjectEvaluator.CompileIncludes(evaluated, d.FilePath))
                .ToList();
        }
        catch (Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            return new List<Document>();
        }
    }

    // Snapshot of a compilation's public API: every public type (plus nested
    // public/protected types) and their public/protected members, keyed by
    // DocumentationCommentId with a display signature that pins accessibility,
    // modifiers, parameters (names, defaults), return type, and constraints.
    internal static List<ApiSurfaceEntry> SnapshotApiSurface(Compilation compilation)
    {
        var entries = new List<ApiSurfaceEntry>();
        WalkNamespace(compilation.Assembly.GlobalNamespace);
        return entries;

        void WalkNamespace(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                if (type.DeclaredAccessibility == Accessibility.Public) AddType(type, containingTypeKey: null);
            }
            foreach (var child in ns.GetNamespaceMembers()) WalkNamespace(child);
        }

        void AddType(INamedTypeSymbol type, string? containingTypeKey)
        {
            var typeKey = KeyFor(type);
            entries.Add(new ApiSurfaceEntry(
                typeKey,
                $"{AccessibilityText(type.DeclaredAccessibility)} {type.ToDisplayString(ApiSignatureFormat)}",
                containingTypeKey,
                SourcePathOf(type),
                SourceLineOf(type)));

            // A delegate's API is fully captured by its own display signature.
            if (type.TypeKind == TypeKind.Delegate) return;

            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;

                if (member is INamedTypeSymbol nested)
                {
                    if (IsMemberSurface(nested.DeclaredAccessibility, type)) AddType(nested, typeKey);
                    continue;
                }

                if (!IsMemberSurface(member.DeclaredAccessibility, type)) continue;
                if (member is IMethodSymbol method && method.MethodKind is
                    MethodKind.PropertyGet or MethodKind.PropertySet or
                    MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise or
                    MethodKind.StaticConstructor or MethodKind.Destructor)
                {
                    continue;
                }

                entries.Add(new ApiSurfaceEntry(
                    KeyFor(member),
                    member.ToDisplayString(ApiSignatureFormat),
                    typeKey,
                    SourcePathOf(member),
                    SourceLineOf(member)));
            }
        }
    }

    // Diff two snapshots. Members of a wholly removed (or added) type collapse
    // into the single type entry so one deleted class is one finding, not one
    // per member. A key declared more than once in one snapshot (a compilation with
    // duplicate declarations) is compared once, by its first declaration.
    internal static (
        List<(string changeKind, string signature, string? baselineSignature, ApiSurfaceEntry entry)> breaking,
        List<ApiSurfaceEntry> added)
        DiffApiSurfaces(List<ApiSurfaceEntry> baseline, List<ApiSurfaceEntry> current)
    {
        baseline = baseline.DistinctBy(e => e.Key, StringComparer.Ordinal).ToList();
        current = current.DistinctBy(e => e.Key, StringComparer.Ordinal).ToList();
        var baselineByKey = baseline.ToDictionary(e => e.Key, e => e, StringComparer.Ordinal);
        var currentByKey = current.ToDictionary(e => e.Key, e => e, StringComparer.Ordinal);

        var removedEntries = baseline.Where(e => !currentByKey.ContainsKey(e.Key)).ToList();
        var removedTypeKeys = removedEntries
            .Where(e => e.Key.StartsWith("T:", StringComparison.Ordinal))
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal);

        var breaking = new List<(string changeKind, string signature, string? baselineSignature, ApiSurfaceEntry entry)>();
        foreach (var entry in removedEntries)
        {
            if (entry.ContainingTypeKey != null && removedTypeKeys.Contains(entry.ContainingTypeKey)) continue;
            breaking.Add(("removed", entry.Signature, null, entry));
        }

        foreach (var entry in current)
        {
            if (baselineByKey.TryGetValue(entry.Key, out var baselineEntry)
                && !string.Equals(baselineEntry.Signature, entry.Signature, StringComparison.Ordinal))
            {
                breaking.Add(("changed", entry.Signature, baselineEntry.Signature, entry));
            }
        }

        var addedEntries = current.Where(e => !baselineByKey.ContainsKey(e.Key)).ToList();
        var addedTypeKeys = addedEntries
            .Where(e => e.Key.StartsWith("T:", StringComparison.Ordinal))
            .Select(e => e.Key)
            .ToHashSet(StringComparer.Ordinal);
        var added = addedEntries
            .Where(e => e.ContainingTypeKey == null || !addedTypeKeys.Contains(e.ContainingTypeKey))
            .OrderBy(e => e.Signature, StringComparer.Ordinal)
            .ToList();

        return (breaking.OrderBy(b => b.signature, StringComparer.Ordinal).ToList(), added);
    }

    private static readonly SymbolDisplayFormat ApiSignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
            | SymbolDisplayGenericsOptions.IncludeTypeConstraints
            | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeRef
            | SymbolDisplayMemberOptions.IncludeExplicitInterface
            | SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeModifiers
            | SymbolDisplayMemberOptions.IncludeConstantValue,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword | SymbolDisplayKindOptions.IncludeMemberKeyword,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeDefaultValue,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            // Nullable annotations are part of the public contract: a parameter or
            // return changing from string to string? is a breaking change. The baseline
            // compilation reuses the current compilation's options, so the nullable
            // context is consistent across both surfaces.
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // Whether a member of the given containing type is part of the consumable API
    // surface. protected / protected internal members are reachable only by a
    // subclass, so they count only when the containing type can be subclassed —
    // never on a sealed type.
    private static bool IsMemberSurface(Accessibility accessibility, INamedTypeSymbol containingType)
    {
        if (accessibility == Accessibility.Public) return true;
        if (accessibility is Accessibility.Protected or Accessibility.ProtectedOrInternal)
            return !containingType.IsSealed;
        return false;
    }

    private static string AccessibilityText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        _ => accessibility.ToString().ToLowerInvariant()
    };

    private static string KeyFor(ISymbol symbol)
    {
        var id = DocumentationCommentId.CreateDeclarationId(symbol);
        if (id == null) return symbol.ToDisplayString(ApiSignatureFormat);

        // CreateDeclarationId appends "~ReturnType" to method ids. A return-type
        // change must classify as CHANGED (same identity, different signature),
        // so the suffix is stripped — except for conversion operators, whose
        // identity legitimately includes the return type.
        if (symbol is IMethodSymbol { MethodKind: not MethodKind.Conversion })
        {
            var tilde = id.LastIndexOf('~');
            if (tilde >= 0) id = id[..tilde];
        }
        return id;
    }

    private static string? SourcePathOf(ISymbol symbol) =>
        symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath is { Length: > 0 } path
            ? NormalizePath(path)
            : null;

    private static int? SourceLineOf(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return location?.GetLineSpan().StartLinePosition.Line;
    }

    // Reads every requested blob (`<ref>:<file>`) from one `git cat-file --batch`
    // subprocess. Responses come back in request order as
    // `<oid> blob <size>\n<size bytes>\n`, or `<request> missing\n`. Content is
    // read at the byte level and decoded UTF-8 so it is not corrupted by the
    // console code page.
    private static async Task<Dictionary<string, string>> ReadBaselineBlobsAsync(
        string repoRoot, string baselineRef, IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (files.Count == 0) return result;

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("cat-file");
        startInfo.ArgumentList.Add("--batch");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git");

        try
        {
            // Feed every request on a background task while the responses are read
            // below. Writing all of stdin before reading any stdout deadlocks once
            // git's stdout pipe fills (it blocks writing output, we block writing
            // input). Write-side IO/cancellation failures are swallowed here because
            // the read side is authoritative — a dead git surfaces as a short read.
            var writeStdin = Task.Run(async () =>
            {
                try
                {
                    foreach (var file in files)
                    {
                        await process.StandardInput.WriteAsync($"{baselineRef}:{file}\n".AsMemory(), cancellationToken);
                    }
                    process.StandardInput.Close();
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }, cancellationToken);

            // Drain stderr concurrently: it is redirected but otherwise unread, so a
            // full stderr pipe would block git mid-write and hang the stdout read.
            var drainStderr = process.StandardError.ReadToEndAsync(cancellationToken);

            var stream = process.StandardOutput.BaseStream;
            var newline = new byte[1];
            foreach (var file in files)
            {
                var header = await ReadStreamLineAsync(stream, cancellationToken);
                if (header == null) break;

                // "<oid> blob <size>" on success; "<request> missing" / "<request> ambiguous" otherwise.
                var parts = header.Split(' ');
                if (parts.Length < 3 || !int.TryParse(parts[2], out var size)) continue;

                var content = new byte[size];
                await stream.ReadExactlyAsync(content, cancellationToken);
                await stream.ReadExactlyAsync(newline, cancellationToken); // trailing '\n'
                result[file] = Encoding.UTF8.GetString(content);
            }

            await writeStdin;
            await drainStderr;
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Don't leak the subprocess when the call is cancelled mid-read.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw;
        }
        return result;
    }

    // Reads bytes up to (and consuming) the next '\n', UTF-8 decoding the line.
    // Returns null at end of stream with nothing buffered.
    private static async Task<string?> ReadStreamLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
    }

    // Runs git with redirected output. Arguments go through ArgumentList so a
    // ref containing spaces or metacharacters cannot smuggle extra arguments.
    private static async Task<(bool ok, string stdout, string stderr)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Decode git's output as UTF-8 regardless of the console code page,
            // so non-ASCII source and file paths don't come back mojibaked.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            throw;
        }

        return (process.ExitCode == 0, await stdoutTask, await stderrTask);
    }
}
