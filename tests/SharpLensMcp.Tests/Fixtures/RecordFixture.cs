namespace SharpLensMcp.Tests.Fixtures;

// Exercises the record-vs-class type-kind reporting fix.
// Roslyn reports `record class` as TypeKind.Class with IsRecord=true,
// and `record struct` as TypeKind.Struct with IsRecord=true. Tools must
// distinguish via the GetTypeKindString helper.

public record Person(string Name, int Age);

public record struct Point2D(int X, int Y);

// Plain class — control case (typeKind must remain "Class").
public class PlainCustomer
{
    public string Name { get; init; } = "";
}

// Plain struct — control case (typeKind must remain "Struct").
public struct PlainPoint
{
    public int X;
    public int Y;
}
