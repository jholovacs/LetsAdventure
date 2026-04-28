namespace LetsAdventure.Core.Social;

public sealed class FactionDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Tags { get; set; } = [];
}
