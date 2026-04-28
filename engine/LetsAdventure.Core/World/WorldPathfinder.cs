using LetsAdventure.Core.Simulation;

namespace LetsAdventure.Core.World;

/// <summary>A* pathfinding on an explicit nav graph or a uniform terrain grid.</summary>
public static class WorldPathfinder
{
    public static List<string>? FindPathOnGraph(NavigationGraphDefinition graph, string startId, string goalId)
    {
        if (string.Equals(startId, goalId, StringComparison.Ordinal))
            return [startId];

        var positions = graph.Nodes.ToDictionary(n => n.Id, n => n.Position, StringComparer.Ordinal);
        var adj = BuildAdjacency(graph);
        if (!adj.ContainsKey(startId) || !adj.ContainsKey(goalId))
            return null;

        var open = new PriorityQueue<string, double>();
        var gScore = new Dictionary<string, double>(StringComparer.Ordinal);
        var cameFrom = new Dictionary<string, string>(StringComparer.Ordinal);
        var closed = new HashSet<string>(StringComparer.Ordinal);

        double H(string id)
        {
            if (!positions.TryGetValue(id, out var p) || !positions.TryGetValue(goalId, out var g))
                return 0;
            return Distance(p, g);
        }

        gScore[startId] = 0;
        open.Enqueue(startId, H(startId));

        while (open.TryDequeue(out var current, out _))
        {
            if (!closed.Add(current))
                continue;
            if (string.Equals(current, goalId, StringComparison.Ordinal))
                return ReconstructIds(cameFrom, current);

            var baseG = gScore[current];
            foreach (var (next, stepCost) in adj[current])
            {
                var tentative = baseG + stepCost;
                if (!gScore.TryGetValue(next, out var prev) || tentative < prev)
                {
                    cameFrom[next] = current;
                    gScore[next] = tentative;
                    open.Enqueue(next, tentative + H(next));
                }
            }
        }

        return null;
    }

    public static List<Vec3>? FindPathOnGrid(
        TerrainNavGrid grid,
        Vec3 startWorld,
        Vec3 goalWorld,
        bool allowDiagonal = true)
    {
        if (!grid.TryWorldToCell(startWorld.X, startWorld.Y, out var sc, out var sr)
            || !grid.TryWorldToCell(goalWorld.X, goalWorld.Y, out var gc, out var gr))
            return null;

        if (!grid.Cell(sc, sr).Walkable || !grid.Cell(gc, gr).Walkable)
            return null;

        if (sc == gc && sr == gr)
            return [startWorld];

        var open = new PriorityQueue<(int c, int r), double>();
        var gScore = new Dictionary<(int, int), double>();
        var cameFrom = new Dictionary<(int, int), (int, int)>();
        var closed = new HashSet<(int, int)>();

        static double H(TerrainNavGrid g, int c, int r, int gc, int gr)
        {
            var a = g.CellCenter(c, r);
            var b = g.CellCenter(gc, gr);
            return Distance(a, b);
        }

        gScore[(sc, sr)] = 0;
        open.Enqueue((sc, sr), H(grid, sc, sr, gc, gr));

        while (open.TryDequeue(out var cur, out _))
        {
            if (!closed.Add(cur))
                continue;
            var (cc, cr) = cur;
            if (cc == gc && cr == gr)
                return ReconstructGridPath(grid, cameFrom, cur, startWorld, goalWorld);

            var baseG = gScore[cur];
            for (var d = 0; d < 9; d++)
            {
                var dc = d % 3 - 1;
                var dr = d / 3 - 1;
                if (dc == 0 && dr == 0)
                    continue;
                if (!allowDiagonal && Math.Abs(dc) + Math.Abs(dr) != 1)
                    continue;

                var nc = cc + dc;
                var nr = cr + dr;
                if (!grid.InBounds(nc, nr))
                    continue;
                var cell = grid.Cell(nc, nr);
                if (!cell.Walkable)
                    continue;

                var stepDist = grid.CellSize * (Math.Abs(dc) + Math.Abs(dr) == 2 ? Math.Sqrt(2) : 1);
                var mult = 0.5 * (grid.Cell(cc, cr).MovementCostMultiplier + cell.MovementCostMultiplier);
                var tentative = baseG + stepDist * mult;
                var key = (nc, nr);
                if (!gScore.TryGetValue(key, out var prev) || tentative < prev)
                {
                    cameFrom[key] = cur;
                    gScore[key] = tentative;
                    open.Enqueue(key, tentative + H(grid, nc, nr, gc, gr));
                }
            }
        }

        return null;
    }

    private static Dictionary<string, List<(string to, double cost)>> BuildAdjacency(NavigationGraphDefinition g)
    {
        var d = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);
        foreach (var n in g.Nodes)
            d[n.Id] = [];

        void Add(string a, string b, double c)
        {
            d[a].Add((b, c));
        }

        foreach (var e in g.Edges)
        {
            if (!d.ContainsKey(e.FromId) || !d.ContainsKey(e.ToId))
                continue;
            Add(e.FromId, e.ToId, Math.Max(1e-6, e.TraverseCost));
            if (e.Bidirectional)
                Add(e.ToId, e.FromId, Math.Max(1e-6, e.TraverseCost));
        }

        return d;
    }

    private static List<string> ReconstructIds(Dictionary<string, string> cameFrom, string current)
    {
        var path = new List<string> { current };
        while (cameFrom.TryGetValue(current, out var prev))
        {
            current = prev;
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    private static List<Vec3> ReconstructGridPath(
        TerrainNavGrid grid,
        Dictionary<(int, int), (int, int)> cameFrom,
        (int c, int r) current,
        Vec3 startWorld,
        Vec3 goalWorld)
    {
        var cells = new List<(int c, int r)> { current };
        while (cameFrom.TryGetValue(current, out var prev))
        {
            current = prev;
            cells.Add(current);
        }

        cells.Reverse();
        var waypoints = new List<Vec3>(cells.Count + 2);
        waypoints.Add(startWorld);
        foreach (var (c, r) in cells)
            waypoints.Add(grid.CellCenter(c, r));
        waypoints.Add(goalWorld);
        return waypoints;
    }

    private static double Distance(Vec3 a, Vec3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
