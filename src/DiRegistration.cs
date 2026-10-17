using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One service registration DiRegistrationReader found: the registration method invoked,
// the lifetime (Singleton, Scoped, Transient, or Unknown when it cannot be read from the
// source), the service and implementation types (minimally qualified, null when the source
// does not state them), and where the registering call is written.
internal sealed record DiRegistration(
    string Method,
    string Lifetime,
    string? ServiceType,
    string? ImplementationType,
    Location Location);
