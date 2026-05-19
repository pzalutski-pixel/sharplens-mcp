// Intentionally unsorted using directives so organize_usings_batch has work
// to do on apply. Microsoft.* should re-sort AFTER System.* per the impl's
// bucketing rule (Analysis.cs:981-984).
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
