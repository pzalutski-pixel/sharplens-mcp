using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SharpLensMcp.Tests.Fixtures;

/// <summary>
/// Fixture for source generator tests. The <see cref="FixtureJsonContext"/> triggers
/// the in-box System.Text.Json source generator at build time, producing a partial
/// class with typed metadata accessors (e.g. <c>FixtureJsonContext.Default.FixtureRecord</c>).
///
/// <see cref="FixtureJsonContextConsumer"/> references the generator output — without the
/// bug-fix helper in RoslynService, MSBuildWorkspace's pre-generator compilation reports
/// a phantom CS0117 for that reference (the subject of GitHub issue #7).
/// </summary>
public record FixtureRecord(string Name, int Age);

[JsonSerializable(typeof(FixtureRecord))]
public partial class FixtureJsonContext : JsonSerializerContext
{
}

public class FixtureJsonContextConsumer
{
    public JsonTypeInfo<FixtureRecord> GetTypeInfo()
    {
        // This references FixtureJsonContext.Default.FixtureRecord, which is produced
        // by the JSON source generator. MSBuildWorkspace's default compilation doesn't
        // run generators, so without the fix this line fails to resolve.
        return FixtureJsonContext.Default.FixtureRecord;
    }
}
