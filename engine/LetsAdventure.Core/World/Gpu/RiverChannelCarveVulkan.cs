using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;

namespace LetsAdventure.Core.World;

/// <summary>
/// Optional Vulkan 1.1+ compute paths for river grid work (channel bed carve, river water-surface Z).
/// Falls back silently when Vulkan/shaderc is unavailable. Disable with env <c>LETSADVENTURE_VULKAN_RIVER=0</c>.
/// </summary>
internal static unsafe class RiverChannelCarveVulkan
{
    internal const string DisableEnv = "LETSADVENTURE_VULKAN_RIVER";
    private const string CompResourceCarve = "LetsAdventure.Core.river_channel_carve.comp";
    private const string CompResourceWaterSurf = "LetsAdventure.Core.river_water_surface.comp";

    private const uint WorkgroupSize = 16;

    private static readonly object InitLock = new();
    private static bool _initFailed;
    private static byte[]? _spirvCarveCache;
    private static byte[]? _spirvWaterSurfCache;

    private static Vk? _vk;
    private static Instance _instance;
    private static PhysicalDevice _physicalDevice;
    private static Device _device;
    private static Queue _queue;
    private static uint _queueFamilyIndex;
    private static Pipeline _carvePipeline;
    private static Pipeline _waterSurfPipeline;
    private static PipelineLayout _pipelineLayout;
    private static DescriptorSetLayout _descriptorSetLayout;
    private static ShaderModule _carveShaderModule;
    private static ShaderModule _waterSurfShaderModule;
    private static CommandPool _commandPool;
    private static bool _baseReady;
    private static bool _carvePipelineReady;
    private static bool _waterSurfPipelineReady;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct GpuCarveParams
    {
        public float MinX;
        public float MinY;
        public float Cell;
        public float OceanWaterZ;
        public float TerrainAmp;
        public float EffCarve;
        public float FallbackHalfW;
        public float CellTimes115;
        public int Cols;
        public int Rows;
        public int TilesX;
        public int TilesY;
        public int TileW;
        public int TileH;
        public int PathCount;
        public int Pad;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct GpuWaterSurfParams
    {
        public float MinX;
        public float MinY;
        public float Cell;
        public float OceanWaterZ;
        public float ZSurfMul;
        public int Cols;
        public int Rows;
        public int TilesX;
        public int TilesY;
        public int TileW;
        public int TileH;
        public int PathCount;
        public int Pad;
    }

    public static bool TryCarve(
        double[,] h,
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        List<List<(int c, int r)>> riverPaths,
        bool[,] isRiver,
        bool[,] isOcean,
        int[,] lakeId,
        double oceanWaterZ,
        double[,] riverCellHalfWidthWorld,
        double effCarve,
        double fallbackHalfW,
        double _hwMax,
        int expandCells,
        double[] pathLens,
        IProgress<string>? progress)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;
        if (string.Equals(Environment.GetEnvironmentVariable(DisableEnv), "0",
                StringComparison.OrdinalIgnoreCase))
            return false;

        var report = ProceduralPhysicalWorldGenerator.ExpectLongRunningGridPhase(cols, rows) &&
                     progress != null;
        if (report)
            ProceduralPhysicalWorldGenerator.Report(progress,
                "[coastal] Carving river channel beds (Vulkan compute + tile-culled segments)…");

        var sw = Stopwatch.StartNew();
        try
        {
            if (!RiverPathGpuGeometry.TryBuild(cols, rows, cell, spec, riverPaths, expandCells, pathLens,
                    out var segments, out var tileOffsets, out var tileSegIdx))
                return false;

            if (!EnsureCarvePipeline())
                return false;

            var vk = _vk!;
            var totalCells = (ulong)(cols * rows);
            var floatCount = totalCells;
            var heightBytes = floatCount * sizeof(float);
            var halfWBytes = floatCount * sizeof(float);
            var flagsBytes = floatCount * sizeof(uint);
            var segBytes = (ulong)(segments.Count * Marshal.SizeOf<RiverPathGpuGeometry.GpuRiverSeg>());
            var pathLenBytes = (ulong)(pathLens.Length * sizeof(float));
            var tileOffBytes = (ulong)(tileOffsets.Length * sizeof(uint));
            var tileIdxBytes = (ulong)(tileSegIdx.Length * sizeof(uint));
            var paramsBytes = (ulong)Marshal.SizeOf<GpuCarveParams>();

            var p = new GpuCarveParams
            {
                MinX = (float)spec.MinX,
                MinY = (float)spec.MinY,
                Cell = (float)cell,
                OceanWaterZ = (float)oceanWaterZ,
                TerrainAmp = (float)spec.TerrainAmplitude,
                EffCarve = (float)effCarve,
                FallbackHalfW = (float)fallbackHalfW,
                CellTimes115 = (float)(cell * 1.15),
                Cols = cols,
                Rows = rows,
                TilesX = (cols + RiverPathGpuGeometry.TileW - 1) / RiverPathGpuGeometry.TileW,
                TilesY = (rows + RiverPathGpuGeometry.TileH - 1) / RiverPathGpuGeometry.TileH,
                TileW = RiverPathGpuGeometry.TileW,
                TileH = RiverPathGpuGeometry.TileH,
                PathCount = riverPaths.Count,
                Pad = 0
            };

            if (!TryAllocateHostBuffer(vk, segBytes, out var bufSeg, out var memSeg))
                return false;
            if (!TryAllocateHostBuffer(vk, tileOffBytes, out var bufTileOff, out var memTileOff))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, tileIdxBytes, out var bufTileIdx, out var memTileIdx))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, pathLenBytes, out var bufPathLen, out var memPathLen))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, heightBytes, out var bufHeights, out var memHeights))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, halfWBytes, out var bufHalfW, out var memHalfW))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                ReleaseBuffer(vk, bufHeights, memHeights);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, flagsBytes, out var bufFlags, out var memFlags))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                ReleaseBuffer(vk, bufHeights, memHeights);
                ReleaseBuffer(vk, bufHalfW, memHalfW);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, paramsBytes, out var bufParams, out var memParams))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                ReleaseBuffer(vk, bufHeights, memHeights);
                ReleaseBuffer(vk, bufHalfW, memHalfW);
                ReleaseBuffer(vk, bufFlags, memFlags);
                return false;
            }

            try
            {
                CopySpan<RiverPathGpuGeometry.GpuRiverSeg>(memSeg,
                    CollectionsMarshal.AsSpan(segments));
                CopySpan<uint>(memTileOff, tileOffsets);
                CopySpan<uint>(memTileIdx, tileSegIdx);
                CopySpan<float>(memPathLen, floatsFromPathLens(pathLens));
                CopyHeights(memHeights, h, cols, rows);
                CopyHalfW(memHalfW, riverCellHalfWidthWorld, cols, rows);
                CopyFlags(memFlags, isRiver, isOcean, lakeId, cols, rows);
                CopyStruct(memParams, p);

                if (!RunOnce(vk, _carvePipeline, bufParams, bufSeg, bufTileOff, bufTileIdx, bufPathLen,
                        bufHeights, bufHalfW, bufFlags, cols, rows))
                    return false;

                ReadHeights(memHeights, h, cols, rows);
            }
            finally
            {
                ReleaseBuffer(vk, bufParams, memParams);
                ReleaseBuffer(vk, bufFlags, memFlags);
                ReleaseBuffer(vk, bufHalfW, memHalfW);
                ReleaseBuffer(vk, bufHeights, memHeights);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufSeg, memSeg);
            }

            if (report)
                ProceduralPhysicalWorldGenerator.Report(progress,
                    $"[coastal] River channel bed carve finished (Vulkan, {sw.Elapsed.TotalSeconds:0.0}s).");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryApplyRiverWaterSurfaceZ(
        double[,] h,
        int cols,
        int rows,
        double cell,
        ProceduralWorldSpec spec,
        List<List<(int c, int r)>> riverPaths,
        bool[,] isRiver,
        bool[,] isOcean,
        int[,] lakeId,
        double oceanWaterZ,
        int expandCells,
        double[] pathLens,
        IProgress<string>? progress)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;
        if (string.Equals(Environment.GetEnvironmentVariable(DisableEnv), "0",
                StringComparison.OrdinalIgnoreCase))
            return false;

        var report = ProceduralPhysicalWorldGenerator.ExpectLongRunningGridPhase(cols, rows) &&
                     progress != null;
        if (report)
            ProceduralPhysicalWorldGenerator.Report(progress,
                "[coastal] Water surfaces: river Z (Vulkan compute + tile-culled segments)…");

        var sw = Stopwatch.StartNew();
        try
        {
            if (!RiverPathGpuGeometry.TryBuild(cols, rows, cell, spec, riverPaths, expandCells, pathLens,
                    out var segments, out var tileOffsets, out var tileSegIdx))
                return false;

            if (!EnsureWaterSurfPipeline())
                return false;

            var vk = _vk!;
            var totalCells = (ulong)(cols * rows);
            var floatCount = totalCells;
            var heightBytes = floatCount * sizeof(float);
            const ulong dummyHalfWBytes = 16;
            var flagsBytes = floatCount * sizeof(uint);
            var segBytes = (ulong)(segments.Count * Marshal.SizeOf<RiverPathGpuGeometry.GpuRiverSeg>());
            var pathLenBytes = (ulong)(pathLens.Length * sizeof(float));
            var tileOffBytes = (ulong)(tileOffsets.Length * sizeof(uint));
            var tileIdxBytes = (ulong)(tileSegIdx.Length * sizeof(uint));
            var paramsBytes = (ulong)Marshal.SizeOf<GpuWaterSurfParams>();

            var tilesX = (cols + RiverPathGpuGeometry.TileW - 1) / RiverPathGpuGeometry.TileW;
            var tilesY = (rows + RiverPathGpuGeometry.TileH - 1) / RiverPathGpuGeometry.TileH;
            var wp = new GpuWaterSurfParams
            {
                MinX = (float)spec.MinX,
                MinY = (float)spec.MinY,
                Cell = (float)cell,
                OceanWaterZ = (float)oceanWaterZ,
                ZSurfMul = (float)Math.Max(0.55, spec.TerrainAmplitude * 0.07),
                Cols = cols,
                Rows = rows,
                TilesX = tilesX,
                TilesY = tilesY,
                TileW = RiverPathGpuGeometry.TileW,
                TileH = RiverPathGpuGeometry.TileH,
                PathCount = riverPaths.Count,
                Pad = 0
            };

            if (!TryAllocateHostBuffer(vk, segBytes, out var bufSeg, out var memSeg))
                return false;
            if (!TryAllocateHostBuffer(vk, tileOffBytes, out var bufTileOff, out var memTileOff))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, tileIdxBytes, out var bufTileIdx, out var memTileIdx))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, pathLenBytes, out var bufPathLen, out var memPathLen))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, heightBytes, out var bufHeights, out var memHeights))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, dummyHalfWBytes, out var bufHalfW, out var memHalfW))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                ReleaseBuffer(vk, bufHeights, memHeights);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, flagsBytes, out var bufFlags, out var memFlags))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                ReleaseBuffer(vk, bufHeights, memHeights);
                ReleaseBuffer(vk, bufHalfW, memHalfW);
                return false;
            }

            if (!TryAllocateHostBuffer(vk, paramsBytes, out var bufParams, out var memParams))
            {
                ReleaseBuffer(vk, bufSeg, memSeg);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                ReleaseBuffer(vk, bufHeights, memHeights);
                ReleaseBuffer(vk, bufHalfW, memHalfW);
                ReleaseBuffer(vk, bufFlags, memFlags);
                return false;
            }

            try
            {
                CopySpan<RiverPathGpuGeometry.GpuRiverSeg>(memSeg,
                    CollectionsMarshal.AsSpan(segments));
                CopySpan<uint>(memTileOff, tileOffsets);
                CopySpan<uint>(memTileIdx, tileSegIdx);
                CopySpan<float>(memPathLen, floatsFromPathLens(pathLens));
                CopyHeights(memHeights, h, cols, rows);
                CopyFlags(memFlags, isRiver, isOcean, lakeId, cols, rows);
                CopyStruct(memParams, wp);

                if (!RunOnce(vk, _waterSurfPipeline, bufParams, bufSeg, bufTileOff, bufTileIdx, bufPathLen,
                        bufHeights, bufHalfW, bufFlags, cols, rows))
                    return false;

                ReadHeights(memHeights, h, cols, rows);
            }
            finally
            {
                ReleaseBuffer(vk, bufParams, memParams);
                ReleaseBuffer(vk, bufFlags, memFlags);
                ReleaseBuffer(vk, bufHalfW, memHalfW);
                ReleaseBuffer(vk, bufHeights, memHeights);
                ReleaseBuffer(vk, bufPathLen, memPathLen);
                ReleaseBuffer(vk, bufTileIdx, memTileIdx);
                ReleaseBuffer(vk, bufTileOff, memTileOff);
                ReleaseBuffer(vk, bufSeg, memSeg);
            }

            if (report)
                ProceduralPhysicalWorldGenerator.Report(progress,
                    $"[coastal] Water surfaces: river Z finished (Vulkan, {sw.Elapsed.TotalSeconds:0.0}s).");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool EnsureCarvePipeline()
    {
        lock (InitLock)
        {
            if (_initFailed)
                return false;
            if (_carvePipelineReady)
                return true;
            try
            {
                if (_spirvCarveCache == null)
                {
                    _spirvCarveCache = LoadEmbeddedSpirv(CompResourceCarve, "river_channel_carve.comp");
                    if (_spirvCarveCache == null || _spirvCarveCache.Length == 0)
                        return false;
                }

                if (!TryEnsureVulkanBaseUnlocked())
                    return false;

                var api = _vk!;
                if (!TryCreateComputePipeline(api, _spirvCarveCache, out var pipe, out var sm))
                    return false;

                _carvePipeline = pipe;
                _carveShaderModule = sm;
                _carvePipelineReady = true;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool EnsureWaterSurfPipeline()
    {
        lock (InitLock)
        {
            if (_initFailed)
                return false;
            if (_waterSurfPipelineReady)
                return true;
            try
            {
                if (_spirvWaterSurfCache == null)
                {
                    _spirvWaterSurfCache = LoadEmbeddedSpirv(CompResourceWaterSurf, "river_water_surface.comp");
                    if (_spirvWaterSurfCache == null || _spirvWaterSurfCache.Length == 0)
                        return false;
                }

                if (!TryEnsureVulkanBaseUnlocked())
                    return false;

                var api = _vk!;
                if (!TryCreateComputePipeline(api, _spirvWaterSurfCache, out var pipe, out var sm))
                    return false;

                _waterSurfPipeline = pipe;
                _waterSurfShaderModule = sm;
                _waterSurfPipelineReady = true;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Caller must hold <see cref="InitLock"/>.</summary>
    private static bool TryEnsureVulkanBaseUnlocked()
    {
        if (_baseReady)
            return true;
        if (_initFailed)
            return false;

        try
        {
            var api = Vk.GetApi();
            _vk = api;

            ApplicationInfo appInfo = new()
            {
                SType = StructureType.ApplicationInfo,
                ApiVersion = Vk.Version12
            };

            InstanceCreateInfo instCi = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo
            };

            if (api.CreateInstance(in instCi, null, out var instance) != Result.Success)
            {
                _initFailed = true;
                return false;
            }

            _instance = instance;

            uint pdCount = 0;
            api.EnumeratePhysicalDevices(instance, &pdCount, null);
            if (pdCount == 0)
            {
                _initFailed = true;
                api.DestroyInstance(instance, null);
                return false;
            }

            var pds = new PhysicalDevice[pdCount];
            fixed (PhysicalDevice* pPds = pds)
                api.EnumeratePhysicalDevices(instance, &pdCount, pPds);

            PhysicalDevice chosenPd = default;
            uint chosenFam = 0;
            var bestScore = -1;

            foreach (var pd in pds)
            {
                api.GetPhysicalDeviceProperties(pd, out var props);
                uint qCount = 0;
                api.GetPhysicalDeviceQueueFamilyProperties(pd, &qCount, null);
                var qprops = new QueueFamilyProperties[qCount];
                fixed (QueueFamilyProperties* pQ = qprops)
                    api.GetPhysicalDeviceQueueFamilyProperties(pd, &qCount, pQ);

                uint computeFam = uint.MaxValue;
                for (var qi = 0u; qi < qCount; qi++)
                {
                    if ((qprops[(int)qi].QueueFlags & QueueFlags.ComputeBit) != 0)
                    {
                        computeFam = qi;
                        break;
                    }
                }

                if (computeFam == uint.MaxValue)
                    continue;

                var dt = (uint)props.DeviceType;
                var score = dt == 2u ? 300 : dt == 1u ? 200 : dt == 3u ? 100 : 0;
                if (score > bestScore)
                {
                    bestScore = score;
                    chosenPd = pd;
                    chosenFam = computeFam;
                }
            }

            if (bestScore < 0)
            {
                _physicalDevice = default;
                _initFailed = true;
                api.DestroyInstance(instance, null);
                return false;
            }

            _physicalDevice = chosenPd;
            _queueFamilyIndex = chosenFam;

            float qp = 1f;
            DeviceQueueCreateInfo dqci = new()
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = chosenFam,
                QueueCount = 1,
                PQueuePriorities = &qp
            };

            DeviceCreateInfo dci = new()
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &dqci
            };

            if (api.CreateDevice(_physicalDevice, in dci, null, out var device) != Result.Success)
            {
                _initFailed = true;
                api.DestroyInstance(instance, null);
                return false;
            }

            _device = device;
            api.GetDeviceQueue(device, chosenFam, 0, out _queue);

            var bindings = stackalloc DescriptorSetLayoutBinding[8];
            for (var i = 0; i < 8; i++)
                bindings[i] = new DescriptorSetLayoutBinding
                {
                    Binding = (uint)i,
                    DescriptorType = DescriptorType.StorageBuffer,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.ComputeBit
                };

            DescriptorSetLayoutCreateInfo dslCi = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 8,
                PBindings = bindings
            };

            DescriptorSetLayout dsLayout;
            if (api.CreateDescriptorSetLayout(device, in dslCi, null, out dsLayout) != Result.Success)
            {
                _initFailed = true;
                api.DestroyDevice(device, null);
                api.DestroyInstance(instance, null);
                return false;
            }

            _descriptorSetLayout = dsLayout;

            PipelineLayoutCreateInfo plCi = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &dsLayout
            };

            if (api.CreatePipelineLayout(device, in plCi, null, out _pipelineLayout) != Result.Success)
            {
                _initFailed = true;
                api.DestroyDescriptorSetLayout(device, dsLayout, null);
                api.DestroyDevice(device, null);
                api.DestroyInstance(instance, null);
                return false;
            }

            CommandPoolCreateInfo cpPoolCi = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = chosenFam
            };

            if (api.CreateCommandPool(device, in cpPoolCi, null, out _commandPool) != Result.Success)
            {
                _initFailed = true;
                api.DestroyPipelineLayout(device, _pipelineLayout, null);
                api.DestroyDescriptorSetLayout(device, dsLayout, null);
                api.DestroyDevice(device, null);
                api.DestroyInstance(instance, null);
                return false;
            }

            _baseReady = true;
            return true;
        }
        catch
        {
            _initFailed = true;
            return false;
        }
    }

    private static bool TryCreateComputePipeline(Vk api, byte[] spirv, out Pipeline pipeline,
        out ShaderModule shaderModule)
    {
        pipeline = default;
        shaderModule = default;
        var device = _device;

        ShaderModuleCreateInfo smCi;
        fixed (byte* pCode = spirv)
        {
            smCi = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)pCode
            };
        }

        if (api.CreateShaderModule(device, in smCi, null, out shaderModule) != Result.Success)
            return false;

        nint pMain = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);
        try
        {
            PipelineShaderStageCreateInfo stage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName = (byte*)pMain
            };

            ComputePipelineCreateInfo cpCi = new()
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _pipelineLayout
            };

            if (api.CreateComputePipelines(device, default, 1, in cpCi, null, out pipeline) != Result.Success)
            {
                api.DestroyShaderModule(device, shaderModule, null);
                shaderModule = default;
                return false;
            }
        }
        finally
        {
            SilkMarshal.Free(pMain);
        }

        return true;
    }

    private static byte[]? LoadEmbeddedSpirv(string resourceLogicalName, string debugName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceLogicalName);
        if (stream == null)
            return null;
        using var sr = new StreamReader(stream, Encoding.UTF8);
        var glsl = sr.ReadToEnd();
        return CompileGlsl(glsl, debugName);
    }

    private static byte[]? CompileGlsl(string glsl, string debugName)
    {
        var api = Shaderc.GetApi();
        var compiler = api.CompilerInitialize();
        var options = api.CompileOptionsInitialize();
        api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)Vk.Version12);
        api.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
        var utf8 = Encoding.UTF8.GetBytes(glsl);
        CompilationResult* result = null;
        try
        {
            fixed (byte* src = utf8)
            {
                result = api.CompileIntoSpv(compiler, src, (nuint)utf8.Length, ShaderKind.GlslComputeShader,
                    debugName, "main", options);
            }

            if (api.ResultGetCompilationStatus(result) != CompilationStatus.Success)
            {
                var err = api.ResultGetErrorMessageS(result);
                Debug.WriteLine($"{debugName}: {err}");
                return null;
            }

            var n = api.ResultGetLength(result);
            var ptr = api.ResultGetBytes(result);
            var arr = new byte[(int)n];
            fixed (byte* dst = arr)
                System.Buffer.MemoryCopy(ptr, dst, arr.Length, arr.Length);
            return arr;
        }
        finally
        {
            if (result != null)
                api.ResultRelease(result);
            api.CompileOptionsRelease(options);
            api.CompilerRelease(compiler);
        }
    }

    private static bool TryAllocateHostBuffer(Vk vk, ulong size, out VulkanBuffer buffer, out DeviceMemory memory)
    {
        buffer = default;
        memory = default;
        if (size == 0)
        {
            BufferCreateInfo bciEmpty = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = 1,
                Usage = BufferUsageFlags.StorageBufferBit,
                SharingMode = SharingMode.Exclusive
            };
            if (vk.CreateBuffer(_device, in bciEmpty, null, out buffer) != Result.Success)
                return false;
            vk.GetBufferMemoryRequirements(_device, buffer, out var reqEmpty);
            var memTypeEmpty = FindMemoryType(vk, _physicalDevice, reqEmpty.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            if (memTypeEmpty == uint.MaxValue)
            {
                vk.DestroyBuffer(_device, buffer, null);
                return false;
            }

            MemoryAllocateInfo maiEmpty = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = reqEmpty.Size,
                MemoryTypeIndex = memTypeEmpty
            };
            if (vk.AllocateMemory(_device, in maiEmpty, null, out memory) != Result.Success)
            {
                vk.DestroyBuffer(_device, buffer, null);
                return false;
            }

            if (vk.BindBufferMemory(_device, buffer, memory, 0) != Result.Success)
            {
                vk.FreeMemory(_device, memory, null);
                vk.DestroyBuffer(_device, buffer, null);
                return false;
            }

            return true;
        }

        BufferCreateInfo bci = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.StorageBufferBit,
            SharingMode = SharingMode.Exclusive
        };

        if (vk.CreateBuffer(_device, in bci, null, out buffer) != Result.Success)
            return false;

        vk.GetBufferMemoryRequirements(_device, buffer, out var req);
        var memType = FindMemoryType(vk, _physicalDevice, req.MemoryTypeBits,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        if (memType == uint.MaxValue)
        {
            vk.DestroyBuffer(_device, buffer, null);
            buffer = default;
            return false;
        }

        MemoryAllocateInfo mai = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = memType
        };

        if (vk.AllocateMemory(_device, in mai, null, out memory) != Result.Success)
        {
            vk.DestroyBuffer(_device, buffer, null);
            buffer = default;
            return false;
        }

        if (vk.BindBufferMemory(_device, buffer, memory, 0) != Result.Success)
        {
            vk.FreeMemory(_device, memory, null);
            vk.DestroyBuffer(_device, buffer, null);
            buffer = default;
            memory = default;
            return false;
        }

        return true;
    }

    private static uint FindMemoryType(Vk vk, PhysicalDevice phys, uint typeBits, MemoryPropertyFlags props)
    {
        vk.GetPhysicalDeviceMemoryProperties(phys, out var memProps);
        for (var i = 0u; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1 << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
                return i;
        }

        return uint.MaxValue;
    }

    private static void ReleaseBuffer(Vk vk, VulkanBuffer buf, DeviceMemory mem)
    {
        if (buf.Handle != 0)
            vk.DestroyBuffer(_device, buf, null);
        if (mem.Handle != 0)
            vk.FreeMemory(_device, mem, null);
    }

    private static float[] floatsFromPathLens(double[] pathLens)
    {
        var floats = new float[pathLens.Length];
        for (var i = 0; i < pathLens.Length; i++)
            floats[i] = (float)Math.Max(pathLens[i], 1e-6);
        return floats;
    }

    private static void CopySpan<T>(DeviceMemory mem, ReadOnlySpan<T> data) where T : unmanaged
    {
        var vk = _vk!;
        var byteLen = checked(data.Length * Unsafe.SizeOf<T>());
        void* ptr;
        if (vk.MapMemory(_device, mem, 0, Vk.WholeSize, 0, &ptr) != Result.Success)
            throw new InvalidOperationException("MapMemory");
        try
        {
            MemoryMarshal.AsBytes(data).CopyTo(new Span<byte>((byte*)ptr, byteLen));
        }
        finally
        {
            vk.UnmapMemory(_device, mem);
        }
    }

    private static void CopyStruct<T>(DeviceMemory mem, T value) where T : unmanaged
    {
        var vk = _vk!;
        void* ptr;
        if (vk.MapMemory(_device, mem, 0, Vk.WholeSize, 0, &ptr) != Result.Success)
            throw new InvalidOperationException("MapMemory");
        try
        {
            *(T*)ptr = value;
        }
        finally
        {
            vk.UnmapMemory(_device, mem);
        }
    }

    private static void CopyHeights(DeviceMemory mem, double[,] h, int cols, int rows)
    {
        var n = cols * rows;
        var tmp = new float[n];
        var i = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
                tmp[i++] = (float)h[c, r];
        }

        CopySpan<float>(mem, tmp);
    }

    private static void CopyHalfW(DeviceMemory mem, double[,] hw, int cols, int rows)
    {
        var n = cols * rows;
        var tmp = new float[n];
        var i = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
                tmp[i++] = (float)hw[c, r];
        }

        CopySpan<float>(mem, tmp);
    }

    private static void CopyFlags(DeviceMemory mem, bool[,] isRiver, bool[,] isOcean, int[,] lakeId, int cols,
        int rows)
    {
        var n = cols * rows;
        var tmp = new uint[n];
        var i = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                uint f = 0;
                if (isRiver[c, r])
                    f |= 1;
                if (isOcean[c, r])
                    f |= 2;
                if (lakeId[c, r] != 0)
                    f |= 4;
                tmp[i++] = f;
            }
        }

        CopySpan<uint>(mem, tmp);
    }

    private static unsafe void ReadHeights(DeviceMemory mem, double[,] h, int cols, int rows)
    {
        var vk = _vk!;
        void* ptr;
        if (vk.MapMemory(_device, mem, 0, Vk.WholeSize, 0, &ptr) != Result.Success)
            throw new InvalidOperationException("MapMemory");
        try
        {
            var n = cols * rows;
            var tmp = new float[n];
            fixed (float* dst = tmp)
                System.Buffer.MemoryCopy(ptr, dst, n * sizeof(float), n * sizeof(float));
            var i = 0;
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                    h[c, r] = tmp[i++];
            }
        }
        finally
        {
            vk.UnmapMemory(_device, mem);
        }
    }

    private static unsafe bool RunOnce(
        Vk vk,
        Pipeline pipeline,
        VulkanBuffer bufParams,
        VulkanBuffer bufSeg,
        VulkanBuffer bufTileOff,
        VulkanBuffer bufTileIdx,
        VulkanBuffer bufPathLen,
        VulkanBuffer bufHeights,
        VulkanBuffer bufHalfW,
        VulkanBuffer bufFlags,
        int cols,
        int rows)
    {
        DescriptorPoolSize ps = new()
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = 8
        };

        DescriptorPoolCreateInfo poolCi = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &ps
        };

        if (vk.CreateDescriptorPool(_device, in poolCi, null, out var pool) != Result.Success)
            return false;

        var dsl = _descriptorSetLayout;
        DescriptorSetAllocateInfo allocInfo = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsl
        };

        if (vk.AllocateDescriptorSets(_device, in allocInfo, out var descSet) != Result.Success)
        {
            vk.DestroyDescriptorPool(_device, pool, null);
            return false;
        }

        var buffers = new[]
        {
            bufParams, bufSeg, bufTileOff, bufTileIdx, bufPathLen, bufHeights, bufHalfW, bufFlags
        };

        var writes = stackalloc WriteDescriptorSet[8];
        var infos = stackalloc DescriptorBufferInfo[8];
        for (var i = 0; i < 8; i++)
        {
            infos[i] = new DescriptorBufferInfo
            {
                Buffer = buffers[i],
                Offset = 0,
                Range = Vk.WholeSize
            };
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descSet,
                DstBinding = (uint)i,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &infos[i]
            };
        }

        vk.UpdateDescriptorSets(_device, 8, writes, 0, null);

        CommandBufferAllocateInfo cmdAlloc = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };

        if (vk.AllocateCommandBuffers(_device, in cmdAlloc, out var cmd) != Result.Success)
        {
            vk.DestroyDescriptorPool(_device, pool, null);
            return false;
        }

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        if (vk.BeginCommandBuffer(cmd, in beginInfo) != Result.Success)
        {
            vk.FreeCommandBuffers(_device, _commandPool, 1, ref cmd);
            vk.DestroyDescriptorPool(_device, pool, null);
            return false;
        }

        vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, ref descSet, 0, null);

        var gx = (cols + (int)WorkgroupSize - 1) / (int)WorkgroupSize;
        var gy = (rows + (int)WorkgroupSize - 1) / (int)WorkgroupSize;
        vk.CmdDispatch(cmd, (uint)gx, (uint)gy, 1);

        if (vk.EndCommandBuffer(cmd) != Result.Success)
        {
            vk.FreeCommandBuffers(_device, _commandPool, 1, ref cmd);
            vk.DestroyDescriptorPool(_device, pool, null);
            return false;
        }

        FenceCreateInfo fenceCi = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.None
        };

        if (vk.CreateFence(_device, in fenceCi, null, out var fence) != Result.Success)
        {
            vk.FreeCommandBuffers(_device, _commandPool, 1, ref cmd);
            vk.DestroyDescriptorPool(_device, pool, null);
            return false;
        }

        var cmds = new CommandBuffer[1];
        cmds[0] = cmd;
        var ok = false;
        fixed (CommandBuffer* pCmd = cmds)
        {
            SubmitInfo si = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = pCmd
            };
            ok = vk.QueueSubmit(_queue, 1, in si, fence) == Result.Success &&
                 vk.WaitForFences(_device, 1, ref fence, true, ulong.MaxValue) == Result.Success;
        }

        vk.DestroyFence(_device, fence, null);
        vk.FreeCommandBuffers(_device, _commandPool, 1, ref cmd);
        vk.DestroyDescriptorPool(_device, pool, null);

        return ok;
    }
}
