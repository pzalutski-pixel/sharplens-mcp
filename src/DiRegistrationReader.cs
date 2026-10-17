using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SharpLensMcp;

// Reads Microsoft.Extensions.DependencyInjection service registrations from a call, by the
// registration API's own declaring types (exact namespace and name, so a look-alike helper
// of the same name is never taken for one):
//   - ServiceCollectionServiceExtensions: AddTransient/AddScoped/AddSingleton and their
//     AddKeyed* forms;
//   - ServiceCollectionDescriptorExtensions (…DependencyInjection.Extensions): the
//     TryAdd{Transient,Scoped,Singleton} and TryAddKeyed* forms, and Add, TryAdd,
//     TryAddEnumerable and Replace of a ServiceDescriptor or a collection of them;
//   - ServiceCollectionHostedServiceExtensions: AddHostedService (a Singleton
//     IHostedService);
//   - Add on the IServiceCollection itself (its ICollection<ServiceDescriptor>.Add).
// The call may go through a null-conditional receiver (services?.AddX(...)). Types come
// from the type arguments, from typeof arguments bound to System.Type parameters, from a
// factory lambda's produced type, or from an instance argument's type; a descriptor's from
// the ServiceDescriptor constructor or factory that builds it.
internal static class DiRegistrationReader
{
    private const string DiNamespace = "Microsoft.Extensions.DependencyInjection";
    private const string DiExtensionsNamespace = "Microsoft.Extensions.DependencyInjection.Extensions";

    // The lifetime-named registration methods: Add{Lifetime}, AddKeyed{Lifetime},
    // TryAdd{Lifetime} and TryAddKeyed{Lifetime}.
    private static readonly Regex LifetimeMethod = new(
        "^(?<try>Try)?Add(Keyed)?(?<lifetime>Transient|Scoped|Singleton)$", RegexOptions.CultureInvariant);

    public static IEnumerable<DiRegistration> Read(
        InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken)
    {
        if (model.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation) return Array.Empty<DiRegistration>();

        var method = operation.TargetMethod;
        var declaring = method.ContainingType;
        var name = method.Name;
        var location = RegistrationLocation(invocation);

        var lifetimeMethod = LifetimeMethod.Match(name);
        var isTryAdd = lifetimeMethod.Groups["try"].Success;
        if (lifetimeMethod.Success
            && ((!isTryAdd && Is(declaring, DiNamespace, "ServiceCollectionServiceExtensions"))
                || (isTryAdd && Is(declaring, DiExtensionsNamespace, "ServiceCollectionDescriptorExtensions"))))
        {
            var (service, implementation) = TypesOf(operation.Arguments, method);
            return new[] { new DiRegistration(name, lifetimeMethod.Groups["lifetime"].Value, service, implementation, location) };
        }

        if (Is(declaring, DiNamespace, "ServiceCollectionHostedServiceExtensions") && name == "AddHostedService")
        {
            var (hosted, implementation) = TypesOf(operation.Arguments, method);
            return new[] { new DiRegistration(name, "Singleton", "IHostedService", implementation ?? hosted, location) };
        }

        if (name is "Add" or "TryAdd" or "TryAddEnumerable" or "Replace"
            && (Is(declaring, DiExtensionsNamespace, "ServiceCollectionDescriptorExtensions") || IsServiceCollection(operation.Instance?.Type)))
        {
            return operation.Arguments
                .Where(a => IsDescriptorOrDescriptors(a.Parameter?.Type))
                .SelectMany(a => DescriptorsOf(a.Value))
                .Select(d => new DiRegistration(name, d.Lifetime, d.Service, d.Implementation, location))
                .ToList();
        }

        return Array.Empty<DiRegistration>();
    }

    // Where a registration is reported: the call, or the whole null-conditional access a
    // call through `services?.` belongs to.
    private static Location RegistrationLocation(InvocationExpressionSyntax invocation)
    {
        SyntaxNode node = invocation;
        while (node.Parent is ConditionalAccessExpressionSyntax access && access.WhenNotNull == node) node = access;
        return node.GetLocation();
    }

    private static string? LifetimeOfName(string name) =>
        name.Contains("Singleton") ? "Singleton"
        : name.Contains("Scoped") ? "Scoped"
        : name.Contains("Transient") ? "Transient"
        : null;

    // The service and implementation types a registration states: the type arguments
    // (service, implementation), else typeof arguments bound to System.Type parameters in
    // order; a factory's produced type or an instance argument's type stands for an
    // implementation the type arguments leave open; a single stated type registers itself.
    private static (string? Service, string? Implementation) TypesOf(
        ImmutableArray<IArgumentOperation> arguments, IMethodSymbol method)
    {
        var typeOfs = new List<ITypeSymbol>();
        ITypeSymbol? provided = null;
        foreach (var argument in arguments)
        {
            if (argument.Parameter is not { } parameter) continue;
            var value = Unwrap(argument.Value);
            var declaredType = method.OriginalDefinition.Parameters.ElementAtOrDefault(parameter.Ordinal)?.Type;
            if (value is ITypeOfOperation typeOf && IsSystemType(parameter.Type))
            {
                typeOfs.Add(typeOf.TypeOperand);
            }
            else if (FactoryLambda(value) is { } lambda)
            {
                provided ??= FactoryProducedType(lambda);
            }
            else if (declaredType is ITypeParameterSymbol || parameter.Name == "implementationInstance")
            {
                provided ??= value.Type;
            }
        }

        var typeArguments = method.TypeArguments;
        var service = typeArguments.ElementAtOrDefault(0) ?? typeOfs.ElementAtOrDefault(0);
        var implementation = typeArguments.ElementAtOrDefault(1) ?? typeOfs.ElementAtOrDefault(1) ?? provided ?? service;
        return (Display(service), Display(implementation));
    }

    // The descriptors an argument adds: one built by a ServiceDescriptor constructor or
    // static factory, each element of an array or collection expression of them, or — for
    // any other expression, whose descriptor the source does not show — one Unknown.
    private static IEnumerable<(string Lifetime, string? Service, string? Implementation)> DescriptorsOf(IOperation value)
    {
        switch (Unwrap(value))
        {
            case IObjectCreationOperation { Constructor: { } constructor } creation when IsServiceDescriptor(creation.Type):
            {
                var (service, implementation) = TypesOf(creation.Arguments, constructor);
                yield return (LifetimeArgument(creation.Arguments) ?? (HasInstanceArgument(creation.Arguments) ? "Singleton" : "Unknown"), service, implementation);
                break;
            }
            case IInvocationOperation call when IsServiceDescriptor(call.TargetMethod.ContainingType):
            {
                var (service, implementation) = TypesOf(call.Arguments, call.TargetMethod);
                yield return (LifetimeOfName(call.TargetMethod.Name) ?? LifetimeArgument(call.Arguments) ?? "Unknown", service, implementation);
                break;
            }
            case IArrayCreationOperation { Initializer: { } initializer }:
                foreach (var element in initializer.ElementValues)
                    foreach (var descriptor in DescriptorsOf(element)) yield return descriptor;
                break;
            case ICollectionExpressionOperation collection:
                foreach (var element in collection.Elements)
                    foreach (var descriptor in DescriptorsOf(element)) yield return descriptor;
                break;
            default:
                yield return ("Unknown", null, null);
                break;
        }
    }

    // The lifetime a ServiceLifetime argument names (ServiceLifetime.Scoped, or a constant).
    private static string? LifetimeArgument(ImmutableArray<IArgumentOperation> arguments)
    {
        foreach (var argument in arguments)
        {
            if (!Is(argument.Parameter?.Type, DiNamespace, "ServiceLifetime")) continue;
            var value = Unwrap(argument.Value);
            if (value is IFieldReferenceOperation field) return field.Field.Name;
            if (value.ConstantValue is { HasValue: true, Value: { } constant } && value.Type is INamedTypeSymbol enumType)
            {
                return enumType.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, constant))?.Name;
            }
        }
        return null;
    }

    private static bool HasInstanceArgument(ImmutableArray<IArgumentOperation> arguments) =>
        arguments.Any(a => a.Parameter?.Name == "instance" || a.Parameter?.Name == "implementationInstance");

    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation conversion) operation = conversion.Operand;
        return operation;
    }

    private static IAnonymousFunctionOperation? FactoryLambda(IOperation value) => value switch
    {
        IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda } => lambda,
        IAnonymousFunctionOperation lambda => lambda,
        _ => null
    };

    // The type a factory lambda produces: the type of the last value its body returns (an
    // expression body is one return), before the conversion to the delegate's return type;
    // returns of nested lambdas and local functions are not the factory's.
    private static ITypeSymbol? FactoryProducedType(IAnonymousFunctionOperation lambda) =>
        ReturnedValues(lambda.Body).LastOrDefault() is { } returned ? Unwrap(returned).Type : null;

    private static IEnumerable<IOperation> ReturnedValues(IOperation operation)
    {
        foreach (var child in operation.ChildOperations)
        {
            if (child is IAnonymousFunctionOperation or ILocalFunctionOperation) continue;
            if (child is IReturnOperation { ReturnedValue: { } value }) yield return value;
            foreach (var nested in ReturnedValues(child)) yield return nested;
        }
    }

    private static bool IsServiceCollection(ITypeSymbol? type) =>
        type != null && (Is(type, DiNamespace, "IServiceCollection") || type.AllInterfaces.Any(i => Is(i, DiNamespace, "IServiceCollection")));

    private static bool IsServiceDescriptor(ITypeSymbol? type) => Is(type, DiNamespace, "ServiceDescriptor");

    // A ServiceDescriptor, or an IEnumerable<ServiceDescriptor> (TryAddEnumerable's and
    // TryAdd's collection overloads).
    private static bool IsDescriptorOrDescriptors(ITypeSymbol? type) =>
        IsServiceDescriptor(type)
        || type is INamedTypeSymbol { TypeArguments.Length: 1 } generic && IsServiceDescriptor(generic.TypeArguments[0]);

    private static bool IsSystemType(ITypeSymbol? type) => Is(type, "System", "Type");

    private static bool Is(ITypeSymbol? type, string ns, string name) =>
        type != null && TestFrameworkContracts.Is(type, ns, name);

    private static string? Display(ITypeSymbol? type) => type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
}
