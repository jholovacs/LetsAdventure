using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private readonly ObservableCollection<NavNodeRow> _navNodes = [];
    private readonly ObservableCollection<NavEdgeRow> _navEdges = [];

    private bool _scene3DStale = true;
    private Vec3 _3dFocusWorld;
    private Point _3dLastMouse;
    private MouseButton? _viewportDragButton;
    private bool _3dFocusInitialized;
    private readonly HelixToolkit.Wpf.SharpDX.DefaultEffectsManager _helixEffects = new();
    private readonly HashSet<Key> _3dKeysDown = [];
    private readonly DispatcherTimer _3dMoveTimer;

    public MainWindow()
    {
        InitializeComponent();
        WorldViewportDx.EffectsManager = _helixEffects;
        _3dMoveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        _3dMoveTimer.Tick += (_, _) => Tick3DWasdPan();
        _3dMoveTimer.Start();
        Closed += (_, _) =>
        {
            _helixEffects.Dispose();
            _3dMoveTimer.Stop();
        };
        GridNavNodes.ItemsSource = _navNodes;
        GridNavEdges.ItemsSource = _navEdges;
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

        var g = _world.Navigation.Grid ??= new TerrainNavGridDefinition();
        TxtGridOriginX.Text = g.OriginX.ToString();
        TxtGridOriginY.Text = g.OriginY.ToString();
        TxtGridCellSize.Text = g.CellSize.ToString();
        TxtGridColumns.Text = g.Columns.ToString();
        TxtGridRows.Text = g.Rows.ToString();

        NavRowSync.LoadGraph(_world.Navigation.Graph, _navNodes, _navEdges);
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
        _world = WorldDocumentService.CreateNew();
        _filePath = null;
        RefreshUiFromWorld();
        ClearDirty();
        UpdateStatus();
    }

    private void GenerateWorld_Click(object sender, RoutedEventArgs e)
    {
        if (!WarnDiscardUnsaved())
            return;
        var dlg = new GenerateWorldDialog { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultSpec is null)
            return;

        var result = ProceduralPhysicalWorldGenerator.Generate(dlg.ResultSpec);
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
            $"Max land orth. slope: {r.MaxObservedLandOrthogonalSlope:F2} (limit {dlg.ResultSpec.MaxLandStepOrthogonal:F2}); violation edges: {r.LandSlopeViolationCount}.");
        lines.Add(
            $"River ends in lake disk: {r.RiverTerminatesInLakeRegion}; river→water connectivity: {r.AllFlowingCellsReachStandingWater} (river cells failing: {r.WaterConnectivityFailures}).");
        if (dlg.ResultSpec.UsePerimeterOcean)
            lines.Add(
                $"Coastal hydrology: lake cells without ocean path: {r.LakeBasinDisconnectedFromOceanCells}; downstream profile violations: {r.RiverCenterlineDownstreamGradientViolations}.");

        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            "Procedural world",
            MessageBoxButton.OK,
            r.IsSufficientlyNavigable ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void BaselineWorld_Click(object sender, RoutedEventArgs e)
    {
        if (!WarnDiscardUnsaved())
            return;

        var logWindow = new BaselineBuildProgressWindow { Owner = this };
        var baselineSucceeded = false;
        logWindow.Closed += (_, _) =>
        {
            if (!baselineSucceeded)
                return;
            MainTabs.SelectedItem = Scene3DTab;
        };
        logWindow.Show();
        logWindow.SetBusy(true);
        var progress = new Progress<string>(logWindow.AppendLine);
        try
        {
            var result = await System.Threading.Tasks.Task.Run(() => BaselinePhysicalWorldGenerator.Generate(null, progress))
                .ConfigureAwait(true);

            _world = result.World;
            _filePath = null;
            RefreshUiFromWorld();
            MarkDirty();
            UpdateStatus();

            logWindow.AppendLine("");
            logWindow.AppendLine(
                $"Extent XY {BaselinePhysicalWorldGenerator.DefaultExtentXy:F0}, Z [{BaselinePhysicalWorldGenerator.GroundLevelZ} … {BaselinePhysicalWorldGenerator.DefaultMaxZ}].");
            logWindow.AppendLine(
                $"Regions: {_world.RegionBoundaries.Count}, territories: {_world.Territories.Count}, features: {_world.Features.Count}.");
            foreach (var msg in result.Report.Messages)
                logWindow.AppendLine(msg);
            logWindow.AppendLine(result.Report.IsSufficientlyNavigable
                ? "Hydrology / slope checks passed."
                : "Review hydrology / slope warnings above.");
            logWindow.AppendLine(
                "Closing this window opens the 3D scene tab. WASD pans; LMB orbits; mouse wheel zooms; Shift+wheel changes orbit elevation. Raise “Terrain mesh subdivisions” for close-up detail.");
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

    private void SaveInternal(bool promptPath)
    {
        EnsureNavBundle();
        NavRowSync.SaveGraph(_world.Navigation.Graph!, _navNodes, _navEdges);
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

        WorldDocumentService.Save(path, _world);
        _filePath = path;
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
        if (Equals(MainTabs.SelectedItem, Scene3DTab)
            && Keyboard.Modifiers == ModifierKeys.None
            && Keyboard.FocusedElement is not TextBox
            && Keyboard.FocusedElement is not RichTextBox)
        {
            if (e.Key is Key.W or Key.A or Key.S or Key.D)
            {
                _3dKeysDown.Add(e.Key);
                e.Handled = true;
                return;
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
            _3dKeysDown.Remove(e.Key);
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

    private void NavNodeAdd_Click(object sender, RoutedEventArgs e)
    {
        EnsureNavBundle();
        _navNodes.Add(new NavNodeRow { Id = "nav.new", Z = 1 });
        MarkDirty();
    }

    private void NavNodeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GridNavNodes.SelectedItem is NavNodeRow r)
        {
            _navNodes.Remove(r);
            MarkDirty();
        }
    }

    private void NavEdgeAdd_Click(object sender, RoutedEventArgs e)
    {
        _navEdges.Add(new NavEdgeRow { FromId = "nav.a", ToId = "nav.b" });
        MarkDirty();
    }

    private void NavEdgeRemove_Click(object sender, RoutedEventArgs e)
    {
        if (GridNavEdges.SelectedItem is NavEdgeRow r)
        {
            _navEdges.Remove(r);
            MarkDirty();
        }
    }

    private void NavGridApply_Click(object sender, RoutedEventArgs e)
    {
        EnsureNavBundle();
        var g = _world.Navigation.Grid!;
        if (double.TryParse(TxtGridOriginX.Text, out var ox))
            g.OriginX = ox;
        if (double.TryParse(TxtGridOriginY.Text, out var oy))
            g.OriginY = oy;
        if (double.TryParse(TxtGridCellSize.Text, out var cs))
            g.CellSize = cs;
        if (int.TryParse(TxtGridColumns.Text, out var c))
            g.Columns = Math.Max(1, c);
        if (int.TryParse(TxtGridRows.Text, out var r))
            g.Rows = Math.Max(1, r);
        MarkDirty();
    }

    private void GridNav_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => MarkDirty();

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Equals(MainTabs.SelectedItem, NavGraphTab))
        {
            EnsureNavBundle();
            NavRowSync.LoadGraph(_world.Navigation.Graph, _navNodes, _navEdges);
        }

        if (Equals(MainTabs.SelectedItem, Scene3DTab))
        {
            Ensure3DFocusInitialized();
            if (_scene3DStale)
                RebuildWorld3DScene();
            else
                Apply3DCameraOnly();
        }
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
        var scale = SldPreviewScale.Value;
        const double ox = 40;
        const double oy = 40;
        Image? raster = null;

        if (ChkPreviewRaster is { IsChecked: true } && SldRasterCellPx is not null)
        {
            var hill = ChkPreviewHillshade is { IsChecked: true };
            raster = WorldNavGridPreviewRenderer.TryBuildNavGridImage(_world, SldRasterCellPx.Value, hill, ox, oy);
            if (raster is not null)
                PreviewCanvas.Children.Add(raster);
        }

        if (ChkPreviewVectors is { IsChecked: true })
        {
            foreach (var reg in _world.RegionBoundaries)
            {
                var el = PolyFromVertices(reg.Boundary.Vertices, scale, ox, oy, Brushes.DodgerBlue, 1.2);
                if (el is not null)
                    PreviewCanvas.Children.Add(el);
            }

            foreach (var terr in _world.Territories)
            {
                var el = PolyFromVertices(terr.Boundary.Vertices, scale, ox, oy, Brushes.OrangeRed, 1);
                if (el is not null)
                    PreviewCanvas.Children.Add(el);
            }

            foreach (var f in _world.Features)
            {
                switch (f)
                {
                    case PathCorridorFeature p:
                        var line = PolylineFromPath(p.Centerline, scale, ox, oy, Brushes.ForestGreen, 2);
                        if (line is not null)
                            PreviewCanvas.Children.Add(line);
                        break;
                    case GroundPlateauFeature g:
                        var gp = PolyFromVertices(g.Boundary, scale, ox, oy, Brushes.LightGreen, 0.6);
                        if (gp is not null)
                            PreviewCanvas.Children.Add(gp);
                        break;
                    case StandingWaterFeature w:
                        var wp = PolyFromVertices(w.Shoreline, scale, ox, oy, Brushes.DeepSkyBlue, 0.8);
                        if (wp is not null)
                            PreviewCanvas.Children.Add(wp);
                        break;
                    case FlowingWaterFeature fw:
                        var riverLine = PolylineFromPath(fw.ChannelCenterline, scale, ox, oy, Brushes.CornflowerBlue, 2.5);
                        if (riverLine is not null)
                            PreviewCanvas.Children.Add(riverLine);
                        break;
                }
            }
        }

        // Size from raster (cell space); vector overlays use world XY and can be huge — do not expand canvas from them.
        if (raster is not null)
        {
            PreviewCanvas.Width = Math.Max(480, ox + raster.Width + 40);
            PreviewCanvas.Height = Math.Max(480, oy + raster.Height + 40);
        }
        else
        {
            PreviewCanvas.Width = 2000;
            PreviewCanvas.Height = 2000;
        }
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
        var opt = new WorldScene3DOptions
        {
            Terrain = Chk3DTerrain.IsChecked == true,
            Regions = Chk3DRegions.IsChecked == true,
            Territories = Chk3DTerritories.IsChecked == true,
            Features = Chk3DFeatures.IsChecked == true,
            NavGraph = Chk3DNavGraph.IsChecked == true,
            TerrainSubdivisionsPerCell = (int)Math.Clamp(Math.Round(Sld3DTerrainSub.Value), 1, 8),
            TerrainPerspectiveAnchorWorld = _3dFocusWorld,
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
        Apply3DCameraOnly();
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
        Apply3DCameraOnly();
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
