namespace LetsAdventure.Core.World;

/// <summary>Runtime sampling of nav grid cells when the full <see cref="TerrainNavGridDefinition.Cells"/> list is not in memory.</summary>
public interface INavGridCellSource
{
    bool TryGetCell(int col, int row, out NavCellDefinition cell);
}
