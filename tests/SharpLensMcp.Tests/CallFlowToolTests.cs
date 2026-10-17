using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_outgoing_calls, find_callers, get_call_graph and find_path_between over the
// CallFlowSources fixture, each behavior asserted at both levels (ToolPath): what a body
// calls is every edge the compiler emits (extension and generic calls as one edge per
// declaration, constructors, accessors, partial implementations), lambdas and local
// functions belong to their containing member, interface and virtual calls fan out to
// the in-solution implementations the receiver type can reach (flagged viaDispatch), and
// every list is in canonical order before it is capped.
public class CallFlowToolTests
{
    private static (ToolPathRunner runner, string path) Load(ToolPath toolPath)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(CallFlowSources.Flow, CallFlowSources.FileName);
        return (new ToolPathRunner(toolPath, workspace), document.FilePath!);
    }

    private static List<string> Strings(JToken array, string field) =>
        ((JArray)array).Select(item => item[field]!.Value<string>()!).ToList();

    private static Task<JToken> OutgoingAsync(ToolPathRunner runner, string path, string marker, int offset = 0, int? maxDepth = null)
    {
        var (line, column) = CallFlowSources.Locate(marker, offset);
        return runner.DataAsync("get_outgoing_calls", new { filePath = path, line, column, maxDepth },
            s => s.GetOutgoingCallsAsync(path, line, column, maxDepth));
    }

    private static Task<JObject> CallersEnvelopeAsync(ToolPathRunner runner, string path, string marker, int? maxResults = null)
    {
        var (line, column) = CallFlowSources.Locate(marker);
        return runner.CallAsync("find_callers", new { filePath = path, line, column, maxResults },
            s => s.FindCallersAsync(path, line, column, maxResults));
    }

    private static Task<JToken> GraphAsync(
        ToolPathRunner runner, string path, string marker, string direction, int maxDepth = 3, int maxNodes = 100)
    {
        var (line, column) = CallFlowSources.Locate(marker);
        return runner.DataAsync("get_call_graph", new { filePath = path, line, column, direction, maxDepth, maxNodes },
            s => s.GetCallGraphAsync(path, line, column, direction, maxDepth, maxNodes));
    }

    private static Task<JToken> PathAsync(
        ToolPathRunner runner, (string type, string member) source, (string type, string member) target,
        int maxHops = 8, (string type, string member)? mustPassThrough = null)
    {
        var must = mustPassThrough is { } checkpoint
            ? new List<MemberRef> { new(checkpoint.type, checkpoint.member) }
            : null;
        return runner.DataAsync("find_path_between", new
            {
                sources = new[] { new { typeName = source.type, memberName = source.member } },
                targets = new[] { new { typeName = target.type, memberName = target.member } },
                mustPassThrough = must?.Select(m => new { typeName = m.TypeName, memberName = m.MemberName }).ToArray(),
                maxHops
            },
            s => s.FindPathBetweenAsync(
                new List<MemberRef> { new(source.type, source.member) },
                new List<MemberRef> { new(target.type, target.member) },
                null, must, maxHops));
    }

    private static JToken Pair(JToken data) => ((JArray)data["pairs"]!).Single();

    // ---------------------------------------------------------------- get_outgoing_calls

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task OutgoingCalls_ExtensionAndGenericCalls_AreOneEdgePerDeclaration(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var data = await OutgoingAsync(runner, path, "UseGen()");

        var calls = (JArray)data["calls"]!;
        Strings(calls, "method").Should().Equal(
            new[] { "Flow.Gen.Id<T>(T)", "Flow.Ext.Twice(int)" },
            "Id(1) and Id(\"s\") are one generic declaration, 3.Twice() and Ext.Twice(4) one extension method");
        Strings(calls, "kind").Should().Equal("invocation", "invocation");
        calls.Select(c => c["viaDispatch"]!.Value<bool>()).Should().Equal(false, false);
        calls.Select(c => c["depth"]!.Value<int>()).Should().Equal(1, 1);
        calls.Select(c => c["isExternal"]!.Value<bool>()).Should().Equal(false, false);
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task OutgoingCalls_ObjectCreationAndPropertyRead_CallTheConstructorAndTheGetter(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var make = (JArray)(await OutgoingAsync(runner, path, "Make() { var w = new Worker()"))["calls"]!;
        var read = (JArray)(await OutgoingAsync(runner, path, "Read(PropHost h)"))["calls"]!;

        Strings(make, "method").Should().Equal("Flow.Worker.Worker()");
        Strings(make, "kind").Should().Equal("objectCreation");
        make[0]["location"]!["line"]!.Value<int>().Should().Be(CallFlowSources.LineOf("public Worker() {"));
        Strings(read, "method").Should().Equal("Flow.PropHost.Value.get");
        Strings(read, "kind").Should().Equal("propertyGet");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task OutgoingCalls_PartialMethod_WalksTheImplementationBody(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var data = await OutgoingAsync(runner, path, "Fire() => Hook()", maxDepth: 2);

        var calls = (JArray)data["calls"]!;
        Strings(calls, "method").Should().Equal("Flow.PartialHost.Hook()", "Flow.Target.FromPartial()");
        calls.Select(c => c["depth"]!.Value<int>()).Should().Equal(1, 2);
        calls[0]["location"]!["line"]!.Value<int>().Should().Be(CallFlowSources.LineOf("partial void Hook() {"),
            "the call binds to the definition part, but the implementation part carries the body that runs");
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse();
    }

    // A constructor without a constructor initializer calls its base type's parameterless
    // constructor (object's here), as the compiler emits it.
    [Theory]
    [InlineData(ToolPath.Service, "Worker() { Target.Deep", "Flow.Worker.Worker()", "object.Object()|Flow.Target.Deep()")]
    [InlineData(ToolPath.Dispatcher, "Worker() { Target.Deep", "Flow.Worker.Worker()", "object.Object()|Flow.Target.Deep()")]
    [InlineData(ToolPath.Service, "get { Target.FromGetter", "Flow.PropHost.Value.get", "Flow.Target.FromGetter()")]
    [InlineData(ToolPath.Dispatcher, "get { Target.FromGetter", "Flow.PropHost.Value.get", "Flow.Target.FromGetter()")]
    [InlineData(ToolPath.Service, "operator +", "Flow.Money.operator +(Flow.Money, Flow.Money)", "Flow.Target.Hit()")]
    [InlineData(ToolPath.Dispatcher, "operator +", "Flow.Money.operator +(Flow.Money, Flow.Money)", "Flow.Target.Hit()")]
    [InlineData(ToolPath.Service, "~Money()", "Flow.Money.~Money()", "Flow.Target.Hit2()")]
    [InlineData(ToolPath.Dispatcher, "~Money()", "Flow.Money.~Money()", "Flow.Target.Hit2()")]
    public async Task OutgoingCalls_AtAConstructorAccessorOperatorOrFinalizer_AnalyzesThatMember(
        ToolPath toolPath, string marker, string member, string callees)
    {
        var (runner, path) = Load(toolPath);

        var data = await OutgoingAsync(runner, path, marker);

        data["method"]!.Value<string>().Should().Be(member);
        Strings(data["calls"]!, "method").Should().Equal(callees.Split('|'));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task OutgoingCalls_InterfaceCall_FansOutToTheImplementationsFlaggedViaDispatch(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var calls = (JArray)(await OutgoingAsync(runner, path, "Use(IShape s)"))["calls"]!;

        Strings(calls, "method").Should().Equal("Flow.IShape.Area()", "Flow.Circle.Area()", "Flow.Sq.Area()");
        calls.Select(c => c["viaDispatch"]!.Value<bool>()).Should().Equal(new[] { false, true, true },
            "the interface member is the call as written; the implementations are what may run behind it");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task OutgoingCalls_VirtualCallOnASealedReceiver_DoesNotFanOutToUnrelatedOverrides(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var calls = (JArray)(await OutgoingAsync(runner, path, "Show(Plain p)"))["calls"]!;

        Strings(calls, "method").Should().Equal(new[] { "object.ToString()" },
            "Plain is sealed and does not override ToString, so Noisy.ToString can never run behind p.ToString()");
        calls[0]["viaDispatch"]!.Value<bool>().Should().BeFalse();
        calls[0]["isExternal"]!.Value<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task OutgoingCalls_DepthBoundary_CountsAnAccessorWithCalleesAsTruncation(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var depth1 = await OutgoingAsync(runner, path, "X() { Y(); }", maxDepth: 1);
        var depth3 = await OutgoingAsync(runner, path, "X() { Y(); }", maxDepth: 3);

        Strings(depth1["calls"]!, "method").Should().Equal("Flow.DepthHost.Y()");
        depth1["truncatedByDepth"]!.Value<bool>().Should().BeTrue("Y reads P, whose getter calls Target.Deep");
        Strings(depth3["calls"]!, "method").Should().Equal(
            "Flow.DepthHost.Y()", "Flow.DepthHost.P.get", "Flow.Target.Deep()");
        ((JArray)depth3["calls"]!).Select(c => c["depth"]!.Value<int>()).Should().Equal(1, 2, 3);
        depth3["truncatedByDepth"]!.Value<bool>().Should().BeFalse();
    }

    // ---------------------------------------------------------------- find_callers

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task FindCallers_GetterInitializerAndLambdaCallers_AreTheirMembers_InLocationOrder(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var envelope = await CallersEnvelopeAsync(runner, path, "ForCg() => 0");

        var callers = (JArray)envelope["data"]!["callers"]!;
        callers.Select(c => c["callingSymbol"]!["name"]!.Value<string>()).Should().Equal(
            new[] { "get_P", ".cctor", "L", "Direct" },
            "the expression-bodied getter, the static field initializer the static constructor runs, " +
            "the method containing the lambda, and the direct caller - in location order");
        var lines = CallFlowSources.Flow.Split('\n');
        foreach (var (caller, marker) in callers.Zip(new[]
                 {
                     "public int P => Target.ForCg()", "static int F = Target.ForCg()",
                     "public void L()", "public void Direct()"
                 }))
        {
            var line = CallFlowSources.LineOf(marker);
            caller["location"]!["line"]!.Value<int>().Should().Be(line);
            caller["location"]!["column"]!.Value<int>().Should().Be(lines[line].IndexOf("ForCg", StringComparison.Ordinal));
            caller["viaDispatch"]!.Value<bool>().Should().BeFalse();
            caller["inGeneratedCode"]!.Value<bool>().Should().BeFalse();
        }
        envelope["meta"]!["totalCount"]!.Value<int>().Should().Be(4);
        envelope["meta"]!["truncated"]!.Value<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task FindCallers_Cap_KeepsTheFirstCallersInCanonicalOrder(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var envelope = await CallersEnvelopeAsync(runner, path, "ForCg() => 0", maxResults: 2);

        ((JArray)envelope["data"]!["callers"]!).Select(c => c["callingSymbol"]!["name"]!.Value<string>())
            .Should().Equal("get_P", ".cctor");
        envelope["meta"]!["totalCount"]!.Value<int>().Should().Be(4);
        envelope["meta"]!["returnedCount"]!.Value<int>().Should().Be(2);
        envelope["meta"]!["truncated"]!.Value<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task FindCallers_Implementation_IsCalledThroughTheInterfaceOnlyByReachingReceivers(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var sq = (JArray)(await CallersEnvelopeAsync(runner, path, "Area() { Target.InArea"))["data"]!["callers"]!;
        var noisy = (JArray)(await CallersEnvelopeAsync(runner, path, "ToString() { Target.Danger"))["data"]!["callers"]!;

        sq.Select(c => c["callingSymbol"]!["signature"]!.Value<string>()).Should().Equal("Flow.ShapeUser.Use(Flow.IShape)");
        sq[0]["viaDispatch"]!.Value<bool>().Should().BeTrue();
        sq[0]["kind"]!.Value<string>().Should().Be("invocation");
        noisy.Should().BeEmpty("the only ToString call is on the sealed Plain, which cannot be a Noisy");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task FindCallers_ConstructorAndGenericMethod_ReportEveryCallSite(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var ctor = (JArray)(await CallersEnvelopeAsync(runner, path, "Worker() { Target.Deep"))["data"]!["callers"]!;
        var id = (JArray)(await CallersEnvelopeAsync(runner, path, "Id<T>(T x)"))["data"]!["callers"]!;

        ctor.Select(c => c["callingSymbol"]!["signature"]!.Value<string>()).Should().Equal("Flow.CtorUser.Make()");
        ctor[0]["kind"]!.Value<string>().Should().Be("objectCreation");
        var useGenLine = CallFlowSources.LineOf("UseGen()");
        var useGen = CallFlowSources.Flow.Split('\n')[useGenLine];
        id.Select(c => (c["location"]!["line"]!.Value<int>(), c["location"]!["column"]!.Value<int>())).Should().Equal(
            (useGenLine, useGen.IndexOf("Id(1)", StringComparison.Ordinal)),
            (useGenLine, useGen.IndexOf("Id(\"s\")", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(ToolPath.Service, 0)]
    [InlineData(ToolPath.Dispatcher, 0)]
    [InlineData(ToolPath.Service, -1)]
    [InlineData(ToolPath.Dispatcher, -1)]
    public async Task FindCallers_NonPositiveMaxResults_IsInvalidParameter(ToolPath toolPath, int maxResults)
    {
        var (runner, path) = Load(toolPath);
        var (line, column) = CallFlowSources.Locate("ForCg() => 0");

        var error = await runner.ErrorAsync("find_callers", new { filePath = path, line, column, maxResults },
            s => s.FindCallersAsync(path, line, column, maxResults));

        error["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be($"maxResults must be between 1 and 1000 (got {maxResults})");
    }

    // ---------------------------------------------------------------- get_call_graph

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task CallGraph_Both_IsTheCallersTreeUnionTheCalleesTree(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var data = await GraphAsync(runner, path, "Root() { }", "both");

        Strings(data["nodes"]!, "fullName").Should().Equal(new[] { "Flow.Sibling.Root()", "Flow.Sibling.Caller()" },
            "Other is a callee of Root's caller, neither a caller nor a callee of Root");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task CallGraph_GenericAndExtensionCalls_AreOneNodePerDeclaration(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var data = await GraphAsync(runner, path, "UseGen()", "callees", maxDepth: 1);

        Strings(data["nodes"]!, "fullName").Should().Equal("Flow.Gen.UseGen()", "Flow.Ext.Twice(int)", "Flow.Gen.Id<T>(T)");
        ((JArray)data["edges"]!).Count.Should().Be(2);
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task CallGraph_Callers_AreGetterInitializerAndLambdaMembers_InCanonicalOrder(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var data = await GraphAsync(runner, path, "ForCg() => 0", "callers", maxDepth: 1);

        var nodes = (JArray)data["nodes"]!;
        Strings(nodes, "fullName").Should().Equal(
            "Flow.Target.ForCg()", "Flow.CgHost.CgHost()", "Flow.CgHost.Direct()", "Flow.CgHost.L()", "Flow.CgHost.P.get");
        Strings(nodes, "kind").Should().Equal("Ordinary", "StaticConstructor", "Ordinary", "Ordinary", "PropertyGet");
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse("none of the four callers has a caller of its own");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task CallGraph_InterfaceCall_DrawsDispatchEdgesToTheImplementations(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var data = await GraphAsync(runner, path, "Use(IShape s)", "callees", maxDepth: 2);

        var nodes = Strings(data["nodes"]!, "fullName");
        nodes.Should().Equal(
            "Flow.ShapeUser.Use(Flow.IShape)", "Flow.Circle.Area()", "Flow.IShape.Area()", "Flow.Sq.Area()",
            "Flow.Target.InArea()");
        var edges = ((JArray)data["edges"]!)
            .Select(e => (nodes[e["from"]!.Value<int>()], nodes[e["to"]!.Value<int>()], e["viaDispatch"]!.Value<bool>()))
            .ToList();
        edges.Should().BeEquivalentTo(new[]
        {
            ("Flow.ShapeUser.Use(Flow.IShape)", "Flow.IShape.Area()", false),
            ("Flow.ShapeUser.Use(Flow.IShape)", "Flow.Circle.Area()", true),
            ("Flow.ShapeUser.Use(Flow.IShape)", "Flow.Sq.Area()", true),
            ("Flow.Sq.Area()", "Flow.Target.InArea()", false)
        });
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task CallGraph_DepthBoundary_CountsAnAccessorWithCalleesAsTruncation(ToolPath toolPath)
    {
        var (runner, path) = Load(toolPath);

        var data = await GraphAsync(runner, path, "X() { Y(); }", "callees", maxDepth: 2);

        Strings(data["nodes"]!, "fullName").Should().Equal("Flow.DepthHost.X()", "Flow.DepthHost.Y()", "Flow.DepthHost.P.get");
        data["truncatedByDepth"]!.Value<bool>().Should().BeTrue("the P getter at the boundary calls Target.Deep");
    }

    [Theory]
    [InlineData(ToolPath.Service, -5)]
    [InlineData(ToolPath.Dispatcher, -5)]
    [InlineData(ToolPath.Service, 0)]
    [InlineData(ToolPath.Dispatcher, 0)]
    public async Task CallGraph_NonPositiveMaxNodes_IsInvalidParameter(ToolPath toolPath, int maxNodes)
    {
        var (runner, path) = Load(toolPath);
        var (line, column) = CallFlowSources.Locate("UseGen()");

        var error = await runner.ErrorAsync("get_call_graph",
            new { filePath = path, line, column, direction = "callees", maxDepth = 3, maxNodes },
            s => s.GetCallGraphAsync(path, line, column, "callees", 3, maxNodes));

        error["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be($"maxNodes must be between 1 and 10000 (got {maxNodes})");
    }

    // ---------------------------------------------------------------- find_path_between

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_ThroughAnExtensionCall_HopCountCountsEdges(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var pair = Pair(await PathAsync(runner, ("ExtUser", "UseExt"), ("Target", "Leaf"), maxHops: 2));

        pair["reachable"]!.Value<bool>().Should().BeTrue();
        Strings(pair["hops"]!, "symbol").Should().Equal(
            "Flow.ExtUser.UseExt()", "Flow.Ext.Twice(int)", "Flow.Target.Leaf(int)");
        pair["hopCount"]!.Value<int>().Should().Be(2, "maxHops and hopCount both count edges");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_ThroughAConstructorAndAGetter(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var viaCtor = Pair(await PathAsync(runner, ("CtorUser", "Make"), ("Target", "Deep")));
        var viaGetter = Pair(await PathAsync(runner, ("PropUser", "Read"), ("Target", "FromGetter")));
        var toCtor = Pair(await PathAsync(runner, ("CtorUser", "Make"), ("Worker", "Worker")));

        Strings(viaCtor["hops"]!, "symbol").Should().Equal("Flow.CtorUser.Make()", "Flow.Worker.Worker()", "Flow.Target.Deep()");
        Strings(viaGetter["hops"]!, "symbol").Should().Equal(
            "Flow.PropUser.Read(Flow.PropHost)", "Flow.PropHost.Value.get", "Flow.Target.FromGetter()");
        Strings(toCtor["hops"]!, "symbol").Should().Equal("Flow.CtorUser.Make()", "Flow.Worker.Worker()");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_LambdasAndLocalFunctions_AreTheirContainingMembers(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var lambdaA = Pair(await PathAsync(runner, ("LambdaUser", "A"), ("Target", "Hit")));
        var lambdaB = Pair(await PathAsync(runner, ("LambdaUser", "B"), ("Target", "Hit")));
        var localS1 = Pair(await PathAsync(runner, ("LocA", "S1"), ("Target", "Hit")));
        var localS2 = Pair(await PathAsync(runner, ("LocA", "S2"), ("Target", "Hit")));

        Strings(lambdaA["hops"]!, "symbol").Should().Equal("Flow.LambdaUser.A()", "Flow.Target.Hit()");
        lambdaB["reachable"]!.Value<bool>().Should().BeFalse("B's lambda calls Hit2, and A's lambda is not B's");
        lambdaB["hitHopLimit"]!.Value<bool>().Should().BeFalse();
        Strings(localS1["hops"]!, "symbol").Should().Equal("Flow.LocA.S1()", "Flow.Target.Hit()");
        localS2["reachable"]!.Value<bool>().Should().BeFalse("S2's Local is not S1's same-named Local");
        localS2["hitHopLimit"]!.Value<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_DispatchFollowsOnlyWhatTheReceiverCanReach(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var iface = Pair(await PathAsync(runner, ("ShapeUser", "Use"), ("Target", "InArea")));
        var sealedReceiver = Pair(await PathAsync(runner, ("ToStringUser", "Show"), ("Target", "Danger")));

        Strings(iface["hops"]!, "symbol").Should().Equal(
            "Flow.ShapeUser.Use(Flow.IShape)", "Flow.Sq.Area()", "Flow.Target.InArea()");
        sealedReceiver["reachable"]!.Value<bool>().Should().BeFalse();
        sealedReceiver["hitHopLimit"]!.Value<bool>().Should().BeFalse("the pair is disconnected, not over budget");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_ThroughAPartialMethodImplementation(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var pair = Pair(await PathAsync(runner, ("PartialHost", "Fire"), ("Target", "FromPartial")));

        Strings(pair["hops"]!, "symbol").Should().Equal(
            "Flow.PartialHost.Fire()", "Flow.PartialHost.Hook()", "Flow.Target.FromPartial()");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_MustPassThrough_MaxHopsBoundsTheWholePath(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var tooShort = Pair(await PathAsync(runner, ("Chain", "S"), ("Chain", "T1"), maxHops: 2, mustPassThrough: ("Chain", "C1")));
        var enough = Pair(await PathAsync(runner, ("Chain", "S"), ("Chain", "T1"), maxHops: 4, mustPassThrough: ("Chain", "C1")));

        tooShort["reachable"]!.Value<bool>().Should().BeFalse("S -> A1 -> C1 -> B1 -> T1 is four edges");
        tooShort["hitHopLimit"]!.Value<bool>().Should().BeTrue();
        tooShort["failedLeg"]!.Value<string>().Should().Be("checkpointToTarget");
        Strings(enough["hops"]!, "symbol").Should().Equal(
            "Flow.Chain.S()", "Flow.Chain.A1()", "Flow.Chain.C1()", "Flow.Chain.B1()", "Flow.Chain.T1()");
        enough["hopCount"]!.Value<int>().Should().Be(4);
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_MustPassThrough_FirstLegFailure_ReportsThatLegsFrontiers(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var pair = Pair(await PathAsync(runner, ("Chain", "S"), ("Chain", "T1"), maxHops: 1, mustPassThrough: ("Chain", "C1")));

        pair["reachable"]!.Value<bool>().Should().BeFalse();
        pair["hitHopLimit"]!.Value<bool>().Should().BeTrue();
        pair["failedLeg"]!.Value<string>().Should().Be("sourceToCheckpoint");
        pair["sourceFrontierSize"]!.Value<int>().Should().Be(2, "S and the A1 it calls");
        pair["targetFrontierSize"]!.Value<int>().Should().Be(1, "the checkpoint C1, the first leg's target");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_ShadowingEndpointType_CarriesTheNotice(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var data = await PathAsync(runner, ("Random", "Roll"), ("Target", "Hit"));

        Pair(data)["reachable"]!.Value<bool>().Should().BeTrue();
        data["notice"]!.Value<string>().Should().Be(
            "'Random' resolved to Flow.Random; also matches System.Random — pass the fully-qualified name to target it.");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PathBetween_ZeroMaxNodes_IsInvalidParameter(ToolPath toolPath)
    {
        var (runner, _) = Load(toolPath);

        var error = await runner.ErrorAsync("find_path_between",
            new
            {
                sources = new[] { new { typeName = "Chain", memberName = "S" } },
                targets = new[] { new { typeName = "Chain", memberName = "T1" } },
                maxNodes = 0
            },
            s => s.FindPathBetweenAsync(
                new List<MemberRef> { new("Chain", "S") }, new List<MemberRef> { new("Chain", "T1") }, null, null, maxNodes: 0));

        error["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("maxNodes must be between 1 and 10000 (got 0)");
    }
}
