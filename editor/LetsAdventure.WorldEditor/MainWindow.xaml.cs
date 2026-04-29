using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using LetsAdventure.Core.Json;
using LetsAdventure.Core.Simulation;
using LetsAdventure.Core.World;
using LetsAdventure.WorldEditor.Services;

namespace LetsAdventure.WorldEditor;

public partial class MainWindow : Window
{
    private PhysicalWorldDefinition _world = WorldDocumentService.CreateNew();
    private string? _filePath;
    private bool _dirty;

    private bool _scene3DStale = true;
    private Vec3 _3dFocusWorld;
    private Point _3dLastMouse;
    private MouseButton? _viewportDragButton;
    private bool _3dFocusInitialized;
    private readonly HelixToolkit.Wpf.SharpDX.DefaultEffectsManager _helixEffects = new();
    private readonly HashSet<Key> _3dKeysDown = [];
    private readonly HashSet<Key> _mapPreviewKeysDown = [];
    private readonly ScaleTransform _mapPreviewScaleTransform = new(1, 1);
    private double _mapPreviewZoom = 1;
    private NavGridChunkCellSource? _navChunkSource;
    private string? _navChunkStoreDirectoryAbsolute;
    private bool _mapPreviewDragging;
    private Point _mapPreviewDragLast;
    private readonly DispatcherTimer _3dMoveTimer;

    public MainWindow()
    {
        InitializeComponent();
        WorldViewportDx.EffectsManager = _helixEffects;
        _3dMoveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _3dMoveTimer.Tick += (_, _) =>
        {
            Tick3DWasdPan();
            TickMapPreviewWasdPan();
        };
        _3dMoveTimer.Start();
        Closed += (_, _) =>
        {
            _helixEffects.Dispose();
            _3dMoveTimer.Stop();
            _navChunkSource?.Dispose();
            _navChunkSource = null;
        };
        CmbFeatureKind.ItemsSource = new[]
        {
            "ground_plateau", "path", "mountain_ridge", "water_standing", "water_flowing",
            "building", "vegetation", "solid_volume", "sky_volume",
        };
        CmbFeatureKind.SelectedIndex = 0;
        RefreshUiFromWorld();
        UpdateStatus();
    }

    private void MarkDirty()
    {
        _dirty = true;
        StatusDirty.Text = "Modified";
    }

    private void ClearDirty()
    {
        _dirty = false;
        StatusDirty.Text = "";
    }

    private void UpdateStatus() => StatusPath.Text = _filePath ?? "(unsaved new world)";

    private void RefreshUiFromWorld()
    {
        EnsureNavBundle();
        TxtCoordDesc.Text = _world.CoordinateDescription;
        TxtMinX.Text = _world.GlobalBounds.Min.X.ToString();
        TxtMinY.Text = _world.GlobalBounds.Min.Y.ToString();
        TxtMinZ.Text = _world.GlobalBounds.Min.Z.ToString();
        TxtMaxX.Text = _world.GlobalBounds.Max.X.ToString();
        TxtMaxY.Text = _world.GlobalBounds.Max.Y.ToString();
        TxtMaxZ.Text = _world.GlobalBounds.Max.Z.ToString();

        LstRegions.ItemsSource = _world.RegionBoundaries;
        LstTerritories.ItemsSource = _world.Territories;
        LstFeatures.ItemsSource = _world.Features;

        _ = _world.Navigation.Grid ??= new TerrainNavGridDefinition();
        SyncNavGridChunkAccess();
        DrawPreview();
        _scene3DStale = true;
        _3dFocusInitialized = false;
        if (Equals(MainTabs.SelectedItem, Scene3DTab))
            RebuildWorld3DScene();
    }

    private void EnsureNavBundle()
    {
        _world.Navigation.Graph ??= new NavigationGraphDefinition();
        _world.Navigation.Grid ??= new TerrainNavGridDefinition { CellSize = 4, Columns = 16, Rows = 16 };
    }

    private void SyncNavGridChunkAccess()
    {
        _navChunkSource?.Dispose();
        _navChunkSource = null;
        _world.NavGridCellSource = null;
        _navChunkStoreDirectoryAbsolute = null;

        var grid = _world.Navigation.Grid;
        if (grid is null || grid.Columns < 1 || grid.Rows < 1)
            return;

        if (grid.Cells is { Count: var populated } && populated >= (long)grid.Columns * grid.Rows)
            return;

        string? dir = null;
        if (!string.IsNullOrEmpty(grid.NavGridChunkSessionDirectoryAbsolute))
            dir = System.IO.Path.GetFullPath(grid.NavGridChunkSessionDirectoryAbsolute);
        else if (!string.IsNullOrEmpty(grid.NavGridChunkStoreRelativePath) && !string.IsNullOrEmpty(_filePath))
        {
            var baseDir = System.IO.Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(baseDir))
                dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, grid.NavGridChunkStoreRelativePath));
        }

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;

        _navChunkSource = NavGridChunkCellSource.TryOpen(dir);
        if (_navChunkSource is null)
            return;
        _navChunkStoreDirectoryAbsolute = dir;
        _world.NavGridCellSource = _navChunkSource;
    }

    private string? ResolveNavChunkStoreDirectory()
    {
        var grid = _world.Navigation.Grid;
        if (grid is null)
            return null;
        if (!string.IsNullOrEmpty(_navChunkStoreDirectoryAbsolute) &&
            Directory.Exists(_navChunkStoreDirectoryAbsolute))
            return _navChunkStoreDirectoryAbsolute;
        if (!string.IsNullOrEmpty(grid.NavGridChunkSessionDirectoryAbsolute))
        {
            var d = System.IO.Path.GetFullPath(grid.NavGridChunkSessionDirectoryAbsolute);
            if (Directory.Exists(d))
                return d;
        }

        if (!string.IsNullOrEmpty(grid.NavGridChunkStoreRelativePath) && !string.IsNullOrEmpty(_filePath))
        {
            var baseDir = System.IO.Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(baseDir))
            {
                var d = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, grid.NavGridChunkStoreRelativePath));
                if (Directory.Exists(d))
                    return d;
            }
        }

        return null;
    }

    private void PreloadNavGridChunksFor3DView()
    {
        if (_world.NavGridCellSource is not NavGridChunkCellSource src)
            return;
        var chunkHalf = Sld3DChunkDetailHalf is not null
            ? (int)Math.Clamp(Math.Round(Sld3DChunkDetailHalf.Value), 0, 512)
            : 72;
        var cw = Math.Max(1, src.Manifest.ChunkWidthCells);
        var radius = Math.Clamp(chunkHalf / cw + 3, 2, 18);
        src.PreloadChunksAroundWorldXY(_3dFocusWorld.X, _3dFocusWorld.Y, radius);
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(sourceDir, file);
            var dest = System.IO.Path.Combine(targetDir, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private void ApplyMetadata_Click(object sender, RoutedEventArgs e)
    {
        _world.CoordinateDescription = TxtCoordDesc.Text.Trim();
        if (double.TryParse(TxtMinX.Text, out var minX)
            && double.TryParse(TxtMinY.Text, out var minY)
            && double.TryParse(TxtMinZ.Text, out var minZ)
            && double.TryParse(TxtMaxX.Text, out var maxX)
            && double.TryParse(TxtMaxY.Text, out var maxY)
            && double.TryParse(TxtMaxZ.Text, out var maxZ))
        {
            _world.GlobalBounds.Min = new Vec3 { X = minX, Y = minY, Z = minZ };
            _world.GlobalBounds.Max = new Vec3 { X = maxX, Y = maxY, Z = maxZ };
        }

        MarkDirty();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!WarnDiscardUnsaved())
            return;
        _navChunkSource?.Dispose();
        _navChunkSource = null;
        _navChunkStoreDirectoryAbsolute = null;
        _world = WorldDocumentService.CreateNew();
        _filePath = null;
        RefreshUiFromWorld();
        TxtWaterSurfaceOffset.Text = new BaselineGenerationRequest().WaterSurfaceOffsetM.ToString(CultureInfo.InvariantCulture);
        TxtLandFreshwaterCoveragePct.Text = "100";
        ClearDirty();
        UpdateStatus();
    }

    private async void GenerateWorld_Click(object sender, RoutedEventArgs e)
    {
        if (!WarnDiscardUnsaved())
            return;
        var dlg = new GenerateWorldDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultSpec is null)
            return;

        var spec = dlg.ResultSpec;
        PhysicalWorldGenerationResult result;
        try
        {
            result = await Task.Run(() => ProceduralPhysicalWorldGenerator.Generate(spec)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Procedural world", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _world = result.World;
        _filePath = null;
        RefreshUiFromWorld();
        MarkDirty();
        UpdateStatus();

        var r = result.Report;
        var lines = new List<string>();
        if (r.IsSufficientlyNavigable)
            lines.Add("Generation passed navigability and hydrology checks.");
        else
            lines.Add("Generation completed with warnings — review land slope and water connectivity.");

        lines.AddRange(r.Messages);
        lines.Add(
            $"Max land orth. slope: {r.MaxObservedLandOrthogonalSlope:F2} (limit {spec.MaxLandStepOrthogonal:F2}); violation edges: {r.LandSlopeViolationCount}.");
        lines.Add(
            $"Hydrology strict pass: {r.RiverTerminatesInLakeRegion}; river→ocean connectivity: {r.AllFlowingCellsReachStandingWater} (river cells failing: {r.WaterConnectivityFailures}).");
        lines.Add(
            $"Lake cells without ocean path: {r.LakeBasinDisconnectedFromOceanCells}; downstream profile violations: {r.RiverCenterlineDownstreamGradientViolations}.");

        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            "Procedural world",
            MessageBoxButton.OK,
            r.IsSufficientlyNavigable ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static bool TryParseBaselineRequestFromUi(
        string minXt, string minYt, string minZt, string maxXt, string maxYt, string maxZt, string waterOffsetText,
        string landFreshwaterPctText,
        out BaselineGenerationRequest request, out string error)
    {
        request = default!;
        if (!double.TryParse(minXt, NumberStyles.Float, CultureInfo.InvariantCulture, out var minX)
            || !double.TryParse(minYt, NumberStyles.Float, CultureInfo.InvariantCulture, out var minY)
            || !double.TryParse(minZt, NumberStyles.Float, CultureInfo.InvariantCulture, out var minZ)
            || !double.TryParse(maxXt, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX)
            || !double.TryParse(maxYt, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY)
            || !double.TryParse(maxZt, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxZ))
        {
            error = "Enter valid numbers for all min/max X, Y, and Z bounds.";
            return false;
        }

        if (!double.TryParse(waterOffsetText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var waterOffset)
            || !double.IsFinite(waterOffset))
        {
            error = "Enter a valid finite number for water offset (m).";
            return false;
        }

        if (!double.TryParse(landFreshwaterPctText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rawPct)
            || !double.IsFinite(rawPct)
            || rawPct <= 0)
        {
            error = "Enter a positive number for land freshwater (%).";
            return false;
        }

        var pctClamped = Math.Clamp(rawPct, 2.5, 500.0);
        var strictness = 100.0 / pctClamped;

        request = new BaselineGenerationRequest
        {
            Seed = null,
            MinX = minX,
            MinY = minY,
            MinZ = minZ,
            MaxX = maxX,
            MaxY = maxY,
            MaxZ = maxZ,
            WaterSurfaceOffsetM = waterOffset,
            LandFreshwaterStrictness = strictness,
        };

        try
        {
            request.Validate();
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        error = "";
        return true;
    }

    private async void MetadataGenerateBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (!WarnDiscardUnsaved())
            return;

        if (!TryParseBaselineRequestFromUi(
                TxtMinX.Text, TxtMinY.Text, TxtMinZ.Text, TxtMaxX.Text, TxtMaxY.Text, TxtMaxZ.Text,
                TxtWaterSurfaceOffset.Text,
                TxtLandFreshwaterCoveragePct.Text,
                out var request,
                out var parseErr))
        {
            MessageBox.Show(this, parseErr, "Baseline map", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var logWindow = new BaselineBuildProgressWindow { Owner = this };
        var baselineSucceeded = false;
        logWindow.Closed += (_, _) =>
        {
            if (!baselineSucceeded)
                return;
            // Let the progress window finish tearing down before tab change + 3D work. Background/Normal
            // still run in the same "storm" as Close; ApplicationIdle yields until input/layout settle.
            _ = Dispatcher.BeginInvoke(() => { MainTabs.SelectedItem = Scene3DTab; },
                DispatcherPriority.ApplicationIdle);
        };
        logWindow.Show();
        logWindow.SetBusy(true);
        IProgress<string> progress = new Progress<string>(logWindow.AppendLine);
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => BaselinePhysicalWorldGenerator.Generate(request, progress))
                .ConfigureAwait(true);

            _world = result.World;
            _filePath = null;

            var baselineDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LetsAdventure", "WorldEditor", "baseline-output");
            var baselineJson = System.IO.Path.Combine(baselineDir, "physical_world.json");
            progress.Report("Saving baseline to disk (required before continuing)…");
            try
            {
                if (!SaveWorldToPathCore(baselineJson, progress))
                    throw new InvalidOperationException("Baseline save path is invalid.");
            }
            catch (Exception ex)
            {
                logWindow.AppendLine("");
                logWindow.AppendLine("Failed to save baseline to disk: " + ex.Message);
                logWindow.NotifyComplete();
                return;
            }

            progress.Report($"Baseline saved: {baselineJson}");
            ClearDirty();
            UpdateStatus();

            RefreshUiFromWorld();

            logWindow.AppendLine("");
            logWindow.AppendLine($"Saved to: {baselineJson}");
            logWindow.AppendLine(
                $"Bounds X [{request.MinX:F0} … {request.MaxX:F0}], Y [{request.MinY:F0} … {request.MaxY:F0}], Z [{request.MinZ:F0} … {request.MaxZ:F0}].");
            logWindow.AppendLine(
                $"Regions: {_world.RegionBoundaries.Count}, territories: {_world.Territories.Count}, features: {_world.Features.Count}.");
            foreach (var msg in result.Report.Messages)
                logWindow.AppendLine(msg);
            logWindow.AppendLine(result.Report.IsSufficientlyNavigable
                ? "Hydrology / slope checks passed."
                : "Review hydrology / slope warnings above.");
            logWindow.AppendLine(
                "Closing this window opens the 3D scene tab. Baseline output is under baseline-output above; use Save As to copy elsewhere. WASD pans; LMB orbits; mouse wheel zooms; Shift+wheel changes orbit elevation.");
            baselineSucceeded = true;
            logWindow.NotifyComplete();
        }
        catch (Exception ex)
        {
            logWindow.AppendLine("");
            logWindow.AppendLine("Failed: " + ex.Message);
            logWindow.NotifyComplete();
        }
    }

    private void LoadMap_Click(object sender, RoutedEventArgs e) => Open_Click(sender, e);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!WarnDiscardUnsaved())
            return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Physical world JSON|physical_world.json;*.json|All files|*.*",
        };
        if (dlg.ShowDialog() != true)
            return;
        _world = WorldDocumentService.Load(dlg.FileName);
        _filePath = dlg.FileName;
        RefreshUiFromWorld();
        ClearDirty();
        UpdateStatus();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveInternal(promptPath: false);

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveInternal(promptPath: true);

    /// <summary>
    /// Persists <see cref="_world"/> to <paramref name="path"/> (JSON + <c>.navgrid</c> sidecar when required).
    /// No dialogs; sets <see cref="_filePath"/> on success.
    /// </summary>
    /// <returns>False when the path has no directory component.</returns>
    private bool SaveWorldToPathCore(string path, IProgress<string>? progress)
    {
        EnsureNavBundle();
        var dir = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            return false;
        Directory.CreateDirectory(dir);

        var grid = _world.Navigation.Grid;
        if (grid is not null)
        {
            var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
            var sidecar = System.IO.Path.Combine(dir, baseName + ".navgrid");
            if (grid.Cells is { Count: var nc } && nc >= NavGridChunkIO.AutoExportCellThreshold)
            {
                progress?.Report($"Writing nav grid chunks to {sidecar}…");
                NavGridChunkIO.ExportToDirectory(grid, sidecar, progress: progress);
                WorldNavGridPreviewRenderer.TryWriteOverviewPng(grid, System.IO.Path.Combine(sidecar, "preview.png"));
                grid.NavGridChunkStoreRelativePath = System.IO.Path.GetFileName(sidecar);
                grid.NavGridChunkSessionDirectoryAbsolute = null;
                grid.Cells = null;
                _navChunkSource?.Dispose();
                _navChunkSource = NavGridChunkCellSource.TryOpen(sidecar);
                _navChunkStoreDirectoryAbsolute = sidecar;
                _world.NavGridCellSource = _navChunkSource;
            }
            else if (!string.IsNullOrEmpty(grid.NavGridChunkSessionDirectoryAbsolute))
            {
                progress?.Report($"Copying nav grid chunk session to {sidecar}…");
                Directory.CreateDirectory(sidecar);
                CopyDirectoryRecursive(grid.NavGridChunkSessionDirectoryAbsolute, sidecar);
                grid.NavGridChunkStoreRelativePath = System.IO.Path.GetFileName(sidecar);
                grid.NavGridChunkSessionDirectoryAbsolute = null;
                _navChunkSource?.Dispose();
                _navChunkSource = NavGridChunkCellSource.TryOpen(sidecar);
                _navChunkStoreDirectoryAbsolute = sidecar;
                _world.NavGridCellSource = _navChunkSource;
            }
        }

        progress?.Report($"Writing {path}…");
        WorldDocumentService.Save(path, _world);
        _world.NavGridCellSource = _navChunkSource;
        _filePath = path;
        return true;
    }

    private void SaveInternal(bool promptPath)
    {
        var path = _filePath;
        if (promptPath || string.IsNullOrEmpty(path))
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "physical_world.json|physical_world.json|JSON|*.json",
                FileName = "physical_world.json",
            };
            if (dlg.ShowDialog() != true)
                return;
            path = dlg.FileName;
        }

        if (!SaveWorldToPathCore(path, progress: null))
            return;
        ClearDirty();
        UpdateStatus();
        MessageBox.Show(this, "Saved.", "World Editor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportFeatures_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "physical_world.features.json|physical_world.features.json|JSON|*.json",
            FileName = "physical_world.features.json",
        };
        if (dlg.ShowDialog() != true)
            return;
        WorldDocumentService.SaveFeaturesOverlay(dlg.FileName, _world.Features);
        MessageBox.Show(this, "Features overlay saved. Engine merges this after physical_world.json.", "World Editor",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>True if user allows discarding unsaved work (or nothing dirty).</summary>
    private bool WarnDiscardUnsaved()
    {
        if (!_dirty)
            return true;
        var r = MessageBox.Show(this, "Discard unsaved changes?", "World Editor", MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return r == MessageBoxResult.Yes;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty)
        {
            var r = MessageBox.Show(this, "Close without saving?", "World Editor", MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (r == MessageBoxResult.No)
                e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox
            && Keyboard.FocusedElement is not RichTextBox
            && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (Equals(MainTabs.SelectedItem, MapPreviewTab))
            {
                if (e.Key is Key.W or Key.A or Key.S or Key.D)
                {
                    _mapPreviewKeysDown.Add(e.Key);
                    e.Handled = true;
                    return;
                }
            }

            if (Equals(MainTabs.SelectedItem, Scene3DTab))
            {
                if (e.Key is Key.W or Key.A or Key.S or Key.D)
                {
                    _3dKeysDown.Add(e.Key);
                    e.Handled = true;
                    return;
                }
            }
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        if (e.Key == Key.S)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.N)
        {
            New_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.O)
        {
            Open_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.W or Key.A or Key.S or Key.D)
        {
            _3dKeysDown.Remove(e.Key);
            _mapPreviewKeysDown.Remove(e.Key);
        }
    }

    private void Tick3DWasdPan()
    {
        if (!IsLoaded || !Equals(MainTabs.SelectedItem, Scene3DTab) || World3DCamera is null)
            return;
        if (_3dKeysDown.Count == 0)
            return;

        var ld = World3DCamera.LookDirection;
        var forward = new Vector3D(ld.X, 0, ld.Z);
        if (forward.LengthSquared < 1e-12)
            forward = new Vector3D(0, 0, -1);
        else
            forward.Normalize();

        var right = Vector3D.CrossProduct(forward, new Vector3D(0, 1, 0));
        if (right.LengthSquared < 1e-12)
            right = new Vector3D(1, 0, 0);
        else
            right.Normalize();

        var speed = Math.Max(2.5, Sld3DDistance.Value * 0.0045);
        var delta = new Vector3D(0, 0, 0);
        if (_3dKeysDown.Contains(Key.W))
            delta += forward * speed;
        if (_3dKeysDown.Contains(Key.S))
            delta -= forward * speed;
        if (_3dKeysDown.Contains(Key.D))
            delta += right * speed;
        if (_3dKeysDown.Contains(Key.A))
            delta -= right * speed;

        if (delta.LengthSquared < 1e-12)
            return;

        Pan3DFocusInViewSpace(delta);
        PreloadNavGridChunksFor3DView();
        Apply3DCameraOnly();
        WorldViewportDx.InvalidateRender();
    }

    /// <summary>Moves orbit focus in view plane (WPF XZ horizontal, Y up) and re-samples Z from terrain.</summary>
    private void Pan3DFocusInViewSpace(Vector3D deltaView)
    {
        double nx, ny;
        if (WorldSceneHelixBuilder.TryGetViewMapping(_world, out _, out var xyScale, out _))
        {
            nx = _3dFocusWorld.X + deltaView.X / xyScale;
            ny = _3dFocusWorld.Y - deltaView.Z / xyScale;
        }
        else
        {
            nx = _3dFocusWorld.X + deltaView.X;
            ny = _3dFocusWorld.Y - deltaView.Z;
        }

        var gz = WorldScene3DBuilder.SampleSurfaceElevation(_world, nx, ny) + FocusClearanceAboveGround();
        _3dFocusWorld = new Vec3 { X = nx, Y = ny, Z = gz };
        _3dFocusInitialized = true;
        Txt3DWorldX.Text = _3dFocusWorld.X.ToString("F1");
        Txt3DWorldY.Text = _3dFocusWorld.Y.ToString("F1");
        Txt3DWorldZ.Text = _3dFocusWorld.Z.ToString("F1");
    }

    private void RegionAdd_Click(object sender, RoutedEventArgs e)
    {
        _world.RegionBoundaries.Add(new RegionBoundaryEntry
        {
            LoreRegionId = "region.new_region",
            Boundary = new PolygonColumnBounds { ZMin = -20, ZMax = 200 },
        });
        LstRegions.Items.Refresh();
        MarkDirty();
    }

    private void RegionRemove_Click(object sender, RoutedEventArgs e)
    {
        if (LstRegions.SelectedItem is RegionBoundaryEntry r)
        {
            _world.RegionBoundaries.Remove(r);
            LstRegions.Items.Refresh();
            RegionEditor.IsEnabled = false;
            MarkDirty();
        }
    }

    private void LstRegions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstRegions.SelectedItem is not RegionBoundaryEntry r)
        {
            RegionEditor.IsEnabled = false;
            return;
        }

        RegionEditor.IsEnabled = true;
        TxtRegionLoreId.Text = r.LoreRegionId;
        TxtRegionZMin.Text = r.Boundary.ZMin.ToString();
        TxtRegionZMax.Text = r.Boundary.ZMax.ToString();
        TxtRegionVertices.Text = string.Join(Environment.NewLine,
            r.Boundary.Vertices.Select(v => $"{v.X}, {v.Y}"));
    }

    private void RegionApply_Click(object sender, RoutedEventArgs e)
    {
        if (LstRegions.SelectedItem is not RegionBoundaryEntry r)
            return;
        r.LoreRegionId = TxtRegionLoreId.Text.Trim();
        if (double.TryParse(TxtRegionZMin.Text, out var z0))
            r.Boundary.ZMin = z0;
        if (double.TryParse(TxtRegionZMax.Text, out var z1))
            r.Boundary.ZMax = z1;
        r.Boundary.Vertices = ParseVertices(TxtRegionVertices.Text);
        LstRegions.Items.Refresh();
        MarkDirty();
        DrawPreview();
    }

    private void TerritoryAdd_Click(object sender, RoutedEventArgs e)
    {
        _world.Territories.Add(new TerritoryClaim
        {
            Id = "terr.new",
            ClaimantFactionId = "faction.example",
            Priority = 1,
            Boundary = new PolygonColumnBounds { ZMin = -10, ZMax = 100 },
        });
        LstTerritories.Items.Refresh();
        MarkDirty();
    }

    private void TerritoryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (LstTerritories.SelectedItem is TerritoryClaim t)
        {
            _world.Territories.Remove(t);
            LstTerritories.Items.Refresh();
            TerritoryEditor.IsEnabled = false;
            MarkDirty();
        }
    }

    private void LstTerritories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstTerritories.SelectedItem is not TerritoryClaim t)
        {
            TerritoryEditor.IsEnabled = false;
            return;
        }

        TerritoryEditor.IsEnabled = true;
        TxtTerritoryId.Text = t.Id;
        TxtTerritoryFaction.Text = t.ClaimantFactionId;
        TxtTerritoryLoreRegion.Text = t.LoreRegionId ?? "";
        TxtTerritoryPriority.Text = t.Priority.ToString();
        TxtTerritoryZMin.Text = t.Boundary.ZMin.ToString();
        TxtTerritoryZMax.Text = t.Boundary.ZMax.ToString();
        TxtTerritoryVertices.Text = string.Join(Environment.NewLine,
            t.Boundary.Vertices.Select(v => $"{v.X}, {v.Y}"));
    }

    private void TerritoryApply_Click(object sender, RoutedEventArgs e)
    {
        if (LstTerritories.SelectedItem is not TerritoryClaim t)
            return;
        t.Id = TxtTerritoryId.Text.Trim();
        t.ClaimantFactionId = TxtTerritoryFaction.Text.Trim();
        var lr = TxtTerritoryLoreRegion.Text.Trim();
        t.LoreRegionId = string.IsNullOrEmpty(lr) ? null : lr;
        if (int.TryParse(TxtTerritoryPriority.Text, out var pr))
            t.Priority = pr;
        if (double.TryParse(TxtTerritoryZMin.Text, out var z0))
            t.Boundary.ZMin = z0;
        if (double.TryParse(TxtTerritoryZMax.Text, out var z1))
            t.Boundary.ZMax = z1;
        t.Boundary.Vertices = ParseVertices(TxtTerritoryVertices.Text);
        LstTerritories.Items.Refresh();
        MarkDirty();
        DrawPreview();
    }

    private static List<GeoVec2> ParseVertices(string text)
    {
        var list = new List<GeoVec2>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            if (double.TryParse(parts[0], out var x) && double.TryParse(parts[1], out var y))
                list.Add(new GeoVec2 { X = x, Y = y });
        }

        return list;
    }

    private void FeatureAdd_Click(object sender, RoutedEventArgs e)
    {
        var kind = CmbFeatureKind.SelectedItem as string ?? "ground_plateau";
        var f = CreateFeature(kind);
        _world.Features.Add(f);
        LstFeatures.Items.Refresh();
        LstFeatures.SelectedItem = f;
        MarkDirty();
        DrawPreview();
    }

    private static PhysicalTerrainFeature CreateFeature(string kind) => kind switch
    {
        "solid_volume" => new SolidVolumeFeature
        {
            Id = "feat.solid_new",
            LayerPriority = 10,
            Bounds = new AxisAlignedBounds
            {
                Min = new Vec3(),
                Max = new Vec3 { X = 10, Y = 10, Z = 10 },
            },
            Composition = SurfaceComposition.Rock,
            BlocksNavigation = true,
        },
        "mountain_ridge" => new MountainRidgeFeature
        {
            Id = "feat.mountain_new",
            LayerPriority = 8,
            CorridorHalfWidth = 30,
            BaseElevationZ = 0,
            PeakElevationZ = 80,
            SurfaceComposition = SurfaceComposition.Rock,
        },
        "water_standing" => new StandingWaterFeature
        {
            Id = "feat.water_new",
            LayerPriority = 3,
            WaterSurfaceZ = 0,
            Depth = 2,
        },
        "water_flowing" => new FlowingWaterFeature
        {
            Id = "feat.river_new",
            LayerPriority = 3,
            ChannelHalfWidth = 6,
            WaterSurfaceZ = 0,
        },
        "path" => new PathCorridorFeature
        {
            Id = "feat.path_new",
            LayerPriority = 5,
            HalfWidth = 3,
            Surface = SurfaceComposition.Pavement,
            MovementCostMultiplier = 0.9,
        },
        "building" => new BuildingFootprintFeature
        {
            Id = "feat.building_new",
            LayerPriority = 9,
            BaseZ = 0,
            RoofZ = 10,
            BlocksNavigation = true,
        },
        "vegetation" => new VegetationVolumeFeature
        {
            Id = "feat.veg_new",
            LayerPriority = 4,
            ZMin = 0,
            ZMax = 15,
            Density01 = 0.5,
            MovementCostMultiplier = 1.2,
        },
        "sky_volume" => new SkyVolumeFeature
        {
            Id = "feat.sky_new",
            LayerPriority = -50,
            Bounds = new AxisAlignedBounds
            {
                Min = new Vec3 { Z = 50 },
                Max = new Vec3 { X = 256, Y = 256, Z = 300 },
            },
        },
        _ => new GroundPlateauFeature
        {
            Id = "feat.ground_new",
            LayerPriority = 0,
            ElevationZ = 0,
            Composition = SurfaceComposition.Soil,
        },
    };

    private void FeatureRemove_Click(object sender, RoutedEventArgs e)
    {
        if (LstFeatures.SelectedItem is PhysicalTerrainFeature f)
        {
            _world.Features.Remove(f);
            LstFeatures.Items.Refresh();
            TxtFeatureJson.Clear();
            MarkDirty();
            DrawPreview();
        }
    }

    private void LstFeatures_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstFeatures.SelectedItem is not PhysicalTerrainFeature f)
        {
            TxtFeatureJson.Clear();
            return;
        }

        var json = JsonSerializer.Serialize((object)f, f.GetType(), GameJson.Options);
        TxtFeatureJson.Text = json;
    }

    private void FeatureApplyJson_Click(object sender, RoutedEventArgs e)
    {
        if (LstFeatures.SelectedItem is not PhysicalTerrainFeature old)
            return;
        try
        {
            var parsed = JsonSerializer.Deserialize<PhysicalTerrainFeature>(TxtFeatureJson.Text, GameJson.Options);
            if (parsed is null)
                throw new InvalidOperationException("Deserialize returned null.");
            var idx = _world.Features.IndexOf(old);
            if (idx < 0)
                return;
            _world.Features[idx] = parsed;
            LstFeatures.Items.Refresh();
            LstFeatures.SelectedItem = parsed;
            MarkDirty();
            DrawPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "JSON error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NavGridLoadAllFromChunks_Click(object sender, RoutedEventArgs e)
    {
        EnsureNavBundle();
        var grid = _world.Navigation.Grid!;
        var dir = ResolveNavChunkStoreDirectory();
        if (string.IsNullOrEmpty(dir))
        {
            MessageBox.Show(this,
                "No nav grid chunk store found. Save the world (large grids write a .navgrid folder), or generate a baseline so chunks exist beside your session.",
                "Nav grid", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (!NavGridChunkIO.TryLoadManifest(dir, out var m) || m is null)
            {
                MessageBox.Show(this, "manifest.json is missing or invalid in the chunk directory.", "Nav grid",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (grid.Columns != m.Columns || grid.Rows != m.Rows
                || Math.Abs(grid.OriginX - m.OriginX) > 1e-6
                || Math.Abs(grid.OriginY - m.OriginY) > 1e-6
                || Math.Abs(grid.CellSize - m.CellSize) > 1e-6)
            {
                var r = MessageBox.Show(this,
                    "Chunk manifest metadata does not match the nav grid fields in this document. Replace grid metadata with manifest values and load all cells?",
                    "Nav grid", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                    return;
                grid.Columns = m.Columns;
                grid.Rows = m.Rows;
                grid.OriginX = m.OriginX;
                grid.OriginY = m.OriginY;
                grid.CellSize = m.CellSize;
            }

            grid.Cells = NavGridChunkIO.MaterializeCellsFromDirectory(dir);
            grid.NavGridChunkSessionDirectoryAbsolute = null;
            _navChunkSource?.Dispose();
            _navChunkSource = null;
            _navChunkStoreDirectoryAbsolute = null;
            _world.NavGridCellSource = null;
            SyncNavGridChunkAccess();
            _scene3DStale = true;
            DrawPreview();
            if (Equals(MainTabs.SelectedItem, Scene3DTab))
                RebuildWorld3DScene();
            MarkDirty();
            MessageBox.Show(this,
                $"Loaded {grid.Cells.Count} nav cells into memory. Save still writes the full grid to the chunk store when above the export threshold.",
                "Nav grid", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Nav grid", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NavGridReleaseCellsToChunkStore_Click(object sender, RoutedEventArgs e)
    {
        EnsureNavBundle();
        var grid = _world.Navigation.Grid!;
        var dir = ResolveNavChunkStoreDirectory();
        if (string.IsNullOrEmpty(dir))
        {
            MessageBox.Show(this,
                "No chunk directory on disk. Save the world first to create a .navgrid folder next to the JSON.",
                "Nav grid", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var expected = (long)grid.Columns * grid.Rows;
        if (grid.Cells is { Count: var n } && n >= expected && n > 0)
        {
            try
            {
                NavGridChunkIO.ExportToDirectory(grid, dir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not write cells to the chunk store: " + ex.Message, "Nav grid",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        grid.Cells = null;
        SyncNavGridChunkAccess();
        _scene3DStale = true;
        DrawPreview();
        if (Equals(MainTabs.SelectedItem, Scene3DTab))
            RebuildWorld3DScene();
        MarkDirty();
        MessageBox.Show(this, "Nav grid is streaming from chunk files again (manifest + binaries on disk).", "Nav grid",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Equals(MainTabs.SelectedItem, Scene3DTab))
        {
            Ensure3DFocusInitialized();
            PreloadNavGridChunksFor3DView();
            if (_scene3DStale)
            {
                // Full Helix rebuild can take many seconds; running it synchronously from tab change
                // (e.g. right after closing the baseline progress window) wedges the UI thread / D3D.
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!Equals(MainTabs.SelectedItem, Scene3DTab))
                        return;
                    RebuildWorld3DScene();
                }, DispatcherPriority.ApplicationIdle);
            }
            else
                Apply3DCameraOnly();
        }

        if (Equals(MainTabs.SelectedItem, MapPreviewTab))
        {
            _3dKeysDown.Clear();
            MapPreviewScrollViewer?.Focus();
            Dispatcher.BeginInvoke(FitMapPreviewToScroll, DispatcherPriority.Loaded);
        }
        else
        {
            _mapPreviewKeysDown.Clear();
            MapPreviewEndDrag(MapPreviewScrollViewer);
        }
    }

    private void MapPreviewScrollViewer_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Equals(MainTabs.SelectedItem, MapPreviewTab) || sender is not ScrollViewer sv)
            return;
        sv.Focus();
        _mapPreviewDragging = true;
        _mapPreviewDragLast = e.GetPosition(sv);
        sv.CaptureMouse();
        e.Handled = true;
    }

    private void MapPreviewScrollViewer_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mapPreviewDragging || sender is not ScrollViewer sv)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            MapPreviewEndDrag(sv);
            return;
        }

        var p = e.GetPosition(sv);
        var dx = p.X - _mapPreviewDragLast.X;
        var dy = p.Y - _mapPreviewDragLast.Y;
        _mapPreviewDragLast = p;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - dx);
        sv.ScrollToVerticalOffset(sv.VerticalOffset - dy);
    }

    private void MapPreviewScrollViewer_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        MapPreviewEndDrag(sender as ScrollViewer);

    private void MapPreviewScrollViewer_OnLostMouseCapture(object sender, MouseEventArgs e) =>
        MapPreviewEndDrag(sender as ScrollViewer);

    private void MapPreviewEndDrag(ScrollViewer? sv)
    {
        if (!_mapPreviewDragging)
            return;
        _mapPreviewDragging = false;
        sv?.ReleaseMouseCapture();
    }

    private void MapPreviewScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Equals(MainTabs.SelectedItem, MapPreviewTab) || PreviewCanvas is null)
            return;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (SldRasterCellPx is null)
                return;
            var next = SldRasterCellPx.Value + (e.Delta > 0 ? 1 : -1);
            SldRasterCellPx.Value = Math.Clamp(next, SldRasterCellPx.Minimum, SldRasterCellPx.Maximum);
            e.Handled = true;
            return;
        }

        var sv = MapPreviewScrollViewer;
        if (sv is null)
            return;

        var p = e.GetPosition(sv);
        var zoom0 = _mapPreviewZoom;
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        var zoom1 = Math.Clamp(zoom0 * factor, 1e-6, 64);
        if (Math.Abs(zoom1 - zoom0) < 1e-15)
        {
            e.Handled = true;
            return;
        }

        var worldX = sv.HorizontalOffset + p.X;
        var worldY = sv.VerticalOffset + p.Y;
        var ratio = zoom1 / zoom0;

        _mapPreviewZoom = zoom1;
        PreviewCanvas.LayoutTransform = _mapPreviewScaleTransform;
        _mapPreviewScaleTransform.ScaleX = _mapPreviewZoom;
        _mapPreviewScaleTransform.ScaleY = _mapPreviewZoom;

        sv.UpdateLayout();
        PreviewCanvas.UpdateLayout();

        var nx = worldX * ratio - p.X;
        var ny = worldY * ratio - p.Y;
        var maxX = Math.Max(0, sv.ScrollableWidth);
        var maxY = Math.Max(0, sv.ScrollableHeight);
        sv.ScrollToHorizontalOffset(Math.Clamp(nx, 0, maxX));
        sv.ScrollToVerticalOffset(Math.Clamp(ny, 0, maxY));
        e.Handled = true;
    }

    private void TickMapPreviewWasdPan()
    {
        if (!IsLoaded || MapPreviewScrollViewer is null || !Equals(MainTabs.SelectedItem, MapPreviewTab))
            return;
        if (_mapPreviewKeysDown.Count == 0)
            return;

        var step = 32 * Math.Max(0.5, _mapPreviewZoom);
        if (_mapPreviewKeysDown.Contains(Key.W))
            MapPreviewScrollViewer.ScrollToVerticalOffset(MapPreviewScrollViewer.VerticalOffset - step);
        if (_mapPreviewKeysDown.Contains(Key.S))
            MapPreviewScrollViewer.ScrollToVerticalOffset(MapPreviewScrollViewer.VerticalOffset + step);
        if (_mapPreviewKeysDown.Contains(Key.A))
            MapPreviewScrollViewer.ScrollToHorizontalOffset(MapPreviewScrollViewer.HorizontalOffset - step);
        if (_mapPreviewKeysDown.Contains(Key.D))
            MapPreviewScrollViewer.ScrollToHorizontalOffset(MapPreviewScrollViewer.HorizontalOffset + step);
    }

    private void RefreshPreview_Click(object sender, RoutedEventArgs e) => DrawPreview();

    private void SldPreviewScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Slider fires during XAML load before later controls (e.g. PreviewCanvas) are constructed.
        if (PreviewCanvas is null)
            return;
        DrawPreview();
    }

    private void PreviewOption_Changed(object sender, RoutedEventArgs e)
    {
        if (PreviewCanvas is null)
            return;
        DrawPreview();
    }

    private void SldRasterCellPx_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PreviewCanvas is null)
            return;
        DrawPreview();
    }

    private void DrawPreview()
    {
        if (PreviewCanvas is null)
            return;
        PreviewCanvas.Children.Clear();
        PreviewCanvas.LayoutTransform = _mapPreviewScaleTransform;
        _mapPreviewScaleTransform.ScaleX = _mapPreviewZoom;
        _mapPreviewScaleTransform.ScaleY = _mapPreviewZoom;

        const double marginOx = 40;
        const double marginOy = 40;
        var cellPx = SldRasterCellPx?.Value ?? 1;
        var gridForMap = _world.Navigation.Grid;
        double ppwX = 0, ppwY = 0;
        var haveGridMap = gridForMap != null
                          && WorldNavGridPreviewRenderer.TryGetMapPreviewPixelsPerWorldMeter(_world, cellPx,
                              _navChunkStoreDirectoryAbsolute, out ppwX, out ppwY);
        var gridOx = haveGridMap ? gridForMap!.OriginX : 0.0;
        var gridOy = haveGridMap ? gridForMap!.OriginY : 0.0;
        var scaleFallback = SldPreviewScale.Value;

        Point ProjectWorld(GeoVec2 p) => haveGridMap
            ? new Point(marginOx + (p.X - gridOx) * ppwX, marginOy + (p.Y - gridOy) * ppwY)
            : new Point(marginOx + p.X * scaleFallback, marginOy + p.Y * scaleFallback);

        Image? raster = null;

        if (ChkPreviewRaster is { IsChecked: true } && SldRasterCellPx is not null)
        {
            var hill = ChkPreviewHillshade is { IsChecked: true };
            raster = WorldNavGridPreviewRenderer.TryBuildNavGridImage(_world, SldRasterCellPx.Value, hill, marginOx,
                marginOy,
                _navChunkStoreDirectoryAbsolute);
            if (raster is not null)
            {
                Canvas.SetZIndex(raster, 0);
                PreviewCanvas.Children.Add(raster);
            }
        }

        var regionStroke = new SolidColorBrush(Color.FromRgb(255, 232, 90));
        var terrStroke = new SolidColorBrush(Color.FromRgb(255, 64, 160));
        const double regionStrokePx = 3.25;
        const double terrStrokePx = 3.0;

        if (ChkPreviewLoreRegions is { IsChecked: true })
        {
            foreach (var reg in _world.RegionBoundaries)
            {
                var el = PolyFromVerticesMapped(reg.Boundary.Vertices, ProjectWorld, regionStroke, regionStrokePx);
                if (el is not null)
                {
                    Canvas.SetZIndex(el, 10);
                    PreviewCanvas.Children.Add(el);
                }
            }
        }

        if (ChkPreviewTerritoryBorders is { IsChecked: true })
        {
            foreach (var terr in _world.Territories)
            {
                var el = PolyFromVerticesMapped(terr.Boundary.Vertices, ProjectWorld, terrStroke, terrStrokePx);
                if (el is not null)
                {
                    Canvas.SetZIndex(el, 11);
                    PreviewCanvas.Children.Add(el);
                }
            }
        }

        if (ChkPreviewFeatures is { IsChecked: true })
        {
            foreach (var f in _world.Features)
            {
                switch (f)
                {
                    case PathCorridorFeature p:
                        var line = PolylineFromPathMapped(p.Centerline, ProjectWorld, Brushes.ForestGreen, 2);
                        if (line is not null)
                        {
                            Canvas.SetZIndex(line, 5);
                            PreviewCanvas.Children.Add(line);
                        }

                        break;
                    case GroundPlateauFeature g:
                        var gp = PolyFromVerticesMapped(g.Boundary, ProjectWorld, Brushes.LightGreen, 0.6);
                        if (gp is not null)
                        {
                            Canvas.SetZIndex(gp, 4);
                            PreviewCanvas.Children.Add(gp);
                        }

                        break;
                    case StandingWaterFeature w:
                        var wp = PolyFromVerticesMapped(w.Shoreline, ProjectWorld, Brushes.DeepSkyBlue, 0.8);
                        if (wp is not null)
                        {
                            Canvas.SetZIndex(wp, 3);
                            PreviewCanvas.Children.Add(wp);
                        }

                        break;
                    case FlowingWaterFeature fw:
                        var riverLine =
                            PolylineFromPathMapped(fw.ChannelCenterline, ProjectWorld, Brushes.CornflowerBlue, 2.5);
                        if (riverLine is not null)
                        {
                            Canvas.SetZIndex(riverLine, 6);
                            PreviewCanvas.Children.Add(riverLine);
                        }

                        break;
                }
            }
        }

        // Size from raster (cell space); vector overlays use world XY and can be huge — do not expand canvas from them.
        if (raster is not null)
        {
            PreviewCanvas.Width = Math.Max(480, marginOx + raster.Width + 40);
            PreviewCanvas.Height = Math.Max(480, marginOy + raster.Height + 40);
        }
        else
        {
            PreviewCanvas.Width = 2000;
            PreviewCanvas.Height = 2000;
        }
    }

    private void FitMapPreviewToScroll()
    {
        if (MapPreviewScrollViewer is null || PreviewCanvas is null || !Equals(MainTabs.SelectedItem, MapPreviewTab))
            return;
        MapPreviewScrollViewer.UpdateLayout();
        PreviewCanvas.UpdateLayout();
        var vw = MapPreviewScrollViewer.ViewportWidth;
        var vh = MapPreviewScrollViewer.ViewportHeight;
        if (vw < 2 || vh < 2)
            return;
        var extW = MapPreviewScrollViewer.ExtentWidth;
        var extH = MapPreviewScrollViewer.ExtentHeight;
        if (extW < 2 || extH < 2)
            return;
        var fit = 0.92 * Math.Min(vw / extW, vh / extH);
        if (fit < _mapPreviewZoom && fit > 1e-8)
        {
            _mapPreviewZoom = Math.Clamp(fit, 1e-6, 64);
            PreviewCanvas.LayoutTransform = _mapPreviewScaleTransform;
            _mapPreviewScaleTransform.ScaleX = _mapPreviewZoom;
            _mapPreviewScaleTransform.ScaleY = _mapPreviewZoom;
            MapPreviewScrollViewer.ScrollToHorizontalOffset(0);
            MapPreviewScrollViewer.ScrollToVerticalOffset(0);
        }
    }

    private void MapPreviewFit_Click(object sender, RoutedEventArgs e)
    {
        if (MapPreviewScrollViewer is null || PreviewCanvas is null)
            return;
        DrawPreview();
        Dispatcher.BeginInvoke(() =>
        {
            if (MapPreviewScrollViewer is null || PreviewCanvas is null)
                return;
            MapPreviewScrollViewer.UpdateLayout();
            PreviewCanvas.UpdateLayout();
            var vw = MapPreviewScrollViewer.ViewportWidth;
            var vh = MapPreviewScrollViewer.ViewportHeight;
            if (vw < 2 || vh < 2)
                return;
            var extW = MapPreviewScrollViewer.ExtentWidth;
            var extH = MapPreviewScrollViewer.ExtentHeight;
            if (extW < 2 || extH < 2)
                return;
            var fit = 0.92 * Math.Min(vw / extW, vh / extH);
            _mapPreviewZoom = Math.Clamp(fit, 1e-6, 64);
            PreviewCanvas.LayoutTransform = _mapPreviewScaleTransform;
            _mapPreviewScaleTransform.ScaleX = _mapPreviewZoom;
            _mapPreviewScaleTransform.ScaleY = _mapPreviewZoom;
            MapPreviewScrollViewer.ScrollToHorizontalOffset(0);
            MapPreviewScrollViewer.ScrollToVerticalOffset(0);
        }, DispatcherPriority.Loaded);
    }

    private static UIElement? PolyFromVerticesMapped(IReadOnlyList<GeoVec2> v, Func<GeoVec2, Point> toCanvas,
        Brush stroke, double thickness)
    {
        if (v.Count < 2)
            return null;
        if (v.Count == 2)
            return PolylineFromPathMapped(v, toCanvas, stroke, thickness);
        var poly = new Polygon
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            Fill = Brushes.Transparent,
            SnapsToDevicePixels = true,
        };
        foreach (var p in v)
            poly.Points.Add(toCanvas(p));
        return poly;
    }

    private static Polyline? PolylineFromPathMapped(IReadOnlyList<GeoVec2> v, Func<GeoVec2, Point> toCanvas,
        Brush stroke, double thickness)
    {
        if (v.Count < 2)
            return null;
        var line = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            SnapsToDevicePixels = true,
        };
        foreach (var p in v)
            line.Points.Add(toCanvas(p));
        return line;
    }

    private static UIElement? PolyFromVertices(IReadOnlyList<GeoVec2> v, double scale, double ox, double oy,
        Brush stroke, double thickness)
    {
        if (v.Count < 2)
            return null;
        if (v.Count == 2)
            return PolylineFromPath(v, scale, ox, oy, stroke, thickness);
        var poly = new Polygon
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            Fill = Brushes.Transparent,
        };
        foreach (var p in v)
            poly.Points.Add(new Point(ox + p.X * scale, oy + p.Y * scale));
        return poly;
    }

    private static Polyline? PolylineFromPath(IReadOnlyList<GeoVec2> v, double scale, double ox, double oy,
        Brush stroke, double thickness)
    {
        if (v.Count < 2)
            return null;
        var line = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
        };
        foreach (var p in v)
            line.Points.Add(new Point(ox + p.X * scale, oy + p.Y * scale));
        return line;
    }

    /// <summary>Height of the 3D orbit focus point above sampled ground at that XY (world units).</summary>
    private const double FocusHeightAboveGround = 100;

    private static double FocusClearanceAboveGround() => FocusHeightAboveGround;

    private void Ensure3DFocusInitialized()
    {
        if (_3dFocusInitialized)
            return;
        double cx, cy;
        if (WorldSceneHelixBuilder.TryGetSceneOriginGround(_world, out var navCenter))
        {
            cx = navCenter.X;
            cy = navCenter.Y;
        }
        else
        {
            var b = _world.GlobalBounds;
            cx = (b.Min.X + b.Max.X) * 0.5;
            cy = (b.Min.Y + b.Max.Y) * 0.5;
        }

        var ground = WorldScene3DBuilder.SampleSurfaceElevation(_world, cx, cy);
        var clearance = FocusClearanceAboveGround();
        _3dFocusWorld = new Vec3 { X = cx, Y = cy, Z = ground + clearance };
        Txt3DWorldX.Text = cx.ToString("F1");
        Txt3DWorldY.Text = cy.ToString("F1");
        Txt3DWorldZ.Text = _3dFocusWorld.Z.ToString("F1");
        _3dFocusInitialized = true;
    }

    private void RebuildWorld3DScene()
    {
        EnsureNavBundle();
        Ensure3DFocusInitialized();
        PreloadNavGridChunksFor3DView();
        var chunkHalf = Sld3DChunkDetailHalf is not null
            ? (int)Math.Clamp(Math.Round(Sld3DChunkDetailHalf.Value), 0, 512)
            : 72;
        var opt = new WorldScene3DOptions
        {
            Terrain = Chk3DTerrain.IsChecked == true,
            Regions = Chk3DRegions.IsChecked == true,
            Territories = Chk3DTerritories.IsChecked == true,
            Features = Chk3DFeatures.IsChecked == true,
            NavGraph = Chk3DNavGraph.IsChecked == true,
            TerrainSubdivisionsPerCell = (int)Math.Clamp(Math.Round(Sld3DTerrainSub.Value), 1, 8),
            TerrainPerspectiveAnchorWorld = _3dFocusWorld,
            TerrainDetailAnchorWorld = _3dFocusWorld,
            TerrainDetailCellHalfExtent = chunkHalf,
            TerrainAerialPerspective = Chk3DTerrainHaze.IsChecked == true,
        };
        var built = WorldSceneHelixBuilder.Build(_world, opt);
        WorldHelixSceneRoot.Children.Clear();
        foreach (var c in built.Children.ToArray())
        {
            built.Children.Remove(c);
            WorldHelixSceneRoot.Children.Add(c);
        }

        _scene3DStale = false;
        Apply3DCameraOnly();
        WorldViewportDx.InvalidateRender();
    }

    private void Apply3DCameraOnly()
    {
        if (World3DCamera is null)
            return;
        Point3D focus;
        if (WorldSceneHelixBuilder.TryGetViewMapping(_world, out var origin, out var xyScale, out var hScale))
            focus = WorldSceneHelixBuilder.ScaledFocusFromWorld(origin, _3dFocusWorld, xyScale, hScale);
        else
            focus = WorldScene3DBuilder.WorldToWpf(_3dFocusWorld);
        var yaw = Sld3DYaw.Value * (Math.PI / 180);
        var pitch = Sld3DPitch.Value * (Math.PI / 180);
        var d = Sld3DDistance.Value;
        var cp = Math.Cos(pitch);
        var ox = cp * Math.Sin(yaw) * d;
        var oy = Math.Sin(pitch) * d;
        var oz = cp * Math.Cos(yaw) * d;
        var camPos = focus + new Vector3D(ox, oy, oz);
        World3DCamera.Position = camPos;
        var look = focus - camPos;
        if (look.LengthSquared < 1e-12)
            look = new Vector3D(0, 0, -1);
        else
            look.Normalize();
        World3DCamera.LookDirection = look;
        World3DCamera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void World3D_OrbitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;
        Txt3DYawValue.Text = Sld3DYaw.Value.ToString("F0");
        Txt3DPitchValue.Text = Sld3DPitch.Value.ToString("F0");
        Txt3DDistanceValue.Text = Sld3DDistance.Value.ToString("F0");
        Apply3DCameraOnly();
    }

    private void World3D_FovSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || World3DCamera is null)
            return;
        Txt3DFovValue.Text = Sld3DFov.Value.ToString("F0");
        World3DCamera.FieldOfView = Sld3DFov.Value;
    }

    private void World3D_Layer_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        RebuildWorld3DScene();
    }

    private void World3D_Rebuild_Click(object sender, RoutedEventArgs e) => RebuildWorld3DScene();

    private void World3D_RefreshMenu_Click(object sender, RoutedEventArgs e)
    {
        _scene3DStale = true;
        if (Equals(MainTabs.SelectedItem, Scene3DTab))
            RebuildWorld3DScene();
    }

    private void World3D_ApplyFocus_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(Txt3DWorldX.Text, out var x)
            || !double.TryParse(Txt3DWorldY.Text, out var y)
            || !double.TryParse(Txt3DWorldZ.Text, out var z))
        {
            MessageBox.Show(this, "Enter valid X, Y, Z for the orbit focus point.", "3D scene",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _3dFocusWorld = new Vec3 { X = x, Y = y, Z = z };
        _3dFocusInitialized = true;
        PreloadNavGridChunksFor3DView();
        Apply3DCameraOnly();
        if (_world.NavGridCellSource is NavGridChunkCellSource && Equals(MainTabs.SelectedItem, Scene3DTab) &&
            Chk3DTerrain.IsChecked == true)
            RebuildWorld3DScene();
        else if (_world.NavGridCellSource is NavGridChunkCellSource)
            _scene3DStale = true;
    }

    private void World3D_SampleZ_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(Txt3DWorldX.Text, out var x) || !double.TryParse(Txt3DWorldY.Text, out var y))
        {
            MessageBox.Show(this, "Enter X and Y first.", "3D scene", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var z = WorldScene3DBuilder.SampleSurfaceElevation(_world, x, y) + 1.5;
        Txt3DWorldZ.Text = z.ToString("F2");
    }

    private void World3D_CenterBounds_Click(object sender, RoutedEventArgs e)
    {
        var b = _world.GlobalBounds;
        var cx = (b.Min.X + b.Max.X) * 0.5;
        var cy = (b.Min.Y + b.Max.Y) * 0.5;
        var z = WorldScene3DBuilder.SampleSurfaceElevation(_world, cx, cy) + FocusClearanceAboveGround();
        _3dFocusWorld = new Vec3 { X = cx, Y = cy, Z = z };
        Txt3DWorldX.Text = cx.ToString("F1");
        Txt3DWorldY.Text = cy.ToString("F1");
        Txt3DWorldZ.Text = z.ToString("F1");
        _3dFocusInitialized = true;
        var dx = b.Max.X - b.Min.X;
        var dy = b.Max.Y - b.Min.Y;
        var diag = Math.Sqrt(dx * dx + dy * dy);
        if (WorldSceneHelixBuilder.TryGetViewMapping(_world, out _, out var xyScale, out _))
        {
            var viewDiag = diag * xyScale;
            Sld3DDistance.Value = Math.Clamp(viewDiag * 0.55, Sld3DDistance.Minimum, Sld3DDistance.Maximum);
        }
        else
            Sld3DDistance.Value = Math.Clamp(diag * 0.55, Sld3DDistance.Minimum, Sld3DDistance.Maximum);
        PreloadNavGridChunksFor3DView();
        Apply3DCameraOnly();
        if (_world.NavGridCellSource is NavGridChunkCellSource && Equals(MainTabs.SelectedItem, Scene3DTab) &&
            Chk3DTerrain.IsChecked == true)
            RebuildWorld3DScene();
        else if (_world.NavGridCellSource is NavGridChunkCellSource)
            _scene3DStale = true;
    }

    private void WorldViewportDx_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var pitch = Sld3DPitch.Value + e.Delta * 0.048;
            Sld3DPitch.Value = Math.Clamp(pitch, Sld3DPitch.Minimum, Sld3DPitch.Maximum);
            Txt3DPitchValue.Text = Sld3DPitch.Value.ToString("F0");
        }
        else
        {
            var d = Sld3DDistance.Value * Math.Exp(-e.Delta * 0.0011);
            Sld3DDistance.Value = Math.Clamp(d, Sld3DDistance.Minimum, Sld3DDistance.Maximum);
            Txt3DDistanceValue.Text = Sld3DDistance.Value.ToString("F0");
        }

        Apply3DCameraOnly();
        WorldViewportDx.InvalidateRender();
    }

    private void World3D_TerrainDetail_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;
        Txt3DTerrainSubValue.Text = ((int)Math.Clamp(Math.Round(Sld3DTerrainSub.Value), 1, 8)).ToString();
        if (Equals(MainTabs.SelectedItem, Scene3DTab) && Chk3DTerrain.IsChecked == true)
            RebuildWorld3DScene();
    }

    private void World3D_ChunkDetailHalf_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || Txt3DChunkDetailHalfValue is null)
            return;
        Txt3DChunkDetailHalfValue.Text = ((int)Math.Clamp(Math.Round(Sld3DChunkDetailHalf.Value), 0, 512)).ToString();
        if (Equals(MainTabs.SelectedItem, Scene3DTab) && Chk3DTerrain.IsChecked == true)
            RebuildWorld3DScene();
    }

    private void World3D_TerrainHaze_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (Equals(MainTabs.SelectedItem, Scene3DTab) && Chk3DTerrain.IsChecked == true)
            RebuildWorld3DScene();
    }

    private void WorldViewportDx_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        _viewportDragButton = MouseButton.Left;
        _3dLastMouse = e.GetPosition(WorldViewportDx);
        WorldViewportDx.CaptureMouse();
    }

    private void WorldViewportDx_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_viewportDragButton != MouseButton.Left)
            return;
        var p = e.GetPosition(WorldViewportDx);
        var dx = p.X - _3dLastMouse.X;
        var dy = p.Y - _3dLastMouse.Y;
        _3dLastMouse = p;
        Sld3DYaw.Value = Math.Clamp(Sld3DYaw.Value - dx * 0.32, Sld3DYaw.Minimum, Sld3DYaw.Maximum);
        Sld3DPitch.Value = Math.Clamp(Sld3DPitch.Value + dy * 0.32, Sld3DPitch.Minimum, Sld3DPitch.Maximum);
    }

    private void WorldViewportDx_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _viewportDragButton != MouseButton.Left)
            return;
        _viewportDragButton = null;
        WorldViewportDx.ReleaseMouseCapture();
    }

}
