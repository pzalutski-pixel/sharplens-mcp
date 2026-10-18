namespace SharpLensMcp;

/// <summary>
/// What MSBuildWorkspace actually produced for one loaded project (one per target framework
/// of a multi-targeted project).
/// </summary>
/// <param name="FilePath">The project file.</param>
/// <param name="Name">The workspace project name, carrying the target framework when multi-targeted.</param>
/// <param name="IsPlaceholder">
/// MSBuild evaluation failed, so the workspace substituted an empty project: no documents and
/// no output path. Nothing of the project's code is visible.
/// </param>
/// <param name="MetadataReferenceCount">References the compilation sees; 0 for an unrestored SDK project.</param>
/// <param name="ResolvesCoreLibrary">Whether those references define System.Object, so code in the project can bind.</param>
/// <param name="IsUnrestored">
/// An SDK-style project without a project.assets.json: it has not been restored, so its package
/// references are missing whatever framework references happened to resolve.
/// </param>
public sealed record LoadedProjectState(
    string FilePath,
    string Name,
    bool IsPlaceholder,
    int MetadataReferenceCount,
    bool ResolvesCoreLibrary,
    bool IsUnrestored = false);
