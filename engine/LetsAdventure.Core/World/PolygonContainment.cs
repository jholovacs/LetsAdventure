namespace LetsAdventure.Core.World;

public static class PolygonContainment
{
    /// <summary>Point-in-polygon on the horizontal plane (X/Y).</summary>
    public static bool ContainsXy(IReadOnlyList<GeoVec2> vertices, double x, double y)
    {
        if (vertices.Count < 3)
            return false;

        var inside = false;
        for (int i = 0, j = vertices.Count - 1; i < vertices.Count; j = i++)
        {
            var xi = vertices[i].X;
            var yi = vertices[i].Y;
            var xj = vertices[j].X;
            var yj = vertices[j].Y;
            var intersect = yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi;
            if (intersect)
                inside = !inside;
        }

        return inside;
    }

    public static bool Contains(PolygonColumnBounds column, double x, double y, double z) =>
        z >= column.ZMin && z <= column.ZMax && ContainsXy(column.Vertices, x, y);
}
