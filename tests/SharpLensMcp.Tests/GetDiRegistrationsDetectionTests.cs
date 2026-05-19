using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for get_di_registrations covering the actual registration
// detection at Discovery.cs:102-137. The impl matches by
//   (a) method name starts with one of the DI patterns
//   (b) containing-type display string Contains "ServiceCollection"
// so the fixture's fake extension class is named "...ServiceCollection..."
// to clear gate (b) without taking a real Microsoft.Extensions.DependencyInjection
// dependency.
public class GetDiRegistrationsDetectionTests
{
    private const string Code = @"
namespace App.Abstractions
{
    public interface IFoo { }
    public class Foo : IFoo { }
    public class Bar { }
}

namespace Microsoft.Extensions.DependencyInjection.Stub
{
    public interface IServiceCollection { }

    public static class ServiceCollectionServiceExtensions
    {
        public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection svc) where TImpl : class, TService where TService : class => svc;
        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection svc) where TImpl : class, TService where TService : class => svc;
        public static IServiceCollection AddTransient<TService, TImpl>(this IServiceCollection svc) where TImpl : class, TService where TService : class => svc;
    }
}

namespace App.Composition
{
    using Microsoft.Extensions.DependencyInjection.Stub;
    using App.Abstractions;

    public class Composition
    {
        public void Register(IServiceCollection services)
        {
            services.AddSingleton<IFoo, Foo>();
            services.AddScoped<IFoo, Foo>();
            services.AddTransient<Bar, Bar>();
        }
    }
}";

    private static async Task<JArray> RunAndGetRegistrations()
    {
        var(workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.GetDiRegistrationsAsync();
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return (json["data"]!["registrations"] as JArray)!;
    }

    [Fact]
    public async Task GetDiRegistrations_DetectsAddSingleton_WithLifetimeServiceAndImpl()
    {
        var regs = await RunAndGetRegistrations();
        var singleton = regs.FirstOrDefault(r => r["method"]!.Value<string>() == "AddSingleton");
        singleton.Should().NotBeNull("the AddSingleton invocation must be matched");
        singleton!["lifetime"]!.Value<string>().Should().Be("Singleton");
        singleton["serviceType"]!.Value<string>()!.Should().Contain("IFoo",
            "TypeArguments[0] must round-trip to MinimallyQualifiedFormat as IFoo");
        singleton["implementationType"]!.Value<string>()!.Should().Contain("Foo");
    }

    [Fact]
    public async Task GetDiRegistrations_DetectsAddScoped_WithScopedLifetime()
    {
        var regs = await RunAndGetRegistrations();
        var scoped = regs.FirstOrDefault(r => r["method"]!.Value<string>() == "AddScoped");
        scoped.Should().NotBeNull();
        scoped!["lifetime"]!.Value<string>().Should().Be("Scoped",
            "the lifetime ternary at Discovery.cs:114-118 must pick `Scoped` when methodName contains `Scoped`");
    }

    [Fact]
    public async Task GetDiRegistrations_DetectsAddTransient_WithTransientLifetime()
    {
        var regs = await RunAndGetRegistrations();
        var transient = regs.FirstOrDefault(r => r["method"]!.Value<string>() == "AddTransient");
        transient.Should().NotBeNull();
        transient!["lifetime"]!.Value<string>().Should().Be("Transient");
        transient["serviceType"]!.Value<string>()!.Should().Contain("Bar");
    }

    [Fact]
    public async Task GetDiRegistrations_TotalCount_Is3()
    {
        var regs = await RunAndGetRegistrations();
        regs.Count.Should().Be(3,
            "the fixture invokes AddSingleton, AddScoped, AddTransient — each must contribute exactly one registration");
    }
}
