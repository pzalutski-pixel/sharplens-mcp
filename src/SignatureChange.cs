namespace SharpLensMcp;

/// <summary>
/// Represents a single change to a method signature.
/// </summary>
public class SignatureChange
{
    /// <summary>
    /// Action: "add", "remove", "rename", "reorder"
    /// </summary>
    public string Action { get; set; } = "";

    /// <summary>
    /// Parameter name (for remove, rename) or new parameter name (for add)
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Parameter type (for add)
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// New name (for rename)
    /// </summary>
    public string? NewName { get; set; }

    /// <summary>
    /// Default value (for add)
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Position to insert (for add), -1 means end
    /// </summary>
    public int? Position { get; set; }

    /// <summary>
    /// New parameter order (for reorder)
    /// </summary>
    public List<string>? Order { get; set; }
}
