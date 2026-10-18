using FluentAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SharpLensMcp.Tests;

// The load classification as a pure function over constructed inputs: no MSBuild, no
// network, no NuGet advisory database. Paths are absolute under a fixed root.
public class LoadFidelityClassifierTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "classifier-fixture");
    private static readonly string Lib = Path.Combine(Root, "Lib", "Lib.csproj");
    private static readonly string App = Path.Combine(Root, "App", "App.csproj");
    private static readonly string Shared = Path.Combine(Root, "Shared", "Shared.shproj");

    private static LoadedProjectState Complete(string path, string? name = null) =>
        new(path, name ?? Path.GetFileNameWithoutExtension(path), IsPlaceholder: false, MetadataReferenceCount: 150, ResolvesCoreLibrary: true);

    private static DeclaredSolutionProjects Declared(params string[] compiling) =>
        new(compiling, Array.Empty<string>());

    private static WorkspaceDiagnostic Failure(string message) => new(WorkspaceDiagnosticKind.Failure, message);

    private static readonly IReadOnlyList<RestoreLogEntry> NoRestoreLog = Array.Empty<RestoreLogEntry>();

    [Fact]
    public void RelayedMessage_ForACompletelyLoadedProject_IsAWarning_NotAFailure()
    {
        // The BuildHost keeps MSBuild's Warning severity but DiagnosticReporter relays
        // it as a Failure; the project loaded completely, so it is a warning.
        var relay = MSBuildRelayMessage.Format(Lib, $"{Lib}: (0, 0): Package 'Newtonsoft.Json' 12.0.3 has a known high severity vulnerability");

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { Complete(Lib) }, new[] { Failure(relay) }, NoRestoreLog);

        fidelity.IsPartial.Should().BeFalse();
        fidelity.Failures.Should().BeEmpty();
        fidelity.Warnings.Should().Equal(relay);
        fidelity.DeclaredProjectCount.Should().Be(1);
        fidelity.LoadedProjectCount.Should().Be(1);
    }

    [Fact]
    public void RelayedMessage_InTheInvariantEnglishForm_IsRecognized()
    {
        var relay = $"Msbuild failed when processing the file '{Lib}' with message: {Lib}: (12, 5): custom warning";

        MSBuildRelayMessage.TryParse(relay, out var projectPath, out var inner).Should().BeTrue();
        projectPath.Should().Be(Lib);
        inner.Should().Be($"{Lib}: (12, 5): custom warning");

        MSBuildRelayMessage.TryParse($"Msbuild failed when processing the file '{Lib}'", out projectPath, out inner).Should().BeTrue();
        projectPath.Should().Be(Lib);
        inner.Should().Be("");

        MSBuildRelayMessage.TryParse($"Project file not found: '{Lib}'", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void RelayedMessage_ForAPlaceholderProject_IsAFailure_AndThePlaceholderIsNamed()
    {
        var relay = MSBuildRelayMessage.Format(Lib, $"{Lib}: (1, 1): The SDK 'Does.Not.Exist' specified could not be found.");
        var placeholder = new LoadedProjectState(Lib, "Lib", IsPlaceholder: true, MetadataReferenceCount: 0, ResolvesCoreLibrary: false);

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { placeholder }, new[] { Failure(relay) }, NoRestoreLog);

        fidelity.IsPartial.Should().BeTrue();
        fidelity.Failures.Should().Equal(
            relay,
            $"Project '{Lib}' did not load: MSBuild could not evaluate it, so it is an empty placeholder with no documents.");
        fidelity.LoadedProjectCount.Should().Be(0);
    }

    [Fact]
    public void RelayedMessage_ForAProjectThatIsAbsent_IsAFailure()
    {
        var relay = MSBuildRelayMessage.Format(App, $"{App}: (0, 0): something failed");

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { Complete(Lib) }, new[] { Failure(relay) }, NoRestoreLog);

        fidelity.Failures.Should().Equal(relay);
    }

    [Fact]
    public void WorkspaceOriginatedFailure_StaysAFailure_AndWorkspaceWarningStaysAWarning()
    {
        var notFound = $"Project file not found: '{Path.Combine(Root, "Missing", "Missing.csproj")}'";
        var duplicate = new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, "Duplicate source file 'x.cs' in project 'y'");

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { Complete(Lib) }, new[] { Failure(notFound), duplicate }, NoRestoreLog);

        fidelity.Failures.Should().Equal(notFound);
        fidelity.Warnings.Should().Equal(duplicate.Message);
    }

    [Fact]
    public void SharedProject_IsNotDeclaredAsCompiling_AndItsCannotOpenMessageIsAWarning()
    {
        // A .shproj has nothing of its own to load (its files compile into the
        // importing projects), so a healthy solution containing one is not partial.
        var cannotOpen = $"Cannot open project '{Shared}' because the file extension '.shproj' is not associated with a language.";
        var declared = new DeclaredSolutionProjects(new[] { App }, new[] { Shared });

        var fidelity = LoadFidelityClassifier.Classify(declared, new[] { Complete(App) }, new[] { Failure(cannotOpen) }, NoRestoreLog);

        fidelity.IsPartial.Should().BeFalse();
        fidelity.DeclaredProjectCount.Should().Be(1);
        fidelity.Warnings.Should().Equal(cannotOpen);
    }

    [Fact]
    public void DeclaredProjectMissing_IsAFailure_EvenWhenAMultiTargetedProjectMakesTheCountsEqual()
    {
        // Lib loads once per target framework (2 workspace projects) while App
        // silently did not load — 2 declared vs 2 loaded by count, but App is missing.
        var loaded = new[] { Complete(Lib, "Lib(net8.0)"), Complete(Lib, "Lib(netstandard2.0)") };

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib, App), loaded, Array.Empty<WorkspaceDiagnostic>(), NoRestoreLog);

        fidelity.IsPartial.Should().BeTrue();
        fidelity.Failures.Should().Equal($"Project '{App}' is declared in the solution but did not load.");
        fidelity.DeclaredProjectCount.Should().Be(2);
        fidelity.LoadedProjectCount.Should().Be(1, "one distinct project file loaded, not two");
    }

    [Fact]
    public void ProjectWithoutReferences_IsAFailureNamingRestore()
    {
        // A project whose restore output exists but whose compilation sees no metadata
        // references, with no diagnostic at all.
        var noReferences = new LoadedProjectState(Lib, "Lib", IsPlaceholder: false, MetadataReferenceCount: 0, ResolvesCoreLibrary: false);

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { noReferences }, Array.Empty<WorkspaceDiagnostic>(), NoRestoreLog);

        fidelity.Failures.Should().Equal(
            $"Project 'Lib' ({Lib}) loaded without any metadata references, so none of its code binds (not even System.Object); run 'dotnet restore' and reload.");
    }

    // An SDK-style project without project.assets.json has not been restored. Whether the SDK
    // still supplied framework references (150 here) or none at all, the failure is the one
    // not-restored message, once for the project.
    [Theory]
    [InlineData(150, true)]
    [InlineData(0, false)]
    public void UnrestoredSdkProject_IsOneNotRestoredFailure_WhateverReferencesResolved(int references, bool resolvesCoreLibrary)
    {
        var net8 = new LoadedProjectState(Lib, "Lib(net8.0)", IsPlaceholder: false, references, resolvesCoreLibrary, IsUnrestored: true);
        var net10 = new LoadedProjectState(Lib, "Lib(net10.0)", IsPlaceholder: false, references, resolvesCoreLibrary, IsUnrestored: true);

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { net8, net10 }, Array.Empty<WorkspaceDiagnostic>(), NoRestoreLog);

        fidelity.IsPartial.Should().BeTrue();
        fidelity.Failures.Should().Equal(
            $"Project '{Lib}' has not been restored: it has no project.assets.json, so its package references are missing; run 'dotnet restore' and reload.");
    }

    [Fact]
    public void ProjectWhoseReferencesLackTheCoreLibrary_IsAFailure()
    {
        var noCorlib = new LoadedProjectState(Lib, "Lib", IsPlaceholder: false, MetadataReferenceCount: 3, ResolvesCoreLibrary: false);

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { noCorlib }, Array.Empty<WorkspaceDiagnostic>(), NoRestoreLog);

        fidelity.Failures.Should().Equal(
            $"Project 'Lib' ({Lib}) loaded with 3 metadata references, but none defines System.Object, so none of its code binds; run 'dotnet restore' and reload.");
    }

    [Fact]
    public void RestoreLogError_IsAFailure_AndSupersedesItsRelayedCopy()
    {
        // A failed restore (NU1101) on a project that otherwise loaded: the relay lost the
        // Error severity and the code; the assets file keeps both.
        const string message = "Unable to find package Missing.Pkg. No packages exist with this id in source(s): nuget.org";
        var relay = MSBuildRelayMessage.Format(Lib, $"{Lib}: (0, 0): {message}");
        var restore = new[] { new RestoreLogEntry(Lib, "NU1101", "Error", message) };

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { Complete(Lib) }, new[] { Failure(relay) }, restore);

        fidelity.IsPartial.Should().BeTrue();
        fidelity.Failures.Should().Equal($"Restore NU1101 in '{Lib}': {message}");
        fidelity.Warnings.Should().BeEmpty("the relayed copy is superseded, not reported twice");
    }

    [Fact]
    public void RestoreLogWarning_IsAWarningWithItsCode_AndSupersedesItsRelayedCopy()
    {
        const string message = "Package 'Newtonsoft.Json' 12.0.3 has a known high severity vulnerability, https://github.com/advisories/GHSA-5crp-9r3c-p9vr";
        var relay = MSBuildRelayMessage.Format(Lib, $"{Lib}: (0, 0): {message}");
        var restore = new[] { new RestoreLogEntry(Lib, "NU1903", "Warning", message) };

        var fidelity = LoadFidelityClassifier.Classify(Declared(Lib), new[] { Complete(Lib) }, new[] { Failure(relay) }, restore);

        fidelity.IsPartial.Should().BeFalse();
        fidelity.Warnings.Should().Equal($"Restore NU1903 in '{Lib}': {message}");
    }

    [Fact]
    public void UnreadableSolutionFile_SkipsTheDeclaredCrossCheck()
    {
        var fidelity = LoadFidelityClassifier.Classify(null, new[] { Complete(Lib) }, Array.Empty<WorkspaceDiagnostic>(), NoRestoreLog);

        fidelity.DeclaredProjectCount.Should().BeNull();
        fidelity.IsPartial.Should().BeFalse();
    }
}
