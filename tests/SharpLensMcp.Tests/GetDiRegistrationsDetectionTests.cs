using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for get_di_registrations covering the actual registration
// detection. The impl matches by
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
            "the lifetime ternary must pick `Scoped` when methodName contains `Scoped`");
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

    private const string NonGenericCode = @"
using System;
namespace Microsoft.Extensions.DependencyInjection.Stub2
{
    public interface IServiceCollection { }
    public static class ServiceCollectionServiceExtensions
    {
        public static IServiceCollection AddSingleton(this IServiceCollection svc, Type service, Type impl) => svc;
        public static IServiceCollection AddSingleton(this IServiceCollection svc, Type service, Func<IServiceCollection, object> factory) => svc;
        public static IServiceCollection AddSingletonCache(this IServiceCollection svc) => svc;
    }
}
namespace App2
{
    using Microsoft.Extensions.DependencyInjection.Stub2;
    public interface IService { }
    public class Service : IService { }
    public class Host
    {
        public void Register(IServiceCollection services)
        {
            services.AddSingleton(typeof(IService), typeof(Service));
            services.AddSingleton(typeof(IService), sp => new Service());
            services.AddSingletonCache();
        }
    }
}";

    [Fact]
    public async Task GetDiRegistrations_NonGeneric_ReadsTypeofAndFactoryTypes_AndIgnoresLookalike()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(NonGenericCode);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var regs = (JObject.FromObject(await service.GetDiRegistrationsAsync())["data"]!["registrations"] as JArray)!;

        regs.Should().HaveCount(2,
            "the two AddSingleton overloads register; AddSingletonCache is a lookalike, not DI");
        regs.Should().OnlyContain(r => r["serviceType"]!.Value<string>()!.Contains("IService"),
            "the service type comes from the first typeof argument");
        regs.Should().OnlyContain(r => r["implementationType"]!.Value<string>()!.Contains("Service"),
            "the implementation type comes from the second typeof or the factory's produced type");
        regs.Should().NotContain(r => r["method"]!.Value<string>() == "AddSingletonCache");
    }

    private const string HostedAndFactoryCode = @"
using System;
namespace Microsoft.Extensions.DependencyInjection.Stub3
{
    public interface IServiceCollection { }
    public static class ServiceCollectionHostedExtensions
    {
        public static IServiceCollection AddHostedService<THosted>(this IServiceCollection svc) where THosted : class => svc;
        public static IServiceCollection AddSingleton(this IServiceCollection svc, Type service, Func<IServiceCollection, object> factory) => svc;
    }
}
namespace App3
{
    using Microsoft.Extensions.DependencyInjection.Stub3;
    public interface IWidget { }
    public class Widget : IWidget { }
    public class Worker { }
    public class Host3
    {
        public void Register(IServiceCollection services)
        {
            services.AddHostedService<Worker>();
            services.AddSingleton(typeof(IWidget), sp =>
            {
                var w = new Widget();
                return w;
            });
        }
    }
}";

    [Fact]
    public async Task GetDiRegistrations_AddHostedService_ServiceTypeIsIHostedService()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(HostedAndFactoryCode);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var regs = (JObject.FromObject(await service.GetDiRegistrationsAsync())["data"]!["registrations"] as JArray)!;

        var hosted = regs.Single(r => r["method"]!.Value<string>() == "AddHostedService");
        hosted["serviceType"]!.Value<string>().Should().Be("IHostedService",
            "AddHostedService<T> registers T under the IHostedService service, not under T itself");
        hosted["implementationType"]!.Value<string>().Should().Be("Worker",
            "the type argument is the implementation");
    }

    [Fact]
    public async Task GetDiRegistrations_BlockBodiedFactory_CapturesReturnedType()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(HostedAndFactoryCode);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var regs = (JObject.FromObject(await service.GetDiRegistrationsAsync())["data"]!["registrations"] as JArray)!;

        var factory = regs.Single(r => r["method"]!.Value<string>() == "AddSingleton");
        factory["serviceType"]!.Value<string>().Should().Be("IWidget");
        factory["implementationType"]!.Value<string>().Should().Be("Widget",
            "a block-bodied factory's returned type must be captured as the implementation, not left as the service type");
    }
}
