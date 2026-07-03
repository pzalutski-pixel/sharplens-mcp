using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// check_architecture coupling edges attribute an invocation to the RECEIVER's
// type, not the (base) type that declares the called method. App.Service calls a
// method declared in Domain.Entity but invoked on an Infra.Repo receiver, so the
// edge is App→Infra, never App→Domain.
public class ArchitectureReceiverEdgeTests
{
    private const string Code = @"
namespace Domain { public class Entity { public virtual void Touch() { } } }
namespace Infra { public class Repo : Domain.Entity { } }
namespace App
{
    public class Service
    {
        public void Run(Infra.Repo repo)
        {
            repo.Touch();
        }
    }
}";

    private static async Task<JArray> ViolationsFor(string from, string deny)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var rules = new List<ArchitectureRule> { new() { From = from, Deny = deny } };
        var result = await service.CheckArchitectureAsync(rules, level: "namespace");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return (json["data"]!["violations"] as JArray)!;
    }

    [Fact]
    public async Task InvocationEdge_PointsAtReceiverType_NotTheDeclaringBaseType()
    {
        // The call binds to Domain.Entity.Touch, but the edge must follow the
        // Infra.Repo receiver — so an App→Domain rule sees nothing here.
        var domainViolations = await ViolationsFor("App*", "Domain*");
        domainViolations.Should().BeEmpty(
            "Touch is declared in Domain.Entity but the receiver is Infra.Repo — no App→Domain edge");

        var infraViolations = await ViolationsFor("App*", "Infra*");
        infraViolations.Should().NotBeEmpty(
            "App.Service depends on Infra.Repo via the receiver of the call");
        infraViolations.Should().Contain(v => v["toType"]!.Value<string>()!.Contains("Infra.Repo"));
    }
}
