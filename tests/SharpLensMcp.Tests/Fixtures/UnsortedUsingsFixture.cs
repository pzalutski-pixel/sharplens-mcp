// Intentionally unsorted using directives so organize_usings_batch has work to
// do on apply. With this project's ImplicitUsings, `System` and
// `System.Collections.Generic` are CS8019-redundant (the tool removes them);
// System.Text and Microsoft.CodeAnalysis are genuinely needed and re-sort
// System-first.
using Microsoft.CodeAnalysis;
using System.Text;
using System;
using System.Collections.Generic;

namespace SharpLensMcp.Tests.Fixtures;

public class UnsortedUsingsTarget
{
    public List<string> Items { get; } = new();
    public StringBuilder Builder { get; } = new();
    public Workspace? Workspace { get; }
    public Action? Callback { get; }
}
