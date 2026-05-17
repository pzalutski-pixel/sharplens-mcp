namespace SharpLensMcp.Tests.Fixtures;

// Fixtures for get_call_graph: a linear chain (A -> B -> C), a cycle (D -> E -> D),
// and a fan-out (Hub calls 4 leaves). Names are fixture-prefixed to avoid
// collisions with FindTypeByName.

public class CallGraphFixture
{
    public void ChainA() => ChainB();
    public void ChainB() => ChainC();
    public void ChainC() { /* leaf */ }

    public void CycleD() => CycleE();
    public void CycleE() => CycleD();

    public void HubMethod()
    {
        Leaf1();
        Leaf2();
        Leaf3();
        Leaf4();
    }

    public void Leaf1() { }
    public void Leaf2() { }
    public void Leaf3() { }
    public void Leaf4() { }
}
