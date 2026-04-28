using System.Collections.ObjectModel;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.World;

namespace LetsAdventure.WorldEditor;

public sealed class NavNodeRow
{
    public string Id { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string TagsCsv { get; set; } = "";

    public static NavNodeRow From(NavigationNodeDefinition n) => new()
    {
        Id = n.Id,
        X = n.Position.X,
        Y = n.Position.Y,
        Z = n.Position.Z,
        TagsCsv = string.Join(", ", n.Tags),
    };

    public NavigationNodeDefinition ToDef() => new()
    {
        Id = Id,
        Position = new Vec3 { X = X, Y = Y, Z = Z },
        Tags = TagsCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
    };
}

public sealed class NavEdgeRow
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public double TraverseCost { get; set; } = 1;
    public bool Bidirectional { get; set; } = true;

    public static NavEdgeRow From(NavigationEdgeDefinition e) => new()
    {
        FromId = e.FromId,
        ToId = e.ToId,
        TraverseCost = e.TraverseCost,
        Bidirectional = e.Bidirectional,
    };

    public NavigationEdgeDefinition ToDef() => new()
    {
        FromId = FromId,
        ToId = ToId,
        TraverseCost = TraverseCost,
        Bidirectional = Bidirectional,
    };
}

public static class NavRowSync
{
    public static void LoadGraph(NavigationGraphDefinition? graph, ObservableCollection<NavNodeRow> nodes, ObservableCollection<NavEdgeRow> edges)
    {
        nodes.Clear();
        edges.Clear();
        if (graph is null)
            return;
        foreach (var n in graph.Nodes)
            nodes.Add(NavNodeRow.From(n));
        foreach (var e in graph.Edges)
            edges.Add(NavEdgeRow.From(e));
    }

    public static void SaveGraph(NavigationGraphDefinition graph, ObservableCollection<NavNodeRow> nodes, ObservableCollection<NavEdgeRow> edges)
    {
        graph.Nodes.Clear();
        graph.Edges.Clear();
        foreach (var r in nodes)
            graph.Nodes.Add(r.ToDef());
        foreach (var r in edges)
            graph.Edges.Add(r.ToDef());
    }
}
