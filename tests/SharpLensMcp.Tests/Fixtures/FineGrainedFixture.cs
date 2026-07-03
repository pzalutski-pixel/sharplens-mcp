namespace SharpLensMcp.Tests.Fixtures;

// One fixture exercising all four fine-grained search shapes against
// FineGrainedWidget (construction/patterns) and FineGrainedException (throw/catch).
public class FineGrainedWidget { }

public class FineGrainedException : System.Exception { }

public class FineGrainedHost
{
    public object Make() => new FineGrainedWidget();

    public object MakeImplicit()
    {
        FineGrainedWidget widget = new();
        return widget;
    }

    public bool IsWidget(object o) => o is FineGrainedWidget;

    public FineGrainedWidget? AsWidget(object o) => o as FineGrainedWidget;

    public string Describe(object o) => o switch
    {
        FineGrainedWidget => "widget",
        _ => "other"
    };

    public void Thrower() => throw new FineGrainedException();

    public void Catcher()
    {
        try
        {
            Thrower();
        }
        catch (FineGrainedException)
        {
        }
    }

    public void BaseCatcher()
    {
        try
        {
            Thrower();
        }
        catch (System.Exception)
        {
        }
    }
}
