namespace LetsAdventure.Core.World;

/// <summary>Merges a base physical world with regional overlays (features, boundaries, territories, nav).</summary>
public static class PhysicalWorldMerger
{
    public static PhysicalWorldDefinition Merge(
        PhysicalWorldDefinition? core,
        IEnumerable<PhysicalWorldDefinition> overlays)
    {
        core ??= new PhysicalWorldDefinition();
        var merged = CloneShallow(core);

        foreach (var o in overlays)
        {
            merged.RegionBoundaries.AddRange(o.RegionBoundaries);
            merged.Territories.AddRange(o.Territories);
            merged.Features.AddRange(o.Features);
            MergeNavigation(merged.Navigation, o.Navigation);
        }

        return merged;
    }

    private static PhysicalWorldDefinition CloneShallow(PhysicalWorldDefinition src)
    {
        var nav = src.Navigation;
        var graph = nav.Graph is null
            ? null
            : new NavigationGraphDefinition
            {
                Nodes = [.. nav.Graph.Nodes],
                Edges = [.. nav.Graph.Edges],
            };
        TerrainNavGridDefinition? grid = null;
        if (nav.Grid is { } g)
        {
            grid = new TerrainNavGridDefinition
            {
                OriginX = g.OriginX,
                OriginY = g.OriginY,
                CellSize = g.CellSize,
                Columns = g.Columns,
                Rows = g.Rows,
                Cells = g.Cells is { Count: > 0 } c ? [.. c] : null,
            };
        }

        return new PhysicalWorldDefinition
        {
            SchemaVersion = src.SchemaVersion,
            CoordinateDescription = src.CoordinateDescription,
            GlobalBounds = new AxisAlignedBounds { Min = src.GlobalBounds.Min, Max = src.GlobalBounds.Max },
            RegionBoundaries = [.. src.RegionBoundaries],
            Territories = [.. src.Territories],
            Features = [.. src.Features],
            Navigation = new NavigationBundle { Graph = graph, Grid = grid },
        };
    }

    private static void MergeNavigation(NavigationBundle into, NavigationBundle? from)
    {
        if (from is null)
            return;
        into.Graph ??= new NavigationGraphDefinition();
        if (from.Graph is { } fg)
        {
            into.Graph.Nodes.AddRange(fg.Nodes);
            into.Graph.Edges.AddRange(fg.Edges);
        }

        if (from.Grid is not null && into.Grid is null)
            into.Grid = from.Grid;
    }
}
