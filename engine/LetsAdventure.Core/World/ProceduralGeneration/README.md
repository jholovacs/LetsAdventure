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
| `ProceduralPhysicalWorldGenerator.RiverStamping.cs` | River half-widths, `StampRiverCorridor`, channel bed carve / smooth river-only, polylines. Optional **Vulkan** carve: `World/Gpu/RiverChannelCarveVulkan.cs` + `World/Gpu/Shaders/river_channel_carve.comp`. |
| `ProceduralPhysicalWorldGenerator.ChannelRouting.cs` | Vertex hydraulics (slope, width curve), meander wiggle, **lake outlet** connections to rivers. |
| `ProceduralPhysicalWorldGenerator.RiverDeltas.cs` | Coastal distributaries and sandbar stripes. |
| `ProceduralPhysicalWorldGenerator.OceanLakeBed.cs` | Ocean surface Z estimate, ocean floor carve, lake water levels, lake bed carve. |
| `ProceduralPhysicalWorldGenerator.WaterSurfaces.cs` | Final water-surface Z for ocean/lakes/rivers, neighbor consistency. |
| `ProceduralPhysicalWorldGenerator.Validation.cs` | Water connectivity vs ocean, lake basin reachability, downstream centerline gradients. |
| `ProceduralPhysicalWorldGenerator.NavGrid.cs` | **Navigation grid**: dry-gradient field, cell walkability, composition, slope violation stats. |
| `ProceduralPhysicalWorldGenerator.CoastalFeatures.cs` | `PhysicalTerrainFeature` rivers/lakes for serialization / tooling. |

All `ProceduralPhysicalWorldGenerator.*.cs` files are **`partial`** slices of the same static class so shared private helpers (`ParallelForCols`, `Report`, flow markers, `DrainageField`, …) stay in one logical type.

## Mountains (`ApplyRegionalRuggednessMountainsAndRidges`)

Discrete massifs use a **right circular cone** in world meters: flank slope toward the summit is **constant** \(H/R\) in the noise-free profile, capped so \(H \cdot s_{\min}/R \ge \tan(\theta)\) with \(s_{\min}\)=0.76 (conservative spikey multiplier) and \(\theta\) = `MountainMinInclineTowardPeakDegrees` (default **30°**).

When `MaxZBound − MountainPeakClearanceBelowMaxZM` > `MountainPeakMinAbsoluteZM`, summits are **pinned** so ridgeline Z lies in **[MountainPeakMinAbsoluteZM, MaxZBound − clearance]** (default **1000 … MaxZ−100** m), accounting for spikey variation (0.76…1.24×). If the vertical band is infeasible (e.g. small `MaxZBound` editor worlds), only lift scale + slope geometry apply.


1. **Land heightfield** — noise + dome + ruggedness + mountains; orthogonal slope caps.  
2. **Ocean mask + shelf** — edge band vs terrain; land smoothing (frozen ocean/mountains).  
3. **Depression lakes** — bowl-based inland lakes; slope pass.  
4. **Drainage** — D8 directions + contributing area; **channel network** from terrain; optional **river-corridor pools**; lake outlets; deltas.  
5. **Water geometry** — creep, lake/river beds, finalized surfaces; land slope + valley bias; post smooth.  
6. **Validation + nav grid** — connectivity checks, then `TerrainNavGridDefinition` cells.

## Performance & progress

- **Embarrassingly parallel** grid loops use `ParallelFor` / `ParallelForCols` / `ParallelForRows` when `cols×rows` exceeds an internal threshold.  
- For large grids, **`ExpectLongRunningGridPhase`** gates extra `IProgress<string>` detail so small worlds stay quiet.

## River channel bed carve — optional Vulkan compute

After river corridors are stamped, **channel bed carving** (`CarveAllRiverChannelBeds` in `ProceduralPhysicalWorldGenerator.RiverStamping.cs`) may run on **Vulkan 1.1+** via `RiverChannelCarveVulkan` (Silk.NET.Vulkan + Silk.NET.Shaderc). The GLSL source is embedded and compiled to SPIR-V at runtime; no Vulkan SDK is required to build the project.

**When it runs**

- Only on **Windows, Linux, and macOS** (other `OperatingSystem` kinds skip the GPU path entirely).
- Only if initialization succeeds: Vulkan loader, an ICD/driver with a **compute** queue, and host-visible memory for the staging buffers used by this path.

**When it does not**

- Set environment variable **`LETSADVENTURE_VULKAN_RIVER=0`** to force the historical CPU path.
- If GPU setup fails for any reason, the same code **falls back silently** to the CPU implementation (spatial bucket index + column parallelism).

**Environment notes**

- **Linux:** install a Vulkan loader and a suitable ICD (e.g. Mesa for AMD/Intel; proprietary stacks for NVIDIA). Headless or CI machines need a real GPU stack or a software implementation where your distro documents it.
- **macOS:** works when the system Vulkan loader can load a compatible implementation (e.g. **MoltenVK**), per your local setup.
- **Numerics:** the GPU carve uses **32-bit floats** for heights and world coordinates in that pass; the CPU path uses **double**. Heights may differ slightly where the carve adjusts terrain.

## Entry points

- **`ProceduralPhysicalWorldGenerator.Generate(spec, progress)`** — used by the world editor and `BaselinePhysicalWorldGenerator`.
