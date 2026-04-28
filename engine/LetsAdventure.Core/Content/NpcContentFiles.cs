using LetsAdventure.Core.Npcs;

namespace LetsAdventure.Core.Content;

internal sealed class EstablishmentListFile
{
    public List<Establishment> Establishments { get; set; } = [];
}

internal sealed class NpcTemplateListFile
{
    public List<NpcTemplateData> Templates { get; set; } = [];
}

internal sealed class NpcInstanceListFile
{
    public List<NpcInstanceProfile> Instances { get; set; } = [];
}
