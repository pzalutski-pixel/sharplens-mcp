namespace SharpLensMcp.Tests.Fixtures;

// Drives find_god_objects. Defines:
// - 25 small TypeXxx classes (used to inflate GodObject's efferent coupling)
// - GodObject: touches 25 distinct types AND has 25 members → must qualify
// - SmallFocused: 3 members, 2 type deps → must NOT qualify under default thresholds

public class GodObjectType01 { public int V; }
public class GodObjectType02 { public int V; }
public class GodObjectType03 { public int V; }
public class GodObjectType04 { public int V; }
public class GodObjectType05 { public int V; }
public class GodObjectType06 { public int V; }
public class GodObjectType07 { public int V; }
public class GodObjectType08 { public int V; }
public class GodObjectType09 { public int V; }
public class GodObjectType10 { public int V; }
public class GodObjectType11 { public int V; }
public class GodObjectType12 { public int V; }
public class GodObjectType13 { public int V; }
public class GodObjectType14 { public int V; }
public class GodObjectType15 { public int V; }
public class GodObjectType16 { public int V; }
public class GodObjectType17 { public int V; }
public class GodObjectType18 { public int V; }
public class GodObjectType19 { public int V; }
public class GodObjectType20 { public int V; }
public class GodObjectType21 { public int V; }
public class GodObjectType22 { public int V; }
public class GodObjectType23 { public int V; }
public class GodObjectType24 { public int V; }
public class GodObjectType25 { public int V; }

public class GodObjectFixtureClass
{
    public GodObjectType01 F01 = null!;
    public GodObjectType02 F02 = null!;
    public GodObjectType03 F03 = null!;
    public GodObjectType04 F04 = null!;
    public GodObjectType05 F05 = null!;
    public GodObjectType06 F06 = null!;
    public GodObjectType07 F07 = null!;
    public GodObjectType08 F08 = null!;
    public GodObjectType09 F09 = null!;
    public GodObjectType10 F10 = null!;
    public GodObjectType11 F11 = null!;
    public GodObjectType12 F12 = null!;
    public GodObjectType13 F13 = null!;
    public GodObjectType14 F14 = null!;
    public GodObjectType15 F15 = null!;
    public GodObjectType16 F16 = null!;
    public GodObjectType17 F17 = null!;
    public GodObjectType18 F18 = null!;
    public GodObjectType19 F19 = null!;
    public GodObjectType20 F20 = null!;
    public GodObjectType21 F21 = null!;
    public GodObjectType22 F22 = null!;
    public GodObjectType23 F23 = null!;
    public GodObjectType24 F24 = null!;
    public GodObjectType25 F25 = null!;
}

public class SmallFocusedFixture
{
    public GodObjectType01 A = null!;
    public GodObjectType02 B = null!;
    public int Sum() => A.V + B.V;
}
