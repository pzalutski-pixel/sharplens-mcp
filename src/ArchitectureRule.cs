namespace SharpLensMcp;

/// <summary>
/// One dependency rule for check_architecture: types whose namespace (or
/// project, per the tool's level) matches <see cref="From"/> must not depend
/// on types matching <see cref="Deny"/>. Both are glob patterns (* and ?).
/// </summary>
public sealed class ArchitectureRule
{
    public string From { get; set; } = "";
    public string Deny { get; set; } = "";
}
