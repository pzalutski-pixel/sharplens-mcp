using System;

namespace SharpLensMcp.Tests.Fixtures;

// Fixture for ExtractInterface event-branch coverage. GenerateInterfaceCode
// at RoslynService.cs:664-668 handles IEventSymbol but no other fixture
// declares events.
public class EventBearingTarget
{
    public event EventHandler? Triggered;

    public string Label { get; set; } = "";

    protected virtual void OnTriggered() => Triggered?.Invoke(this, EventArgs.Empty);
}
