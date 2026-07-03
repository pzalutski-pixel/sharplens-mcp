namespace SharpLensMcp.Tests.Fixtures;

// Targets for resolve_stack_trace. CaptureTrace produces a REAL exception
// trace at test runtime (the fixture is compiled into the test assembly, so
// the PDB paths point at this very file); the local function exists for the
// mangled local-function frame shape.
public class StackTraceSource
{
    public static string CaptureTrace()
    {
        try
        {
            Trigger();
        }
        catch (InvalidOperationException ex)
        {
            return ex.ToString();
        }
        return "";
    }

    private static void Trigger()
    {
        ThrowingAsync().GetAwaiter().GetResult();
    }

    private static async Task ThrowingAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("stack-trace-fixture");
    }

    public static string UseHelper()
    {
        return Stamp();

        static string Stamp() => "stamp";
    }
}

// Overloaded targets for resolve_stack_trace arity matching. Combine has a
// two-scalar overload and a single-tuple overload, so a frame whose only
// parameter type carries a top-level comma must resolve to the tuple overload
// (arity 1), not be miscounted as two parameters. The constructor exercises the
// .ctor frame shape.
public class StackTraceOverloads
{
    public StackTraceOverloads(string label)
    {
        Label = label;
    }

    public string Label { get; }

    public int Combine(int a, int b) => a + b;

    public int Combine((int, int) pair) => pair.Item1 + pair.Item2;

    // Generic method: a frame renders it as Echo[T], whose arity marker must be
    // stripped (and the type-parameter count used) before the member lookup.
    public T Echo<T>(T value) => value;

    // Two overloads with no distinguishing arity from a file-less mangled frame
    // (async/lambda/local carry no parameter count) — resolution is ambiguous.
    public void Ambiguous(int x) { }
    public void Ambiguous(string y) { }
}

// Nested types reached through demangled async frames: the '+' nesting
// separator must survive so Outer+Inner resolves by metadata name.
public class StackTraceOuter
{
    public class StackTraceInner
    {
        public static async Task NestedAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("nested");
        }
    }
}
