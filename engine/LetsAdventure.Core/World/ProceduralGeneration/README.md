# Procedural coastal world generation

This folder contains **authoring-time** synthesis of heightfields, hydrology, standing water, and the navigation grid for coastal “perimeter ocean” worlds. Units are **SI meters** (see `ProceduralWorldSpec`).

## File map

| File | Responsibility |
|------|------------------|
| `ProceduralWorldSpec.cs` | Tunable inputs: terrain, ocean band, drainage/lake knobs, hydrology, shelf, etc. |
| `PhysicalWorldGenerationModels.cs` | `PhysicalWorldGenerationReport` (quality metrics, messages) and `PhysicalWorldGenerationResult`. |
| `ProceduralPhysicalWorldGenerator.Core.cs` | **`Generate`** → `GenerateCoastalOceanDrainage`: phase ordering, progress messages, world JSON shell. |
| `ProceduralPhysicalWorldGenerator.TerrainHeight.cs` | Base FBM heightfield, continental dome, ridges, regional ruggedness / mountains, noise helpers. |
| `ProceduralPhysicalWorldGenerator.LandSlope.cs` | Global max orthogonal slope relaxation (`EnforceMaxOrthogonalSlope`). |
| `ProceduralPhysicalWorldGenerator.Smoothing.cs` | Masked height smoothing, river valley land bias, closest-point-on-path helpers. |
| `ProceduralPhysicalWorldGenerator.LakesAndShelf.cs` | Perimeter ocean mask, coastal shelf, **contour depression lakes**, river-bowl pool pass, lake bowl geometry, distance-to-ocean grid. |
| `ProceduralPhysicalWorldGenerator.DrainageNetwork.cs` | D8 **flow + accumulation**, dendritic channel tracing, corridor stamping. |
| `ProceduralPhysicalWorldGenerator.RiverStamping.cs` | River half-widths, `StampRiverCorridor`, channel bed carve / smooth river-only, polylines. |
| `ProceduralPhysicalWorldGenerator.ChannelRouting.cs` | Vertex hydraulics (slope, width curve), meander wiggle, **lake outlet** connections to rivers. |
| `ProceduralPhysicalWorldGenerator.RiverDeltas.cs` | Coastal distributaries and sandbar stripes. |
| `ProceduralPhysicalWorldGenerator.OceanLakeBed.cs` | Ocean surface Z estimate, ocean floor carve, lake water levels, lake bed carve. |
| `ProceduralPhysicalWorldGenerator.WaterSurfaces.cs` | Final water-surface Z for ocean/lakes/rivers, neighbor consistency. |
| `ProceduralPhysicalWorldGenerator.Validation.cs` | Water connectivity vs ocean, lake basin reachability, downstream centerline gradients. |
| `ProceduralPhysicalWorldGenerator.NavGrid.cs` | **Navigation grid**: dry-gradient field, cell walkability, composition, slope violation stats. |
| `ProceduralPhysicalWorldGenerator.CoastalFeatures.cs` | `PhysicalTerrainFeature` rivers/lakes for serialization / tooling. |

All `ProceduralPhysicalWorldGenerator.*.cs` files are **`partial`** slices of the same static class so shared private helpers (`ParallelForCols`, `Report`, flow markers, `DrainageField`, …) stay in one logical type.

## Pipeline (high level)

1. **Land heightfield** — noise + dome + ruggedness + mountains; orthogonal slope caps.  
2. **Ocean mask + shelf** — edge band vs terrain; land smoothing (frozen ocean/mountains).  
3. **Depression lakes** — bowl-based inland lakes; slope pass.  
4. **Drainage** — D8 directions + contributing area; **channel network** from terrain; optional **river-corridor pools**; lake outlets; deltas.  
5. **Water geometry** — creep, lake/river beds, finalized surfaces; land slope + valley bias; post smooth.  
6. **Validation + nav grid** — connectivity checks, then `TerrainNavGridDefinition` cells.

## Performance & progress

- **Embarrassingly parallel** grid loops use `ParallelFor` / `ParallelForCols` / `ParallelForRows` when `cols×rows` exceeds an internal threshold.  
- For large grids, **`ExpectLongRunningGridPhase`** gates extra `IProgress<string>` detail so small worlds stay quiet.

## Entry points

- **`ProceduralPhysicalWorldGenerator.Generate(spec, progress)`** — used by the world editor and `BaselinePhysicalWorldGenerator`.
