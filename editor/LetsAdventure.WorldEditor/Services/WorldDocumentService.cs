using System.IO;
using System.Text.Json;
using LetsAdventure.Core.Json;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.World;

namespace LetsAdventure.WorldEditor.Services;

public static class WorldDocumentService
{
    public static PhysicalWorldDefinition CreateNew() =>
        new()
        {
            SchemaVersion = 1,
            CoordinateDescription =
                "X/Y horizontal plane, Z vertical (up). 1 unit = 1 m (SI). Sea level = (MinZ+MaxZ)/2. Ground level is height above sea level for shoreline and land/water contours.",
            GlobalBounds = new AxisAlignedBounds
            {
                Min = new Vec3 { Z = BaselinePhysicalWorldGenerator.DefaultMinZ },
                Max = new Vec3
                {
                    X = BaselinePhysicalWorldGenerator.DefaultExtentXy,
                    Y = BaselinePhysicalWorldGenerator.DefaultExtentXy,
                    Z = BaselinePhysicalWorldGenerator.DefaultMaxZ,
                },
            },
            Navigation = new NavigationBundle
            {
                Graph = new NavigationGraphDefinition(),
                Grid = new TerrainNavGridDefinition
                {
                    CellSize = 4,
                    Columns = 32,
                    Rows = 32,
                },
            },
        };

    public static PhysicalWorldDefinition Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PhysicalWorldDefinition>(json, GameJson.Options)
            ?? throw new InvalidOperationException("Invalid physical world JSON.");
    }

    public static void Save(string path, PhysicalWorldDefinition world)
    {
        world.NavGridCellSource = null;
        var json = JsonSerializer.Serialize(world, GameJson.Options);
        File.WriteAllText(path, json);
    }

    public static void SaveFeaturesOverlay(string path, IReadOnlyList<PhysicalTerrainFeature> features)
    {
        var json = JsonSerializer.Serialize(features, GameJson.Options);
        File.WriteAllText(path, json);
    }
}
