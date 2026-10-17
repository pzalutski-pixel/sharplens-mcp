namespace SharpLensMcp;

// Cycle analysis of a dependency graph (node -> the nodes it depends on), the standard
// way: Tarjan's algorithm finds the strongly connected components, and Johnson's
// algorithm enumerates the elementary cycles within them. Every cycle through a node is
// found, not only the back edges one depth-first walk happens to meet.
//
// Nodes are numbered in ordinal order and every neighbor list is walked in that order,
// so the result is the same on every run. Johnson's search starts from each node in turn
// and finds exactly the cycles whose smallest node is the start, so each cycle is
// reported once, beginning at its smallest node. The search stops after maxCycles
// cycles (a graph can have exponentially many) and reports Truncated; the components are
// always complete.
internal static class DependencyCycles
{
    public const int DefaultMaxCycles = 1000;

    public static CycleAnalysis Analyze(Dictionary<string, List<string>> graph, int maxCycles = DefaultMaxCycles)
    {
        var names = graph.Keys
            .Concat(graph.Values.SelectMany(targets => targets))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var indexOf = names.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i, StringComparer.Ordinal);
        var adjacency = names
            .Select(name => graph.TryGetValue(name, out var targets)
                ? targets.Select(t => indexOf[t]).Distinct().OrderBy(i => i).ToArray()
                : Array.Empty<int>())
            .ToArray();

        var componentOf = StronglyConnectedComponents(adjacency);
        var components = Enumerable.Range(0, names.Count)
            .GroupBy(node => componentOf[node])
            .Select(g => g.OrderBy(node => node).ToList())
            .Where(members => members.Count > 1 || adjacency[members[0]].Contains(members[0]))
            .OrderBy(members => members[0])
            .Select(members => (IReadOnlyList<string>)members.Select(node => names[node]).ToList())
            .ToList();

        var (cycles, truncated) = ElementaryCycles(adjacency, componentOf, maxCycles);
        var ordered = cycles
            .OrderBy(c => c.Count)
            .ThenBy(c => c, SequenceOrder)
            .Select(c => (IReadOnlyList<string>)c.Select(node => names[node]).ToList())
            .ToList();
        return new CycleAnalysis(ordered, truncated, components);
    }

    // Tarjan's algorithm, iterative so a deep graph cannot overflow the stack. Returns the
    // component number of every node.
    private static int[] StronglyConnectedComponents(int[][] adjacency)
    {
        var count = adjacency.Length;
        var order = new int[count];
        Array.Fill(order, -1);
        var low = new int[count];
        var onStack = new bool[count];
        var componentOf = new int[count];
        var stack = new Stack<int>();
        var frames = new Stack<(int Node, int NextEdge)>();
        var nextOrder = 0;
        var components = 0;

        for (var root = 0; root < count; root++)
        {
            if (order[root] >= 0) continue;
            frames.Push((root, 0));
            while (frames.Count > 0)
            {
                var (node, nextEdge) = frames.Pop();
                if (nextEdge == 0)
                {
                    order[node] = low[node] = nextOrder++;
                    stack.Push(node);
                    onStack[node] = true;
                }

                var descended = false;
                for (var edge = nextEdge; edge < adjacency[node].Length; edge++)
                {
                    var target = adjacency[node][edge];
                    if (order[target] < 0)
                    {
                        frames.Push((node, edge + 1));
                        frames.Push((target, 0));
                        descended = true;
                        break;
                    }
                    if (onStack[target]) low[node] = Math.Min(low[node], order[target]);
                }
                if (descended) continue;

                if (low[node] == order[node])
                {
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack[member] = false;
                        componentOf[member] = components;
                    } while (member != node);
                    components++;
                }
                if (frames.Count > 0)
                {
                    var parent = frames.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
            }
        }
        return componentOf;
    }

    // Johnson's algorithm: for each start node s in order, the cycles through s whose
    // other nodes are all greater than s, searched within s's component.
    private static (List<List<int>> Cycles, bool Truncated) ElementaryCycles(
        int[][] adjacency, int[] componentOf, int maxCycles)
    {
        var count = adjacency.Length;
        var cycles = new List<List<int>>();
        var blocked = new bool[count];
        var blockedBy = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        var path = new List<int>();
        var truncated = false;

        for (var start = 0; start < count && !truncated; start++)
        {
            for (var node = start; node < count; node++)
            {
                blocked[node] = false;
                blockedBy[node].Clear();
            }
            Circuit(start, start);
        }
        return (cycles, truncated);

        bool InScope(int node, int start) => node >= start && componentOf[node] == componentOf[start];

        bool Circuit(int node, int start)
        {
            var closed = false;
            path.Add(node);
            blocked[node] = true;
            foreach (var target in adjacency[node])
            {
                if (truncated) break;
                if (!InScope(target, start)) continue;
                if (target == start)
                {
                    if (cycles.Count == maxCycles)
                    {
                        truncated = true;
                        break;
                    }
                    cycles.Add(path.Append(start).ToList());
                    closed = true;
                }
                else if (!blocked[target] && Circuit(target, start))
                {
                    closed = true;
                }
            }

            if (closed)
            {
                Unblock(node);
            }
            else
            {
                foreach (var target in adjacency[node])
                {
                    if (InScope(target, start)) blockedBy[target].Add(node);
                }
            }
            path.RemoveAt(path.Count - 1);
            return closed;
        }

        void Unblock(int node)
        {
            blocked[node] = false;
            foreach (var waiting in blockedBy[node].ToList())
            {
                blockedBy[node].Remove(waiting);
                if (blocked[waiting]) Unblock(waiting);
            }
        }
    }

    // Lexicographic order of two node sequences.
    private static readonly IComparer<List<int>> SequenceOrder = Comparer<List<int>>.Create((x, y) =>
    {
        for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
        {
            var byNode = x[i].CompareTo(y[i]);
            if (byNode != 0) return byNode;
        }
        return x.Count.CompareTo(y.Count);
    });
}
