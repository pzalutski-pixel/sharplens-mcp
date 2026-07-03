namespace SharpLensMcp.Tests.Fixtures;

// Targets for find_similar_code: Alpha and Beta are token-identical modulo
// identifier names, so their normalized fingerprints match exactly (score 1.0).
// CompletelyDifferent shares no structure with them. ClampAlpha/ClampBeta are
// the same idea for expression-bodied members — they prove an expression-bodied
// method is reachable as a CANDIDATE, not only as a query.
public class SimilarCodeHost
{
    public int ClampAlpha(int value, int low, int high) =>
        value < low ? low : value > high ? high : value;

    public int ClampBeta(int input, int floor, int ceiling) =>
        input < floor ? floor : input > ceiling ? ceiling : input;

    public int SumOfSquaresAlpha(int[] values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total += value * value;
        }
        return total;
    }

    public int SumOfCubesBeta(int[] numbers)
    {
        var accumulator = 0;
        foreach (var number in numbers)
        {
            accumulator += number * number;
        }
        return accumulator;
    }

    public string CompletelyDifferent(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "empty";
        }
        var parts = input.Split(',');
        return string.Join(";", parts);
    }
}

// Structural twins of SumOfSquaresAlpha in member kinds beyond a plain method:
// a constructor (BaseMethodDeclaration), a local function, and a property
// accessor — all must be reachable as candidates.
public class SimilarCodeMemberKinds
{
    private readonly int[] _values = System.Array.Empty<int>();
    private int _result;

    public SimilarCodeMemberKinds(int[] seed)
    {
        var total = 0;
        foreach (var value in seed)
        {
            total += value * value;
        }
        _result = total;
    }

    public int Compute()
    {
        int Inner(int[] data)
        {
            var total = 0;
            foreach (var value in data)
            {
                total += value * value;
            }
            return total;
        }
        return Inner(_values) + _result;
    }

    public int Squared
    {
        get
        {
            var total = 0;
            foreach (var value in _values)
            {
                total += value * value;
            }
            return total;
        }
    }
}

// A partial method whose body lives in the second part: the query must
// fingerprint the implementing declaration, not the bodyless one at reference [0].
public partial class SimilarCodePartial
{
    public partial int PartialTwin(int[] values);
}

public partial class SimilarCodePartial
{
    public partial int PartialTwin(int[] values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total += value * value;
        }
        return total;
    }
}
