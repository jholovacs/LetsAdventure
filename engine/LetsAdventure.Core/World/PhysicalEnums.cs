namespace LetsAdventure.Core.World;

/// <summary>Surface / contact material for simulation and nav cost hints.</summary>
public enum SurfaceComposition
{
    Unknown = 0,
    Soil,
    Rock,
    Sand,
    Ice,
    Mud,
    Grass,
    ForestFloor,
    Pavement,
    BuildingInterior,
    Water,
    Air,
}

/// <summary>Macro category for volumetric queries (sky vs ground vs built mass).</summary>
public enum PhysicalMedium
{
    Solid,
    FluidStanding,
    FluidFlowing,
    GroundSurface,
    VegetationVolume,
    OpenAir,
}

/// <summary>Vertical / life-form strata present in a cell (bitmask for variety).</summary>
[Flags]
public enum VegetationStratum
{
    None = 0,
    Grass = 1,
    Shrub = 2,
    Tree = 4,
    ForbWildflower = 8,
    WeedyHerb = 16,
}

/// <summary>Dominant vegetation assembly inferred from terrain, hydrology, and relief.</summary>
public enum VegetationCommunityKind
{
    None = 0,
    /// <summary>Rock, ice, or high slope — sparse shrubs, forbs, weeds.</summary>
    AlpineSparse,
    /// <summary>Open plains — dense grasses and shrubs.</summary>
    PlainsHerbaceous,
    /// <summary>Near fresh water — higher density, shrubs and trees.</summary>
    RiparianWoodland,
    /// <summary>Lower, moist colluvium — forest / woodland without standing water.</summary>
    LowlandForest,
    /// <summary>Sand / beach — scattered grasses and salt-tolerant scrub.</summary>
    CoastalSandSparse,
    /// <summary>Fallback mixed herbaceous / young woodland.</summary>
    TransitionMixed,
}
