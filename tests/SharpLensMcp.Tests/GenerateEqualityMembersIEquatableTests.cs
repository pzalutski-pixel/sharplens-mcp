using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// generate_equality_members emits a strongly-typed Equals(T), so it must refuse when
// the type already declares one — via IEquatable<T>, a new-hidden Equals, or its own
// overload — even without an Equals(object) override, since regenerating duplicates it.
public class GenerateEqualityMembersIEquatableTests
{
    [Fact]
    public async Task TypeImplementingIEquatable_IsRefused()
    {
        const string code = @"
using System;
namespace Production
{
    public class Money : IEquatable<Money>
    {
        public int Amount { get; set; }
        public bool Equals(Money? other) => other != null && other.Amount == Amount;
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Money.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GenerateEqualityMembersAsync(
            filePath: null, line: null, column: null, typeName: "Money");
        var json = JObject.FromObject(result);

        json["success"]!.Value<bool>().Should().BeFalse(json.ToString());
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter,
            "regenerating Equals(Money) would duplicate the existing IEquatable<Money> implementation (CS0111)");
        json["error"]!["Message"]!.Value<string>().Should().Contain("already declares");
    }
}
