namespace SharpLensMcp.Tests.Fixtures;

// Fixture for move_type_to_file's origin-name collision path: the file is
// named after ExactTypeNameHost, so moving that type must be refused with the
// hint to move the OTHER type out instead.

public class ExactTypeNameHost
{
    public int HostValue;
}

public class ExactTypeNameCompanion
{
    public int CompanionValue;
}
