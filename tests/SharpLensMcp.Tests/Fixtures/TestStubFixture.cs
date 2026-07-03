namespace SharpLensMcp.Tests.Fixtures;

// Targets for generate_test_stub: a plain sync method, an async method (must
// produce an async Task test), and an interface-typed parameter (must produce
// the TODO-marked placeholder).
public class TestStubTarget
{
    public int Add(int left, int right) => left + right;

    public async Task<string> FetchAsync(string key)
    {
        await Task.Yield();
        return key;
    }

    public bool Validate(IComparable input) => input != null;

    // A ref parameter: the stub must declare a writable local and pass it by ref.
    public void Accumulate(ref int total) => total += 1;

    // A custom awaitable (not Task/ValueTask): the stub must still go async/await.
    public CustomAwaitable DoCustomAsync() => new CustomAwaitable();

    // Parameters named with reserved keywords: the generated named arguments must
    // be @-escaped (`@class:`/`@event:`) or the call won't compile.
    public int Reserved(int @class, int @event) => @class + @event;

    // A generic method: the stub must emit explicit type arguments or CS0411.
    public T Echo<T>(T value) => value;

    // A generic method constrained to a type in a namespace the stub does not import:
    // the type argument must be fully qualified or the stub fails with CS0246.
    public T Pick<T>() where T : global::SharpLensMcp.Tests.ForeignConstraints.IForeignConstraint => default!;

    // A ref parameter whose type is in a namespace the stub does not import: the ref-local
    // declaration must fully qualify the type or the stub fails with CS0246.
    public void Mutate(ref global::SharpLensMcp.Tests.ForeignConstraints.IForeignConstraint x) { }
}

public sealed class CustomAwaiter : System.Runtime.CompilerServices.INotifyCompletion
{
    public bool IsCompleted => true;
    public void OnCompleted(Action continuation) => continuation();
    public int GetResult() => 0;
}

public sealed class CustomAwaitable
{
    public CustomAwaiter GetAwaiter() => new CustomAwaiter();
}
