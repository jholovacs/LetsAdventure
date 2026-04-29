using System.IO;
using System.Text.Json;

namespace LetsAdventure.WorldEditor.Services;

/// <summary>Map preview scroll/zoom/raster step stored per physical_world.json path.</summary>
public sealed class MapPreviewViewState
{
    public double HorizontalOffset { get; set; }
    public double VerticalOffset { get; set; }
    public double Zoom { get; set; } = 1;
    public double RasterCellPx { get; set; } = 3;
}

/// <summary>Persisted editor settings under LocalApplicationData/LetsAdventure/WorldEditor.</summary>
public sealed class WorldEditorPreferences
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Last folder used for baseline output (physical_world.json + .navgrid).</summary>
    public string? BaselineOutputDirectory { get; set; }

    /// <summary>Last opened/saved physical_world.json (full path). Used for map folder defaults across sessions.</summary>
    public string? LastOpenedWorldPath { get; set; }

    /// <summary>Map preview state keyed by full path to physical_world.json.</summary>
    public Dictionary<string, MapPreviewViewState>? MapPreviewByWorldPath { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LetsAdventure", "WorldEditor", "preferences.json");

    public static WorldEditorPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var p = JsonSerializer.Deserialize<WorldEditorPreferences>(json, JsonOpts);
                if (p is null)
                    return new WorldEditorPreferences();
                p.MapPreviewByWorldPath = NormalizeMapPreviewKeys(p.MapPreviewByWorldPath);
                return p;
            }
        }
        catch
        {
            // ignore corrupt or partial file
        }

        return new WorldEditorPreferences();
    }

    private static Dictionary<string, MapPreviewViewState> NormalizeMapPreviewKeys(
        Dictionary<string, MapPreviewViewState>? raw)
    {
        var norm = new Dictionary<string, MapPreviewViewState>(StringComparer.OrdinalIgnoreCase);
        if (raw is null)
            return norm;
        foreach (var kv in raw)
        {
            try
            {
                norm[Path.GetFullPath(kv.Key)] = kv.Value;
            }
            catch
            {
                // skip invalid paths
            }
        }

        return norm;
    }

    public bool TryGetMapPreviewState(string worldJsonPath, out MapPreviewViewState state)
    {
        state = null!;
        MapPreviewByWorldPath ??= new Dictionary<string, MapPreviewViewState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var key = Path.GetFullPath(worldJsonPath);
            if (MapPreviewByWorldPath.TryGetValue(key, out var s) && s is not null)
            {
                state = s;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    public void SetMapPreviewState(string worldJsonPath, MapPreviewViewState viewState)
    {
        MapPreviewByWorldPath ??= new Dictionary<string, MapPreviewViewState>(StringComparer.OrdinalIgnoreCase);
        var key = Path.GetFullPath(worldJsonPath);
        MapPreviewByWorldPath[key] = viewState;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        MapPreviewByWorldPath = NormalizeMapPreviewKeys(MapPreviewByWorldPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>Folder to pre-select in the baseline output dialog (existing path when possible).</summary>
    public string GetBaselineOutputDirectoryInitialSuggestion()
    {
        if (!string.IsNullOrEmpty(BaselineOutputDirectory))
        {
            try
            {
                var full = Path.GetFullPath(BaselineOutputDirectory);
                if (Directory.Exists(full))
                    return full;
            }
            catch
            {
                // fall through
            }
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LetsAdventure", "WorldEditor", "baseline-output");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
