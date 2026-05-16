namespace SharpLensMcp.Tests.Fixtures;

// Hierarchy for find_implementations / get_derived_types tests.
//   IShapeFixture <- FixtureCircle, FixtureRectangle
//                    FixtureRectangle <- FixtureSquare (transitive)
// find_implementations on IShapeFixture must return FixtureCircle, FixtureRectangle, AND FixtureSquare.
//
// Names are deliberately fixture-prefixed because plain `Rectangle`/`Circle` collide with
// System.Drawing types and FindTypeByName picks the wrong one.

public interface IShapeFixture
{
    double Area { get; }
}

public class FixtureCircle : IShapeFixture
{
    public double Radius { get; init; }
    public double Area => System.Math.PI * Radius * Radius;
}

public class FixtureRectangle : IShapeFixture
{
    public double Width { get; init; }
    public double Height { get; init; }
    public double Area => Width * Height;
}

public class FixtureSquare : FixtureRectangle
{
    public double Side
    {
        get => Width;
        init { Width = value; Height = value; }
    }
}
