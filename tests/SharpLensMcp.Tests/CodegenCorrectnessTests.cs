using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// The code-generation tools emit code that compiles where it lands:
//   - add_null_checks guards exactly the non-nullable reference-type parameters a caller
//     can pass null to (never an out parameter, never one whose default is null), and the
//     guard is formatted and line-broken as the document is;
//   - generate_equality_members compares each member with an operator that exists for its
//     type (EqualityComparer<T>.Default where == is not defined or not reflexive) and
//     never treats an indexer or an explicit interface implementation as a member;
//   - generate_constructor assigns through `this.` when a parameter has the member's name
//     and covers every assignable property, get-only auto-properties included, never an
//     indexer;
//   - extract_interface copies only the accessors the interface can declare and refuses
//     an invalid interface name.
public class CodegenCorrectnessTests
{
    internal const string OutsSource = """
        namespace Gen
        {
            public class Outs
            {
                public bool TryGet(string key, out string value)
                {
                    value = key;
                    return true;
                }

        #nullable disable
                public int Opt(string s = null)
                {
                    return s == null ? 0 : s.Length;
                }
        #nullable restore

                public void Empty(string name)
                {
                }
            }
        }
        """;

    private static Task<object> NullChecks(RoslynService service, string member, bool preview) =>
        service.AddNullChecksAsync(null, null, null, preview, typeName: "Gen.Outs", memberName: member);

    [Fact]
    public async Task NullChecks_SkipOutParameters_AndGuardTheRest()
    {
        var (service, workspace, document) = Load(OutsSource);

        var json = Json(await NullChecks(service, "TryGet", preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(OutsSource,
            ("        {\n            value = key;",
             "        {\n            global::System.ArgumentNullException.ThrowIfNull(key);\n\n            value = key;")));
        ((JArray)json["data"]!["parametersWithNullChecks"]!).Select(p => p.Value<string>()).Should().Equal("key");
        ((JArray)json["data"]!["skippedOut"]!).Select(p => p.Value<string>()).Should().Equal("value");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task NullChecks_SkipAParameterWhoseDefaultIsNull()
    {
        var (service, workspace, document) = Load(OutsSource);

        var json = Json(await NullChecks(service, "Opt", preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["message"]!.Value<string>().Should().Be("No reference-type parameter needs a null check");
        ((JArray)json["data"]!["skippedNullable"]!).Select(p => p.Value<string>()).Should().Equal("s");
        (await TextOf(workspace, document.Id)).Should().Be(OutsSource);
    }

    [Fact]
    public async Task NullChecks_InAnEmptyBody_LeaveNoBlankLineBeforeTheBrace()
    {
        var (service, workspace, document) = Load(OutsSource);

        var json = Json(await NullChecks(service, "Empty", preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(OutsSource,
            ("public void Empty(string name)\n        {\n        }",
             "public void Empty(string name)\n        {\n            global::System.ArgumentNullException.ThrowIfNull(name);\n        }")));
    }

    [Fact]
    public async Task NullChecksPreview_ReportsTheFileThroughTheSolutionPathFormat()
    {
        var (service, workspace, document) = Load(OutsSource);

        var json = Json(await NullChecks(service, "TryGet", preview: true));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var change = ((JArray)json["data"]!["changes"]!).Single();
        change["filePath"]!.Value<string>().Should().Be(Reported(document.FilePath!));
        change["insertAfterLine"]!.Value<int>().Should().Be(5);
        change["newCode"]!.Value<string>().Should().Be("global::System.ArgumentNullException.ThrowIfNull(key);");
        json["data"]!["generatedCode"]!.Value<string>().Should().Be("global::System.ArgumentNullException.ThrowIfNull(key);");
        (await TextOf(workspace, document.Id)).Should().Be(OutsSource);
    }

    [Fact]
    public async Task NullChecksInACrlfDocument_WriteOnlyCrlfLineBreaks()
    {
        var crlf = OutsSource.Replace("\n", "\r\n");
        var (service, workspace, document) = Load(crlf);

        var json = Json(await NullChecks(service, "TryGet", preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(crlf,
            ("        {\r\n            value = key;",
             "        {\r\n            global::System.ArgumentNullException.ThrowIfNull(key);\r\n\r\n            value = key;")));
    }

    internal const string HolderSource = """
        namespace Gen
        {
            public struct Money { public decimal Amount; }

            public class Holder
            {
                public Money M { get; set; }
                public int Count;
                public string Name { get; set; } = "";
                public double Ratio;
            }
        }
        """;

    [Fact]
    public async Task EqualityMembers_CompareWithAnOperatorThatExistsForEachMemberType()
    {
        var (service, workspace, document) = Load(HolderSource);

        var json = Json(await service.GenerateEqualityMembersAsync(null, null, null, includeOperators: false,
            preview: false, typeName: "Gen.Holder"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(HolderSource, ("""
                    public double Ratio;
                }
            """, """
                    public double Ratio;

                    public override bool Equals(object? obj)
                    {
                        return obj is Holder other && Equals(other);
                    }

                    public bool Equals(Holder? other)
                    {
                        if (other is null)
                        {
                            return false;
                        }

                        if (ReferenceEquals(this, other))
                        {
                            return true;
                        }

                        return global::System.Collections.Generic.EqualityComparer<global::Gen.Money>.Default.Equals(M, other.M) && Count == other.Count && Name == other.Name && global::System.Collections.Generic.EqualityComparer<double>.Default.Equals(Ratio, other.Ratio);
                    }

                    public override int GetHashCode()
                    {
                        var hash = new global::System.HashCode();
                        hash.Add(M);
                        hash.Add(Count);
                        hash.Add(Name);
                        hash.Add(Ratio);
                        return hash.ToHashCode();
                    }
                }
            """)));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string BoxSource = """
        namespace Gen
        {
            public class Box<T>
            {
                public T Value { get; set; } = default!;
            }
        }
        """;

    [Fact]
    public async Task EqualityMembers_OnAnUnconstrainedTypeParameter_UseTheDefaultComparer()
    {
        var (service, workspace, document) = Load(BoxSource);

        var json = Json(await service.GenerateEqualityMembersAsync(null, null, null, includeOperators: true,
            preview: false, typeName: "Gen.Box"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(BoxSource, ("""
                    public T Value { get; set; } = default!;
                }
            """, """
                    public T Value { get; set; } = default!;

                    public override bool Equals(object? obj)
                    {
                        return obj is Box<T> other && Equals(other);
                    }

                    public bool Equals(Box<T>? other)
                    {
                        if (other is null)
                        {
                            return false;
                        }

                        if (ReferenceEquals(this, other))
                        {
                            return true;
                        }

                        return global::System.Collections.Generic.EqualityComparer<T>.Default.Equals(Value, other.Value);
                    }

                    public override int GetHashCode()
                    {
                        var hash = new global::System.HashCode();
                        hash.Add(Value);
                        return hash.ToHashCode();
                    }

                    public static bool operator ==(Box<T>? left, Box<T>? right)
                    {
                        return Equals(left, right);
                    }

                    public static bool operator !=(Box<T>? left, Box<T>? right)
                    {
                        return !Equals(left, right);
                    }
                }
            """)));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string IndexerSource = """
        namespace Gen
        {
            public interface ISized { int Size { get; set; } }

            public class WithIndexer : ISized
            {
                private readonly int[] _a = new int[3];
                public int this[int i] { get => _a[i]; set => _a[i] = value; }
                public int Count { get; set; }
                int ISized.Size { get; set; }
            }
        }
        """;

    [Fact]
    public async Task EqualityMembers_NeverCompareAnIndexerOrAnExplicitInterfaceProperty()
    {
        var (service, workspace, _) = Load(IndexerSource);

        var json = Json(await service.GenerateEqualityMembersAsync(null, null, null, includeOperators: false,
            preview: false, typeName: "Gen.WithIndexer"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ((JArray)json["data"]!["membersCompared"]!).Select(m => m.Value<string>()).Should().Equal("_a", "Count");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task Constructor_FromAnIndexerType_TakesTheFieldAndPropertyButNotTheIndexer()
    {
        var (service, _, _) = Load(IndexerSource);

        var json = Json(await service.GenerateConstructorAsync(null, null, null, includeProperties: true, typeName: "Gen.WithIndexer"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["constructorCode"]!.Value<string>().Should().Be(
            "public WithIndexer(int[] a, int count)\n{\n    _a = a;\n    Count = count;\n}");
    }

    internal const string Ctor1Source = """
        namespace Gen
        {
            public class Ctor1
            {
                private readonly string name;
                private readonly int count;
                public string Title { get; }
                public int Size { get; set; }
                public int Computed => count * 2;
                private int Hidden { get; set; }
            }
        }
        """;

    [Fact]
    public async Task Constructor_QualifiesSameNamedFields_AndCoversGetOnlyAutoProperties()
    {
        var (service, _, _) = Load(Ctor1Source);

        var json = Json(await service.GenerateConstructorAsync(null, null, null, includeProperties: true, typeName: "Gen.Ctor1"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var code = json["data"]!["constructorCode"]!.Value<string>()!;
        code.Should().Be(
            "public Ctor1(string name, int count, string title, int size, int hidden)\n{\n" +
            "    this.name = name;\n    this.count = count;\n    Title = title;\n    Size = size;\n    Hidden = hidden;\n}");
        ((JArray)json["data"]!["properties"]!).Select(p => p.Value<string>()).Should().Equal("Title", "Size", "Hidden");

        var (_, compiled, _) = Load(Edited(Ctor1Source,
            ("private int Hidden { get; set; }", "private int Hidden { get; set; }\n" + code)));
        (await ErrorsOf(compiled)).Should().BeEmpty();
    }

    internal const string Svc2Source = """
        using System.Threading.Tasks;

        namespace Gen
        {
            public class Svc2
            {
                public int P { get; private set; }
                public string Q { private get; set; } = "";
                public async Task DoAsync() { await Task.Yield(); }
                public virtual void V() { }
                public static void S() { }
            }
        }
        """;

    [Fact]
    public async Task ExtractInterface_CopiesOnlyTheAccessorsAnInterfaceCanDeclare()
    {
        var (service, _, _) = Load(Svc2Source);

        var json = Json(await service.ExtractInterfaceAsync(null, null, null, "ISvc2", null, typeName: "Gen.Svc2"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var code = json["data"]!["interfaceCode"]!.Value<string>()!;
        code.Should().Be(
            "namespace Gen\n{\n    public interface ISvc2\n    {\n" +
            "        global::System.Int32 P { get; }\n\n" +
            "        global::System.String Q { set; }\n\n" +
            "        global::System.Threading.Tasks.Task DoAsync();\n" +
            "        void V();\n" +
            "    }\n}");

        var (_, compiled, _) = Load(Svc2Source.Replace("public class Svc2", "public class Svc2 : ISvc2") + "\n" + code);
        (await ErrorsOf(compiled)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("I Bad-Name")]
    [InlineData("")]
    [InlineData("class")]
    public async Task ExtractInterface_RefusesAnInvalidInterfaceName(string interfaceName)
    {
        var (service, _, _) = Load(Svc2Source);

        var json = Json(await service.ExtractInterfaceAsync(null, null, null, interfaceName, null, typeName: "Gen.Svc2"));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be($"'{interfaceName}' is not a valid C# identifier");
    }

    [Fact]
    public async Task ExtractInterface_RefusesAMemberNameThatIsNotAnExtractableMember()
    {
        var (service, _, _) = Load(Svc2Source);

        var json = Json(await service.ExtractInterfaceAsync(null, null, null, "ISvc2",
            new List<string> { "P", "Nope", "S" }, typeName: "Gen.Svc2"));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            "Not public instance members of 'Gen.Svc2': Nope, S");
    }
}
