using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_di_registrations recognizes the Microsoft.Extensions.DependencyInjection
// registration API by the declaring types themselves (the stubs below mirror their
// namespaces, names and shapes): AddX/AddKeyedX, the TryAdd* family including
// TryAddKeyed* and TryAddEnumerable, Add/TryAdd of a ServiceDescriptor (built by a
// ServiceDescriptor factory or constructor), AddHostedService, and calls through a
// null-conditional receiver. The implementation type comes from the type arguments, a
// factory lambda's produced type, an instance's type, or the descriptor.
public class DiRegistrationCoverageTests
{
    internal const string Code = """
        namespace Microsoft.Extensions.DependencyInjection
        {
            public enum ServiceLifetime { Singleton, Scoped, Transient }

            public class ServiceDescriptor
            {
                public ServiceDescriptor(System.Type serviceType, System.Type implementationType, ServiceLifetime lifetime) { }
                public static ServiceDescriptor Singleton<TService, TImplementation>()
                    where TService : class where TImplementation : class, TService => null!;
                public static ServiceDescriptor Scoped(System.Type service, System.Type implementationType) => null!;
            }

            public interface IServiceCollection : System.Collections.Generic.IList<ServiceDescriptor> { }

            public static class ServiceCollectionServiceExtensions
            {
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, System.Func<System.IServiceProvider, TService> factory)
                    where TService : class => services;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection services, TService instance)
                    where TService : class => services;
                public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services)
                    where TService : class where TImplementation : class, TService => services;
                public static IServiceCollection AddKeyedTransient<TService, TImplementation>(this IServiceCollection services, object? serviceKey)
                    where TService : class where TImplementation : class, TService => services;
            }

            public static class ServiceCollectionHostedServiceExtensions
            {
                public static IServiceCollection AddHostedService<THostedService>(this IServiceCollection services)
                    where THostedService : class => services;
            }
        }

        namespace Microsoft.Extensions.DependencyInjection.Extensions
        {
            public static class ServiceCollectionDescriptorExtensions
            {
                public static void TryAddKeyedSingleton<TService, TImplementation>(this IServiceCollection collection, object? serviceKey)
                    where TService : class where TImplementation : class, TService { }
                public static void TryAddEnumerable(this IServiceCollection services, ServiceDescriptor descriptor) { }
                public static void TryAdd(this IServiceCollection collection, ServiceDescriptor descriptor) { }
            }
        }

        namespace App
        {
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection.Extensions;

            public interface ISvc { }
            public class SvcImpl : ISvc { }
            public class Worker { }

            public static class Setup
            {
                public static void Configure(IServiceCollection s, IServiceCollection? maybe)
                {
                    s.AddSingleton<ISvc>(sp => new SvcImpl());
                    s.AddSingleton<ISvc>(new SvcImpl());
                    s.AddScoped<ISvc, SvcImpl>();
                    maybe?.AddScoped<ISvc, SvcImpl>();
                    s.AddKeyedTransient<ISvc, SvcImpl>("k");
                    s.TryAddKeyedSingleton<ISvc, SvcImpl>("k");
                    s.TryAddEnumerable(ServiceDescriptor.Singleton<ISvc, SvcImpl>());
                    s.Add(new ServiceDescriptor(typeof(ISvc), typeof(SvcImpl), ServiceLifetime.Transient));
                    s.TryAdd(ServiceDescriptor.Scoped(typeof(ISvc), typeof(SvcImpl)));
                    s.AddHostedService<Worker>();
                }
            }
        }
        """;

    private static readonly string[] Expected =
    {
        "AddSingleton|Singleton|ISvc|SvcImpl",
        "AddSingleton|Singleton|ISvc|SvcImpl",
        "AddScoped|Scoped|ISvc|SvcImpl",
        "AddScoped|Scoped|ISvc|SvcImpl",
        "AddKeyedTransient|Transient|ISvc|SvcImpl",
        "TryAddKeyedSingleton|Singleton|ISvc|SvcImpl",
        "TryAddEnumerable|Singleton|ISvc|SvcImpl",
        "Add|Transient|ISvc|SvcImpl",
        "TryAdd|Scoped|ISvc|SvcImpl",
        "AddHostedService|Singleton|IHostedService|Worker",
    };

    private static List<string> Registrations(JToken data) =>
        ((JArray)data["registrations"]!)
            .Select(r => $"{r["method"]}|{r["lifetime"]}|{r["serviceType"]}|{r["implementationType"]}").ToList();

    [Fact]
    public async Task RecognizesEveryRegistrationShape_InSourceOrder()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Setup.cs");
        var service = ToolResult.Load(workspace);

        var response = await service.GetDiRegistrationsAsync();

        Registrations(ToolResult.Data(response)).Should().Equal(Expected);
        ToolResult.Meta(response)["TotalCount"]!.Value<int>().Should().Be(Expected.Length);
    }

    [Fact]
    public async Task MultiTargetedProject_ReportsEachRegistrationOnce()
    {
        using var fixture = new MultiTargetSolutionFixture(Code, "namespace Consumer { }");
        var service = ToolResult.Load(fixture.Workspace);

        var data = ToolResult.Data(await service.GetDiRegistrationsAsync("Multi"));

        Registrations(data).Should().Equal(Expected);
        ((JArray)data["registrations"]!)[0]["targetFrameworks"]!.Values<string>()
            .Should().Equal(MultiTargetSolutionFixture.Net10, MultiTargetSolutionFixture.Net8);
    }

    [Fact]
    public async Task UnknownProjectName_IsInvalidParameter()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Setup.cs");
        var service = ToolResult.Load(workspace);

        ToolResult.Error(await service.GetDiRegistrationsAsync("Nope"), ErrorCodes.InvalidParameter)
            ["Message"]!.Value<string>().Should().Be("Project not found: Nope");
    }

    [Fact]
    public async Task RecognizesEveryRegistrationShape_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Setup.cs");
        var host = new InMemoryToolHost(workspace);

        var data = await host.DataAsync("get_di_registrations", new { });
        await host.ErrorAsync("get_di_registrations", new { projectName = "Nope" }, ErrorCodes.InvalidParameter);

        Registrations(data).Should().Equal(Expected);
    }
}
