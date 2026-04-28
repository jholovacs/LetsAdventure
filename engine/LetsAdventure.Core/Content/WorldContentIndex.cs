namespace LetsAdventure.Core.Content;

/// <summary>
/// Declares how world content is split for scalable loading (<c>world/content_index.json</c> at the content root).
/// </summary>
public sealed class WorldContentIndex
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>When false, <c>world/npc_instances.json</c> is not loaded at core (NPCs come from region bundles only).</summary>
    public bool LoadGlobalNpcInstancesFile { get; set; } = true;

    /// <summary>When false, <c>world/establishments.json</c> is skipped at core.</summary>
    public bool LoadGlobalEstablishmentsFile { get; set; } = true;

    public bool IncludeGlobalEventDirectory { get; set; } = true;

    public bool IncludeGlobalQuestDirectory { get; set; } = true;

    /// <summary>Optional extra event JSON paths relative to content root (always loaded with core).</summary>
    public List<string> CoreEventFiles { get; set; } = [];

    /// <summary>Optional extra quest JSON paths relative to content root.</summary>
    public List<string> CoreQuestFiles { get; set; } = [];

    public List<RegionContentRef> Regions { get; set; } = [];
}

/// <summary>Per-region payload locations (paths relative to content root unless rooted from <see cref="Directory"/>).</summary>
public sealed class RegionContentRef
{
    public string LoreRegionId { get; set; } = "";

    /// <summary>Subfolder under content root, e.g. <c>world/regions/jade_threshold</c>.</summary>
    public string Directory { get; set; } = "";

    /// <summary>File name inside <see cref="Directory"/>; default <c>npcs.json</c>.</summary>
    public string NpcInstancesFile { get; set; } = "npcs.json";

    public string? EstablishmentsFile { get; set; } = "establishments.json";

    /// <summary>Optional partial <see cref="World.PhysicalWorldDefinition"/> merged onto core physical world.</summary>
    public string? PhysicalOverlayFile { get; set; } = "physical.json";

    public List<string> EventFiles { get; set; } = [];

    public List<string> QuestFiles { get; set; } = [];
}
