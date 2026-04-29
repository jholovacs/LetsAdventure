using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
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
    private bool _3dFocusInitialized;
    private double _3dStreamRebuildAnchorX;
    private double _3dStreamRebuildAnchorY;
    private bool _3dStreamRebuildAnchorValid;
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
    private DispatcherTimer? _mapPreviewRedrawTimer;
    private DispatcherTimer? _mapPreviewPersistTimer;
    private bool _mapPreviewNeedsInitialScrollForWorld;
    private CancellationTokenSource? _mapPreviewRasterCts;
    private int _mapPreviewRasterGeneration;
    /// <summary>World changed since last map preview draw; flushed when Map tab is visible at idle priority.</summary>
    private bool _mapPreviewContentStale = true;
    private bool _mapPreviewIdleFlushScheduled;
    /// <summary>Skip scroll-driven debounced redraw while adjusting offsets from code (avoids layout feedback loops).</summary>
    private int _mapPreviewProgrammaticScrollCount;
    /// <summary>Suppress ScrollChanged→debounced DrawPreview until Loaded after DrawPreview (avoids duplicate heavy passes).</summary>
    private int _mapPreviewIgnoreScrollDrawDepth;
    /// <summary>Last nav patch drawn as raster (cell indices). When panning keeps viewport inside this rect, skip full redraw.</summary>
    private bool _mapPreviewRasterCacheValid;
    private int _mapPreviewRasterCacheC0, _mapPreviewRasterCacheC1, _mapPreviewRasterCacheR0, _mapPreviewRasterCacheR1;
    private double _mapPreviewRasterCacheCellPx;
    private double _mapPreviewRasterCacheZoom;
    private bool _mapPreviewRasterCacheHillshade;
    private int _mapPreviewRasterCacheSuperSample;
    private readonly WorldEditorPreferences _preferences = WorldEditorPreferences.Load();
    private int _map2dLevel;
    private double _map2dAnchorCol;
    private double _map2dAnchorRow;
    private Map2dPyramidManifest? _map2dManifest;
    private Image[] _map2dGridImages = [];
    private bool _suppressChkPyramidViewerChanged;
    private bool _map2dPyramidRenderInProgress;
    /// <summary>When equal to current <see cref="_filePath"/> (or both empty), user declined building tiles from the Map preview tab prompt.</summary>
    private string? _map2dPyramidPromptDeclinedForPath;
    private string? _map2dLastFilePathForDeclineTracking;

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
            _mapPreviewRedrawTimer?.Stop();
            _mapPreviewPersistTimer?.Stop();
            try
            {
                PersistMapPreviewViewStateCore();
            }
            catch
            {
                // ignore IO errors on exit
            }

            _mapPreviewRasterCts?.Cancel();
            _mapPreviewRasterCts?.Dispose();
            _mapPreviewRasterCts = null;
            _navChunkSource?.Dispose();
            _navChunkSource = null;
        };
        CmbFeatureKind.ItemsSource = new[]
        {
            "ground_plateau", "path", "mountain_ridge", "water_standing", "water_flowing",
            "building", "vegetation", "solid_volume", "sky_volume",
        };
        CmbFeatureKind.SelectedIndex = 0;
        _map2dGridImages =
        [
            Map2dP00, Map2dP01, Map2dP02, Map2dP10, Map2dP11, Map2dP12, Map2dP20, Map2dP21, Map2dP22,
        ];
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

    private void UpdateMap2dUiState()
    {
        if (BtnRender2dMap is not null)
        {
            var dir = ResolveNavChunkStoreDirectory();
            BtnRender2dMap.IsEnabled = !string.IsNullOrEmpty(dir) &&
                                       NavGridChunkIO.TryLoadManifest(dir, out _);
        }

        if (ChkPyramidViewer is null)
            return;
        var navDir = ResolveNavChunkStoreDirectory();
        var havePyramid = !string.IsNullOrEmpty(navDir) &&
                            Map2dPyramidGenerator.TryLoadManifest(navDir, out _);
        var canBuild = !string.IsNullOrEmpty(navDir) && NavGridChunkIO.TryLoadManifest(navDir, out _);
        ChkPyramidViewer.IsEnabled = canBuild;

        if (!havePyramid)
            TogglePyramidViewerUi(showPyramid: false);

        if (!havePyramid && ChkPyramidViewer.IsChecked == true && !_map2dPyramidRenderInProgress)
            TogglePyramidViewerUi(showPyramid: false);
        else if (havePyramid && ChkPyramidViewer.IsChecked == true && navDir is not null)
        {
            if (!Map2dPyramidGenerator.TryLoadManifest(navDir, out var refreshed) || refreshed is null)
            {
                SetChkPyramidViewerUncheckedSilently();
                TogglePyramidViewerUi(showPyramid: false);
            }
            else
            {
                _map2dManifest = refreshed;
                _map2dLevel = Math.Clamp(_map2dLevel, 0, refreshed.MaxLevel);
                RebuildMap2dPyramidTiles();
                UpdateMap2dViewerStatusLine();
            }
        }
    }

    private static string Map2dPyramidWorldKey(string? filePath) => filePath ?? "";

    private bool Map2dPyramidPromptWasDeclinedForCurrentWorld() =>
        string.Equals(_map2dPyramidPromptDeclinedForPath, Map2dPyramidWorldKey(_filePath),
            StringComparison.OrdinalIgnoreCase);

    private void SetChkPyramidViewerUncheckedSilently()
    {
        if (ChkPyramidViewer is null)
            return;
        _suppressChkPyramidViewerChanged = true;
        try
        {
            ChkPyramidViewer.IsChecked = false;
        }
        finally
        {
            _suppressChkPyramidViewerChanged = false;
        }

        TogglePyramidViewerUi(showPyramid: false);
    }

    private void ShowMap2dRenderProgressUi()
    {
        if (PrgMap2d is not null)
        {
            PrgMap2d.Value = 0;
            PrgMap2d.IsIndeterminate = false;
            PrgMap2d.Visibility = Visibility.Visible;
        }
    }

    private void HideMap2dRenderProgressUi()
    {
        if (PrgMap2d is not null)
        {
            PrgMap2d.Visibility = Visibility.Collapsed;
            PrgMap2d.Value = 0;
        }
    }

    private async Task RunMap2dPyramidRenderAsync()
    {
        var dir = ResolveNavChunkStoreDirectory();
        if (string.IsNullOrEmpty(dir) || !NavGridChunkIO.TryLoadManifest(dir, out _))
            throw new InvalidOperationException(
                "A nav grid folder with manifest.json is required (same folder streaming uses for map preview).");

        var hill = ChkPreviewHillshade is { IsChecked: true };
        var grid = _world.Navigation.Grid;
        _map2dPyramidRenderInProgress = true;
        Mouse.OverrideCursor = Cursors.Wait;
        ShowMap2dRenderProgressUi();
        if (StatusMap2d is not null)
            StatusMap2d.Text = "[map2d] Starting…";
        try
        {
            IProgress<Map2dPyramidProgressReport> prog = new Progress<Map2dPyramidProgressReport>(r =>
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (StatusMap2d is not null)
                        StatusMap2d.Text = r.Message;
                    if (PrgMap2d is not null)
                        PrgMap2d.Value = Math.Clamp(r.PercentComplete, 0, 100);
                }, DispatcherPriority.Background));
            await Task.Run(() => Map2dPyramidGenerator.Generate(dir, grid, hill, prog, CancellationToken.None))
                .ConfigureAwait(true);
            _map2dPyramidPromptDeclinedForPath = null;
        }
        finally
        {
            _map2dPyramidRenderInProgress = false;
            Mouse.OverrideCursor = null;
            HideMap2dRenderProgressUi();
            UpdateMap2dUiState();
        }
    }

    private async Task ConsiderMap2dPyramidPromptOnMapPreviewTabAsync()
    {
        await Task.Yield();
        if (!Equals(MainTabs.SelectedItem, MapPreviewTab) || _map2dPyramidRenderInProgress)
            return;
        var navDir = ResolveNavChunkStoreDirectory();
        if (string.IsNullOrEmpty(navDir) || !NavGridChunkIO.TryLoadManifest(navDir, out _))
            return;
        if (Map2dPyramidGenerator.TryLoadManifest(navDir, out _))
            return;
        if (Map2dPyramidPromptWasDeclinedForCurrentWorld())
            return;

        var r = MessageBox.Show(this,
            "Pre-rendered 2D map tiles are not built for this world yet.\n\nGenerate the map2d pyramid now? Large worlds may take several minutes; watch the status bar for progress.",
            "Map preview — 2D pyramid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (r != MessageBoxResult.Yes)
        {
            _map2dPyramidPromptDeclinedForPath = Map2dPyramidWorldKey(_filePath);
            SetChkPyramidViewerUncheckedSilently();
            if (StatusMap2d is not null)
                StatusMap2d.Text = "";
            return;
        }

        try
        {
            await RunMap2dPyramidRenderAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Render 2D map", MessageBoxButton.OK, MessageBoxImage.Error);
            SetChkPyramidViewerUncheckedSilently();
            if (StatusMap2d is not null)
                StatusMap2d.Text = "";
            return;
        }

        if (!Map2dPyramidGenerator.TryLoadManifest(navDir, out _))
        {
            SetChkPyramidViewerUncheckedSilently();
            return;
        }

        _suppressChkPyramidViewerChanged = true;
        try
        {
            ChkPyramidViewer!.IsChecked = true;
        }
        finally
        {
            _suppressChkPyramidViewerChanged = false;
        }

        if (!InitMap2dPyramidSession())
            SetChkPyramidViewerUncheckedSilently();
        else
            TogglePyramidViewerUi(showPyramid: true);
        UpdateMap2dViewerStatusLine();
    }

    private void TogglePyramidViewerUi(bool showPyramid)
    {
        if (MapPreviewScrollViewer is null || Map2dPyramidHost is null)
            return;
        if (showPyramid)
        {
            MapPreviewScrollViewer.Visibility = Visibility.Collapsed;
            Map2dPyramidHost.Visibility = Visibility.Visible;
        }
        else
        {
            MapPreviewScrollViewer.Visibility = Visibility.Visible;
            Map2dPyramidHost.Visibility = Visibility.Collapsed;
        }
    }

    private bool InitMap2dPyramidSession()
    {
        var navDir = ResolveNavChunkStoreDirectory();
        if (string.IsNullOrEmpty(navDir) ||
            !Map2dPyramidGenerator.TryLoadManifest(navDir, out var m) || m is null)
            return false;
        _map2dManifest = m;
        _map2dLevel = m.MaxLevel;
        _map2dAnchorCol = Math.Clamp(m.Columns / 2.0, 0, Math.Max(0, m.Columns - 1e-9));
        _map2dAnchorRow = Math.Clamp(m.Rows / 2.0, 0, Math.Max(0, m.Rows - 1e-9));
        RebuildMap2dPyramidTiles();
        UpdateMap2dViewerStatusLine();
        return true;
    }

    private void RebuildMap2dPyramidTiles()
    {
        if (_map2dManifest is null || string.IsNullOrEmpty(_navChunkStoreDirectoryAbsolute))
            return;
        var m = _map2dManifest;
        var L = _map2dLevel;
        var tilePx = m.TilePixels;
        var stride = 1 << L;
        var cellSpan = tilePx * stride;
        var txN = Map2dPyramidTileMath.TileCountX(m.Columns, L, tilePx);
        var tyN = Map2dPyramidTileMath.TileCountY(m.Rows, L, tilePx);
        var centerTx = (int)Math.Clamp(Math.Floor(_map2dAnchorCol / cellSpan), 0, Math.Max(0, txN - 1));
        var centerTy = (int)Math.Clamp(Math.Floor(_map2dAnchorRow / cellSpan), 0, Math.Max(0, tyN - 1));
        var root = Map2dPyramidGenerator.ResolvePyramidRoot(_navChunkStoreDirectoryAbsolute);

        for (var gr = 0; gr < 3; gr++)
        {
            for (var gc = 0; gc < 3; gc++)
            {
                var img = _map2dGridImages[gr * 3 + gc];
                var tx = centerTx + gc - 1;
                var ty = centerTy + gr - 1;
                if (tx < 0 || ty < 0 || tx >= txN || ty >= tyN)
                {
                    img.Source = null;
                    img.Visibility = Visibility.Collapsed;
                    continue;
                }

                img.Visibility = Visibility.Visible;
                var rel = Map2dPyramidTileMath.RelativeTilePath(L, tx, ty, m.ShardSpan);
                var path = System.IO.Path.Combine(root, rel);
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    img.Source = bmp;
                }
                catch
                {
                    img.Source = null;
                    img.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void UpdateMap2dViewerStatusLine()
    {
        if (StatusMap2d is null || _map2dManifest is null)
            return;
        var stride = 1 << _map2dLevel;
        var step = _map2dManifest.MaxLevel - _map2dLevel + 1;
        var steps = _map2dManifest.MaxLevel + 1;
        StatusMap2d.Text =
            $"2D map: zoom step {step}/{steps} (1=full overview, {steps}=1 cell/pixel); stride {stride} cell{(stride == 1 ? "" : "s")}/px";
    }

    private async void Render2dMap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunMap2dPyramidRenderAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Render 2D map", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Render 2D map", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var dir = ResolveNavChunkStoreDirectory();
        if (ChkPyramidViewer is { IsChecked: true } && !string.IsNullOrEmpty(dir) &&
            Map2dPyramidGenerator.TryLoadManifest(dir, out var m) && m is not null)
        {
            _map2dManifest = m;
            _map2dLevel = Math.Clamp(_map2dLevel, 0, m.MaxLevel);
            RebuildMap2dPyramidTiles();
            UpdateMap2dViewerStatusLine();
        }
        else if (StatusMap2d is not null && ChkPyramidViewer is not { IsChecked: true })
            StatusMap2d.Text = "";
    }

    private async void ChkPyramidViewer_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkPyramidViewer is null)
            return;
        if (_suppressChkPyramidViewerChanged)
        {
            if (ChkPyramidViewer.IsChecked != true)
                TogglePyramidViewerUi(showPyramid: false);
            return;
        }

        if (ChkPyramidViewer.IsChecked != true)
        {
            TogglePyramidViewerUi(showPyramid: false);
            if (StatusMap2d is not null)
                StatusMap2d.Text = "";
            RequestMapPreviewIdleFlush();
            return;
        }

        var navDir = ResolveNavChunkStoreDirectory();
        if (string.IsNullOrEmpty(navDir) || !NavGridChunkIO.TryLoadManifest(navDir, out _))
        {
            MessageBox.Show(this,
                "A nav chunk store with manifest.json is required before using the 2D pyramid viewer.",
                "Map preview", MessageBoxButton.OK, MessageBoxImage.Information);
            SetChkPyramidViewerUncheckedSilently();
            return;
        }

        if (Map2dPyramidGenerator.TryLoadManifest(navDir, out _))
        {
            if (!InitMap2dPyramidSession())
                SetChkPyramidViewerUncheckedSilently();
            else
                TogglePyramidViewerUi(showPyramid: true);
            Map2dPyramidHost?.Focus();
            UpdateMap2dViewerStatusLine();
            return;
        }

        var r = MessageBox.Show(this,
            "Pre-rendered 2D map tiles are not built yet.\n\nGenerate the map2d pyramid now? Progress appears in the status bar.",
            "Map preview — 2D pyramid",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (r != MessageBoxResult.Yes)
        {
            SetChkPyramidViewerUncheckedSilently();
            return;
        }

        try
        {
            await RunMap2dPyramidRenderAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Render 2D map", MessageBoxButton.OK, MessageBoxImage.Error);
            SetChkPyramidViewerUncheckedSilently();
            return;
        }

        if (!InitMap2dPyramidSession())
            SetChkPyramidViewerUncheckedSilently();
        else
            TogglePyramidViewerUi(showPyramid: true);
        Map2dPyramidHost?.Focus();
        UpdateMap2dViewerStatusLine();
    }

    private void Map2dPyramidHost_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Equals(MainTabs.SelectedItem, MapPreviewTab) || _map2dManifest is null || Map2dPyramidHost is null)
            return;
        e.Handled = true;

        var newLevel = _map2dLevel;
        if (e.Delta > 0)
        {
            if (_map2dLevel <= 0)
                return;
            newLevel = _map2dLevel - 1;
        }
        else
        {
            if (_map2dLevel >= _map2dManifest.MaxLevel)
                return;
            newLevel = _map2dLevel + 1;
        }

        var w = Map2dPyramidHost.ActualWidth;
        var h = Map2dPyramidHost.ActualHeight;
        if (w <= 1e-6 || h <= 1e-6)
        {
            _map2dLevel = newLevel;
            RebuildMap2dPyramidTiles();
            UpdateMap2dViewerStatusLine();
            return;
        }

        var p = e.GetPosition(Map2dPyramidHost);
        var fx = Math.Clamp(p.X / w, 0, 1);
        var fy = Math.Clamp(p.Y / h, 0, 1);

        Map2dReanchorPyramidForNewLevel(newLevel, fx, fy);
        RebuildMap2dPyramidTiles();
        UpdateMap2dViewerStatusLine();
    }

    /// <summary>Keeps the world point under (fx,fy) stable after changing pyramid level (cursor-centric zoom).</summary>
    private void Map2dReanchorPyramidForNewLevel(int newLevel, double fx, double fy)
    {
        var m = _map2dManifest!;
        var tilePx = m.TilePixels;
        var cols = m.Columns;
        var rows = m.Rows;

        var L0 = _map2dLevel;
        var stride0 = 1 << L0;
        var cellSpan0 = tilePx * stride0;
        var txN0 = Map2dPyramidTileMath.TileCountX(cols, L0, tilePx);
        var tyN0 = Map2dPyramidTileMath.TileCountY(rows, L0, tilePx);
        var cx0 = (int)Math.Clamp(Math.Floor(_map2dAnchorCol / cellSpan0), 0, Math.Max(0, txN0 - 1));
        var cy0 = (int)Math.Clamp(Math.Floor(_map2dAnchorRow / cellSpan0), 0, Math.Max(0, tyN0 - 1));
        var txL0 = Math.Max(0, cx0 - 1);
        var txR0 = Math.Min(txN0 - 1, cx0 + 1);
        var cMin0 = txL0 * (double)cellSpan0;
        var cMaxEx0 = Math.Min(cols, (long)(txR0 + 1) * cellSpan0);
        var tyT0 = Math.Max(0, cy0 - 1);
        var tyB0 = Math.Min(tyN0 - 1, cy0 + 1);
        var rMin0 = tyT0 * (double)cellSpan0;
        var rMaxEx0 = Math.Min(rows, (long)(tyB0 + 1) * cellSpan0);

        var visW0 = Math.Max(1e-9, cMaxEx0 - cMin0);
        var visH0 = Math.Max(1e-9, rMaxEx0 - rMin0);
        var wcf = cMin0 + fx * visW0;
        var wrf = rMin0 + fy * visH0;
        wcf = Math.Clamp(wcf, 0, Math.Max(0, cols - 1e-9));
        wrf = Math.Clamp(wrf, 0, Math.Max(0, rows - 1e-9));

        _map2dLevel = newLevel;
        var stride1 = 1 << newLevel;
        var cellSpan1 = tilePx * stride1;
        var txN1 = Map2dPyramidTileMath.TileCountX(cols, newLevel, tilePx);
        var tyN1 = Map2dPyramidTileMath.TileCountY(rows, newLevel, tilePx);
        var idealW = Math.Min(3.0 * cellSpan1, cols);
        var idealH = Math.Min(3.0 * cellSpan1, rows);
        var targetCMin = wcf - fx * idealW;
        var targetRMin = wrf - fy * idealH;

        var centerTx1 = (int)Math.Clamp(Math.Floor(targetCMin / cellSpan1) + 1, 0, Math.Max(0, txN1 - 1));
        var centerTy1 = (int)Math.Clamp(Math.Floor(targetRMin / cellSpan1) + 1, 0, Math.Max(0, tyN1 - 1));
        _map2dAnchorCol = (centerTx1 + 0.5) * cellSpan1;
        _map2dAnchorRow = (centerTy1 + 0.5) * cellSpan1;
    }

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
        _mapPreviewNeedsInitialScrollForWorld = true;
        _mapPreviewContentStale = true;
        ApplyMapPreviewDefaultsOrSavedForCurrentWorld();
        RequestMapPreviewIdleFlush();
        _scene3DStale = true;
        _3dFocusInitialized = false;
        _3dStreamRebuildAnchorValid = false;
        if (!string.Equals(_map2dLastFilePathForDeclineTracking, _filePath, StringComparison.OrdinalIgnoreCase))
        {
            _map2dPyramidPromptDeclinedForPath = null;
            _map2dLastFilePathForDeclineTracking = _filePath;
        }
        if (Equals(MainTabs.SelectedItem, Scene3DTab))
            RebuildWorld3DScene();
        UpdateMap2dUiState();
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

        _navChunkSource = NavGridChunkCellSource.TryOpen(dir, maxCachedChunks: 512);
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

    /// <summary>Opens a folder picker and returns a directory that contains a valid nav grid manifest.</summary>
    private bool TryPickNavGridChunkDirectory(out string? directory)
    {
        directory = null;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the .navgrid folder (must contain manifest.json)",
            InitialDirectory = TryMapInitialDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true)
            return false;
        var dir = dlg.FolderName;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return false;
        dir = System.IO.Path.GetFullPath(dir);
        if (!NavGridChunkIO.TryLoadManifest(dir, out var manifest) || manifest is null)
        {
            MessageBox.Show(this,
                "No valid manifest.json found in that folder.\n\nSelect the folder that contains manifest.json (often named *.navgrid).",
                "Nav grid", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        directory = dir;
        return true;
    }

    private const double ViewerStreamHalfExtentM = WorldSceneHelixBuilder.Editor3DTerrainClipHalfExtentMeters;
    private const double CameraHeightAboveGroundM = 2;
    private const double WasdMoveSpeedMetersPerSecond = 12;
    private const double TerrainStreamRebuildStepM = 200;

    private void PreloadNavGridChunksFor3DView()
    {
        if (_world.NavGridCellSource is not NavGridChunkCellSource src)
            return;
        var cs = Math.Max(1e-9, src.Manifest.CellSize);
        var cw = Math.Max(1, src.Manifest.ChunkWidthCells);
        var chunkWm = cw * cs;
        var radius = Math.Max(1, (int)Math.Ceiling(ViewerStreamHalfExtentM / chunkWm) + 1);
        src.PreloadChunksAroundWorldXY(_3dFocusWorld.X, _3dFocusWorld.Y, radius);
        src.RetainChunksIntersectingWorldSquare(_3dFocusWorld.X, _3dFocusWorld.Y, ViewerStreamHalfExtentM);
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
        string navSampleSpacingText,
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

        double? navSampleSpacing = null;
        var navTrim = navSampleSpacingText.Trim();
        if (navTrim.Length > 0)
        {
            if (!double.TryParse(navTrim, NumberStyles.Float, CultureInfo.InvariantCulture, out var ns)
                || !double.IsFinite(ns)
                || ns <= 0)
            {
                error = "Nav XY sample (m) must be empty for the default baseline grid, or a positive finite number.";
                return false;
            }

            navSampleSpacing = ns;
        }

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
            NavSampleSpacingMeters = navSampleSpacing,
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
                TxtNavSampleSpacing.Text,
                out var request,
                out var parseErr))
        {
            MessageBox.Show(this, parseErr, "Baseline map", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folderDlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder for baseline output (physical_world.json and physical_world.navgrid will be written here)",
            InitialDirectory = _preferences.GetBaselineOutputDirectoryInitialSuggestion(),
        };
        if (folderDlg.ShowDialog(this) != true)
            return;

        var rawDir = folderDlg.FolderName?.Trim();
        if (string.IsNullOrWhiteSpace(rawDir))
        {
            MessageBox.Show(this, "Choose a valid folder for baseline output.", "Baseline map",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string baselineDir;
        try
        {
            baselineDir = System.IO.Path.GetFullPath(rawDir);
        }
        catch
        {
            MessageBox.Show(this, "Choose a valid folder for baseline output.", "Baseline map",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(baselineDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not use that folder: " + ex.Message, "Baseline map",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _preferences.BaselineOutputDirectory = baselineDir;
        _preferences.Save();

        var baselineJson = System.IO.Path.Combine(baselineDir, "physical_world.json");

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
        logWindow.BeginCancellableBuild();
        IProgress<string> progress = new Progress<string>(logWindow.AppendLine);
        try
        {
            PhysicalWorldGenerationResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(
                    () => BaselinePhysicalWorldGenerator.Generate(request, progress, logWindow.CancellationToken),
                    logWindow.CancellationToken ).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                logWindow.AppendLine("");
                logWindow.AppendLine("Baseline build cancelled by user.");
                logWindow.NotifyCancelled();
                return;
            }

            _world = result.World;
            _filePath = null;

            progress.Report("Saving baseline to disk (required before continuing)…");
            try
            {
                if (!await SaveBaselineWorldAsync(baselineJson, progress).ConfigureAwait(true))
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
                $"Closing this window opens the 3D scene tab. Output folder:\n{baselineDir}\nUse Save As to copy elsewhere. On the 3D tab: WASD walks on the ground (north = +Y); the view streams nav chunks in a 4 km window around you; mouse wheel adjusts field of view.");
            baselineSucceeded = true;
            logWindow.NotifyComplete();
            _ = Dispatcher.BeginInvoke(RefreshUiFromWorld, DispatcherPriority.Background);
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            logWindow.AppendLine("");
            logWindow.AppendLine("Baseline build cancelled.");
            logWindow.NotifyCancelled();
        }
        catch (Exception ex)
        {
            logWindow.AppendLine("");
            logWindow.AppendLine("Failed: " + ex.Message);
            logWindow.NotifyComplete();
        }
    }

    private void LoadMap_Click(object sender, RoutedEventArgs e) => _ = LoadMapFromFolderAsync();

    private async Task LoadMapFromFolderAsync()
    {
        if (!WarnDiscardUnsaved())
            return;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder containing physical_world.json",
            InitialDirectory = TryMapInitialDirectory(),
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true)
            return;
        var dir = dlg.FolderName;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;
        var worldPath = System.IO.Path.Combine(dir, "physical_world.json");
        if (!File.Exists(worldPath))
        {
            MessageBox.Show(this,
                $"No physical_world.json found in:\n{dir}\n\nUse File → Open… to pick a JSON file in another layout.",
                "Load map", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        var prevStatus = StatusPath.Text;
        StatusPath.Text = "Loading world…";
        try
        {
            var world = await Task.Run(() => WorldDocumentService.Load(worldPath)).ConfigureAwait(true);
            _world = world;
            _filePath = worldPath;
            PersistLastOpenedWorldLocation(worldPath);
            RefreshUiFromWorld();
            ClearDirty();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusPath.Text = prevStatus;
            MessageBox.Show(this, ex.Message, "Load map", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            if (StatusPath.Text == "Loading world…")
                UpdateStatus();
        }
    }

    private string? TryMapInitialDirectory()
    {
        if (!string.IsNullOrEmpty(_filePath))
        {
            var d = System.IO.Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                return d;
        }
        if (!string.IsNullOrEmpty(_preferences.LastOpenedWorldPath))
        {
            try
            {
                var fullWorld = System.IO.Path.GetFullPath(_preferences.LastOpenedWorldPath);
                var d = System.IO.Path.GetDirectoryName(fullWorld);
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                    return d;
            }
            catch
            {
                // ignore
            }
        }
        if (!string.IsNullOrEmpty(_navChunkStoreDirectoryAbsolute) && Directory.Exists(_navChunkStoreDirectoryAbsolute))
            return _navChunkStoreDirectoryAbsolute;
        if (!string.IsNullOrEmpty(_preferences.BaselineOutputDirectory))
        {
            try
            {
                var b = System.IO.Path.GetFullPath(_preferences.BaselineOutputDirectory);
                if (Directory.Exists(b))
                    return b;
            }
            catch
            {
                // ignore invalid path
            }
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _ = OpenWorldAsync();

    private async Task OpenWorldAsync()
    {
        if (!WarnDiscardUnsaved())
            return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Physical world JSON|physical_world.json;*.json|All files|*.*",
            InitialDirectory = TryMapInitialDirectory() ?? "",
        };
        if (dlg.ShowDialog(this) != true)
            return;

        Mouse.OverrideCursor = Cursors.Wait;
        var prevStatus = StatusPath.Text;
        StatusPath.Text = "Loading world…";
        try
        {
            var path = dlg.FileName;
            var world = await Task.Run(() => WorldDocumentService.Load(path)).ConfigureAwait(true);
            _world = world;
            _filePath = path;
            PersistLastOpenedWorldLocation(path);
            RefreshUiFromWorld();
            ClearDirty();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusPath.Text = prevStatus;
            MessageBox.Show(this, ex.Message, "Open world", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            if (StatusPath.Text == "Loading world…")
                UpdateStatus();
        }
    }

    private void RequestMapPreviewIdleFlush()
    {
        if (_mapPreviewIdleFlushScheduled)
            return;
        _mapPreviewIdleFlushScheduled = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            _mapPreviewIdleFlushScheduled = false;
            ProcessPendingMapPreviewWork();
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Runs deferred map preview work on the UI thread after layout/input (cheap tab switches, no sync DrawPreview from RefreshUiFromWorld).</summary>
    private void ProcessPendingMapPreviewWork()
    {
        if (!Equals(MainTabs.SelectedItem, MapPreviewTab) || PreviewCanvas is null)
            return;
        if (ChkPyramidViewer is { IsChecked: true })
            return;

        if (!_mapPreviewContentStale && !_mapPreviewNeedsInitialScrollForWorld)
            return;

        if (_mapPreviewNeedsInitialScrollForWorld)
        {
            _mapPreviewNeedsInitialScrollForWorld = false;
            if (TryEnsureMapPreviewCanvasSizeFromGrid())
            {
                MapPreviewScrollViewer?.UpdateLayout();
                PreviewCanvas.UpdateLayout();
                BeginProgrammaticMapScroll(ApplyMapPreviewInitialScrollCore);
                ScheduleDebouncedMapPreviewPersist();
            }
        }

        _mapPreviewContentStale = false;
        _mapPreviewIgnoreScrollDrawDepth++;
        try
        {
            DrawPreview();
        }
        finally
        {
            _ = Dispatcher.BeginInvoke(() => { _mapPreviewIgnoreScrollDrawDepth--; }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Sets canvas logical size from nav grid so scroll metrics exist before the first DrawPreview (initial fit/scroll).</summary>
    private bool TryEnsureMapPreviewCanvasSizeFromGrid()
    {
        if (PreviewCanvas is null || SldRasterCellPx is null)
            return false;
        var grid = _world.Navigation.Grid;
        if (grid is null || grid.Columns < 1 || grid.Rows < 1)
            return false;

        const double marginOx = 40;
        const double marginOy = 40;
        var cellPx = SldRasterCellPx.Value;
        PreviewCanvas.LayoutTransform = _mapPreviewScaleTransform;
        if (!WorldNavGridPreviewRenderer.TryGetMapPreviewPixelsPerWorldMeter(_world, cellPx,
                _navChunkStoreDirectoryAbsolute, out _, out _))
            return false;

        var cw = grid.Columns * cellPx;
        var ch = grid.Rows * cellPx;
        PreviewCanvas.Width = Math.Max(480, marginOx + cw + 40);
        PreviewCanvas.Height = Math.Max(480, marginOy + ch + 40);
        return true;
    }

    private void ApplyMapPreviewInitialScrollCore()
    {
        if (MapPreviewScrollViewer is null || PreviewCanvas is null)
            return;
        if (string.IsNullOrEmpty(_filePath) || !_preferences.TryGetMapPreviewState(_filePath, out var st))
        {
            FitMapPreviewToScroll();
            return;
        }

        MapPreviewScrollViewer.UpdateLayout();
        PreviewCanvas.UpdateLayout();
        var maxX = Math.Max(0, MapPreviewScrollViewer.ScrollableWidth);
        var maxY = Math.Max(0, MapPreviewScrollViewer.ScrollableHeight);
        MapPreviewScrollViewer.ScrollToHorizontalOffset(Math.Clamp(st.HorizontalOffset, 0, maxX));
        MapPreviewScrollViewer.ScrollToVerticalOffset(Math.Clamp(st.VerticalOffset, 0, maxY));
    }

    private void BeginProgrammaticMapScroll(Action action)
    {
        _mapPreviewProgrammaticScrollCount++;
        try
        {
            action();
        }
        finally
        {
            _mapPreviewProgrammaticScrollCount--;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveInternal(promptPath: false);

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveInternal(promptPath: true);

    /// <summary>
    /// Baseline save: offloads chunked nav export / session copy to the thread pool so the UI stays responsive.
    /// Overview PNG and JSON use WPF / shared document state and run on the UI thread.
    /// </summary>
    private async Task<bool> SaveBaselineWorldAsync(string baselineJson, IProgress<string>? progress)
    {
        EnsureNavBundle();
        var dir = System.IO.Path.GetDirectoryName(baselineJson);
        if (string.IsNullOrEmpty(dir))
            return false;
        Directory.CreateDirectory(dir);

        var grid = _world.Navigation.Grid;
        if (grid is not null)
        {
            var baseName = System.IO.Path.GetFileNameWithoutExtension(baselineJson);
            var sidecar = System.IO.Path.Combine(dir, baseName + ".navgrid");
            if (grid.Cells is { Count: var nc } && nc >= NavGridChunkIO.AutoExportCellThreshold)
            {
                progress?.Report($"Writing nav grid chunks to {sidecar}…");
                await Task.Run(() => NavGridChunkIO.ExportToDirectory(grid, sidecar, progress: progress))
                    .ConfigureAwait(true);
                WorldNavGridPreviewRenderer.TryWriteOverviewPng(grid,
                    System.IO.Path.Combine(sidecar, "preview.png"));
                grid.NavGridChunkStoreRelativePath = System.IO.Path.GetFileName(sidecar);
                grid.NavGridChunkSessionDirectoryAbsolute = null;
                grid.Cells = null;
                _navChunkSource?.Dispose();
                _navChunkSource = NavGridChunkCellSource.TryOpen(sidecar, maxCachedChunks: 512);
                _navChunkStoreDirectoryAbsolute = sidecar;
                _world.NavGridCellSource = _navChunkSource;
            }
            else if (!string.IsNullOrEmpty(grid.NavGridChunkSessionDirectoryAbsolute))
            {
                var session = grid.NavGridChunkSessionDirectoryAbsolute;
                progress?.Report($"Copying nav grid chunk session to {sidecar}…");
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(sidecar);
                    CopyDirectoryRecursive(session, sidecar);
                }).ConfigureAwait(true);
                grid.NavGridChunkStoreRelativePath = System.IO.Path.GetFileName(sidecar);
                grid.NavGridChunkSessionDirectoryAbsolute = null;
                _navChunkSource?.Dispose();
                _navChunkSource = NavGridChunkCellSource.TryOpen(sidecar, maxCachedChunks: 512);
                _navChunkStoreDirectoryAbsolute = sidecar;
                _world.NavGridCellSource = _navChunkSource;
            }
        }

        progress?.Report($"Writing {baselineJson}…");
        WorldDocumentService.Save(baselineJson, _world);
        _world.NavGridCellSource = _navChunkSource;
        _filePath = baselineJson;
        PersistLastOpenedWorldLocation(baselineJson);
        return true;
    }

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
                _navChunkSource = NavGridChunkCellSource.TryOpen(sidecar, maxCachedChunks: 512);
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
                _navChunkSource = NavGridChunkCellSource.TryOpen(sidecar, maxCachedChunks: 512);
                _navChunkStoreDirectoryAbsolute = sidecar;
                _world.NavGridCellSource = _navChunkSource;
            }
        }

        progress?.Report($"Writing {path}…");
        WorldDocumentService.Save(path, _world);
        _world.NavGridCellSource = _navChunkSource;
        _filePath = path;
        PersistLastOpenedWorldLocation(path);
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
            if (dlg.ShowDialog(this) != true)
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
        if (dlg.ShowDialog(this) != true)
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

        // World +Y is north; +X is east. Camera looks toward north in view space (fixed heading).
        var step = WasdMoveSpeedMetersPerSecond * _3dMoveTimer.Interval.TotalSeconds;
        double wx = 0, wy = 0;
        if (_3dKeysDown.Contains(Key.W))
            wy += step;
        if (_3dKeysDown.Contains(Key.S))
            wy -= step;
        if (_3dKeysDown.Contains(Key.D))
            wx += step;
        if (_3dKeysDown.Contains(Key.A))
            wx -= step;

        if (wx == 0 && wy == 0)
            return;

        var nx = _3dFocusWorld.X + wx;
        var ny = _3dFocusWorld.Y + wy;
        var ground = WorldScene3DBuilder.SampleSurfaceElevation(_world, nx, ny);
        _3dFocusWorld = new Vec3 { X = nx, Y = ny, Z = ground + CameraHeightAboveGroundM };
        _3dFocusInitialized = true;
        Txt3DWorldX.Text = _3dFocusWorld.X.ToString("F1");
        Txt3DWorldY.Text = _3dFocusWorld.Y.ToString("F1");
        Txt3DWorldZ.Text = _3dFocusWorld.Z.ToString("F1");

        PreloadNavGridChunksFor3DView();
        if (MaybeRebuild3DTerrainAfterMove())
            return;
        Apply3DCameraOnly();
        WorldViewportDx.InvalidateRender();
    }

    /// <summary>Re-centers the high-res terrain patch after the camera has walked far enough (chunked worlds).</summary>
    private bool MaybeRebuild3DTerrainAfterMove()
    {
        if (!Equals(MainTabs.SelectedItem, Scene3DTab))
            return false;
        if (_world.NavGridCellSource is not NavGridChunkCellSource)
            return false;
        if (Chk3DTerrain.IsChecked != true)
            return false;
        if (!_3dStreamRebuildAnchorValid)
            return false;
        var dx = _3dFocusWorld.X - _3dStreamRebuildAnchorX;
        var dy = _3dFocusWorld.Y - _3dStreamRebuildAnchorY;
        if (dx * dx + dy * dy < TerrainStreamRebuildStepM * TerrainStreamRebuildStepM)
            return false;
        RebuildWorld3DScene();
        return true;
    }

    private void Capture3DTerrainStreamAnchor()
    {
        _3dStreamRebuildAnchorX = _3dFocusWorld.X;
        _3dStreamRebuildAnchorY = _3dFocusWorld.Y;
        _3dStreamRebuildAnchorValid = true;
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
            if (!TryPickNavGridChunkDirectory(out dir) || string.IsNullOrEmpty(dir))
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
            if (!TryPickNavGridChunkDirectory(out dir) || string.IsNullOrEmpty(dir))
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
        if (e.AddedItems.Count > 0 && ReferenceEquals(e.AddedItems[0], MapPreviewTab))
            _ = ConsiderMap2dPyramidPromptOnMapPreviewTabAsync();

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
            if (ChkPyramidViewer is { IsChecked: true })
                Map2dPyramidHost?.Focus();
            else
                MapPreviewScrollViewer?.Focus();
            RequestMapPreviewIdleFlush();
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
        ScheduleDebouncedMapPreviewPersist();
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
        ScheduleDebouncedMapPreviewDraw();
        ScheduleDebouncedMapPreviewPersist();
    }

    private void MapPreviewScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!Equals(MainTabs.SelectedItem, MapPreviewTab) || PreviewCanvas is null)
            return;
        if (_mapPreviewProgrammaticScrollCount > 0)
        {
            ScheduleDebouncedMapPreviewPersist();
            return;
        }

        if (_mapPreviewIgnoreScrollDrawDepth > 0)
        {
            ScheduleDebouncedMapPreviewPersist();
            return;
        }
        if (ChkPreviewRaster is not { IsChecked: true })
        {
            ScheduleDebouncedMapPreviewDraw();
            ScheduleDebouncedMapPreviewPersist();
            return;
        }

        if (TryMapPreviewViewportServedByCachedRaster())
        {
            ScheduleDebouncedMapPreviewPersist();
            return;
        }

        ScheduleDebouncedMapPreviewDraw();
        ScheduleDebouncedMapPreviewPersist();
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

    private async void GenerateChunkMapTiles_Click(object sender, RoutedEventArgs e)
    {
        var dir = ResolveNavChunkStoreDirectory();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            MessageBox.Show(this,
                "Could not find a nav grid chunk directory. Open a world that uses on-disk chunks, or ensure physical_world.json sits next to the .navgrid folder.",
                "Chunk map tiles", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!NavGridChunkIO.TryLoadManifest(dir, out var manifest) || manifest is null)
        {
            MessageBox.Show(this, "manifest.json is missing or invalid in the chunk directory.", "Chunk map tiles",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hill = ChkPreviewHillshade is { IsChecked: true };
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Task.Run(() =>
                NavGridChunkPreviewGenerator.Export(dir, pixelsPerNavCell: 1, hillshade: hill, progress: null));
            var subfolder = string.IsNullOrWhiteSpace(manifest.ChunkPreviewPngSubfolder)
                ? "preview_chunks"
                : manifest.ChunkPreviewPngSubfolder;
            MessageBox.Show(this,
                    $"Wrote PNG tiles under:\n{System.IO.Path.Combine(dir, subfolder)}\nand updated manifest.json (chunkPreviewPixelsPerNavCell, chunkPreviewHillshade).\n\nFast map preview uses tiles when hillshade matches the value stored in the manifest.",
                "Chunk map tiles", MessageBoxButton.OK, MessageBoxImage.Information);
            DrawPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Chunk map tiles", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

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
        ScheduleDebouncedMapPreviewPersist();
    }

    private void DrawPreview()
    {
        if (PreviewCanvas is null)
            return;
        if (ChkPyramidViewer is { IsChecked: true })
            return;
        _mapPreviewRasterCacheValid = false;
        PreviewCanvas.Children.Clear();
        _mapPreviewRasterCts?.Cancel();
        _mapPreviewRasterCts?.Dispose();
        _mapPreviewRasterCts = new CancellationTokenSource();
        _mapPreviewRasterGeneration++;
        var mapRasterGen = _mapPreviewRasterGeneration;
        HideMapPreviewRasterBusyUi();

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
        var scheduleAsyncMapRaster = false;

        if (ChkPreviewRaster is { IsChecked: true } && SldRasterCellPx is not null)
        {
            var hill = ChkPreviewHillshade is { IsChecked: true };
            var cellRasterPx = SldRasterCellPx.Value;
            var onMapTab = Equals(MainTabs.SelectedItem, MapPreviewTab);
            var haveNavPatch = false;
            int navPc0 = 0, navPc1 = 0, navPr0 = 0, navPr1 = 0, navSuper = 1;
            if (onMapTab && gridForMap is not null
                         && TryComputeMapPreviewNavPatch(gridForMap, cellRasterPx, marginOx, marginOy, out navPc0,
                             out navPc1, out navPr0, out navPr1, out navSuper))
            {
                haveNavPatch = true;
                var pcCells = navPc1 - navPc0 + 1;
                var prCells = navPr1 - navPr0 + 1;
                var patchPixels = (long)pcCells * prCells * navSuper * navSuper;
                var fromChunks = _world.NavGridCellSource is INavGridCellSource;
                var cellsInMemory = gridForMap.Cells is { Count: var cc } && cc >= (long)gridForMap.Columns * gridForMap.Rows;
                var canEncodeHighResPatch = fromChunks || cellsInMemory;
                var heavyRaster = canEncodeHighResPatch &&
                                  (patchPixels >= 220_000L || (fromChunks && patchPixels >= 50_000L));
                if (heavyRaster)
                {
                    scheduleAsyncMapRaster = true;
                    ShowMapPreviewRasterBusyUi("Rendering map preview…");
                    var token = _mapPreviewRasterCts.Token;
                    var gen = mapRasterGen;
                    var world = _world;
                    var chunkDir = _navChunkStoreDirectoryAbsolute;
                    var apc0 = navPc0;
                    var apc1 = navPc1;
                    var apr0 = navPr0;
                    var apr1 = navPr1;
                    var asuper = navSuper;
                    var apcCells = pcCells;
                    var aprCells = prCells;
                    IProgress<int> prog = new Progress<int>(pct =>
                    {
                        _ = Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (gen != _mapPreviewRasterGeneration || PrgMapPreviewRaster is null)
                                return;
                            PrgMapPreviewRaster.Value = pct;
                            if (TxtMapPreviewRasterBusy is not null)
                                TxtMapPreviewRasterBusy.Text =
                                    $"Rendering map preview — {pct}% (reading nav data; wait cursor active)";
                        }), DispatcherPriority.Background);
                    });
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (!WorldNavGridPreviewRenderer.TryEncodeNavGridPatchForMapPreview(world, hill, chunkDir,
                                    false, apc0, apc1, apr0, apr1, asuper, prog, token, out var data) || data is null)
                                return (NavGridPatchBitmapData?)null;
                            token.ThrowIfCancellationRequested();
                            return data;
                        }
                        catch (OperationCanceledException)
                        {
                            return (NavGridPatchBitmapData?)null;
                        }
                    }, token).ContinueWith(
                        t =>
                        {
                            _ = Dispatcher.BeginInvoke(() =>
                            {
                                HideMapPreviewRasterBusyUi();
                                if (gen != _mapPreviewRasterGeneration || PreviewCanvas is null ||
                                    !Equals(MainTabs.SelectedItem, MapPreviewTab))
                                    return;
                                if (t.IsCanceled || t.IsFaulted)
                                    return;
                                if (!t.Result.HasValue)
                                    return;
                                var img = WorldNavGridPreviewRenderer.CreateNavGridPatchImage(t.Result.Value, cellRasterPx,
                                    marginOx, marginOy, apc0, apr0, apcCells, aprCells);
                                if (img is null)
                                    return;
                                Canvas.SetZIndex(img, 0);
                                PreviewCanvas.Children.Insert(0, img);
                                CommitMapPreviewRasterCache(apc0, apc1, apr0, apr1, cellRasterPx, asuper, hill);
                            }, DispatcherPriority.Background);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
                else
                {
                    raster = WorldNavGridPreviewRenderer.TryBuildNavGridImage(_world, cellRasterPx, hill, marginOx,
                        marginOy, _navChunkStoreDirectoryAbsolute,
                        fullGridOnly: false, navPc0, navPc1, navPr0, navPr1, navSuper);
                }
            }
            else
            {
                raster = WorldNavGridPreviewRenderer.TryBuildNavGridImage(_world, cellRasterPx, hill, marginOx,
                    marginOy, _navChunkStoreDirectoryAbsolute);
            }

            if (!scheduleAsyncMapRaster && raster is not null)
            {
                Canvas.SetZIndex(raster, 0);
                PreviewCanvas.Children.Add(raster);
                if (haveNavPatch)
                    CommitMapPreviewRasterCache(navPc0, navPc1, navPr0, navPr1, cellRasterPx, navSuper, hill);
                else if (gridForMap is not null)
                    CommitMapPreviewRasterCache(0, gridForMap.Columns - 1, 0, gridForMap.Rows - 1, cellRasterPx, 1, hill);
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

        // Canvas spans full nav grid in cell space (patch rasters only cover a sub-rectangle).
        if (haveGridMap && gridForMap is not null)
        {
            var cw = gridForMap.Columns * cellPx;
            var ch = gridForMap.Rows * cellPx;
            PreviewCanvas.Width = Math.Max(480, marginOx + cw + 40);
            PreviewCanvas.Height = Math.Max(480, marginOy + ch + 40);
        }
        else if (raster is not null)
        {
            PreviewCanvas.Width = Math.Max(480, marginOx + raster.Width + 40);
            PreviewCanvas.Height = Math.Max(480, marginOy + raster.Height + 40);
        }
        else
        {
            PreviewCanvas.Width = 2000;
            PreviewCanvas.Height = 2000;
        }

        _mapPreviewContentStale = false;
    }

    private void HideMapPreviewRasterBusyUi()
    {
        if (TxtMapPreviewRasterBusy is not null)
        {
            TxtMapPreviewRasterBusy.Visibility = Visibility.Collapsed;
            TxtMapPreviewRasterBusy.Text = "";
        }

        if (PrgMapPreviewRaster is not null)
        {
            PrgMapPreviewRaster.Visibility = Visibility.Collapsed;
            PrgMapPreviewRaster.Value = 0;
        }

        if (Equals(MainTabs.SelectedItem, MapPreviewTab))
            Mouse.OverrideCursor = null;
    }

    private void ShowMapPreviewRasterBusyUi(string message)
    {
        if (TxtMapPreviewRasterBusy is not null)
        {
            TxtMapPreviewRasterBusy.Text = message;
            TxtMapPreviewRasterBusy.Visibility = Visibility.Visible;
        }

        if (PrgMapPreviewRaster is not null)
        {
            PrgMapPreviewRaster.Value = 0;
            PrgMapPreviewRaster.Visibility = Visibility.Visible;
        }

        if (Equals(MainTabs.SelectedItem, MapPreviewTab))
            Mouse.OverrideCursor = Cursors.Wait;
    }

    private bool TryMapPreviewViewportServedByCachedRaster()
    {
        if (!_mapPreviewRasterCacheValid)
            return false;
        if (!Equals(MainTabs.SelectedItem, MapPreviewTab) || PreviewCanvas is null)
            return false;

        var grid = _world.Navigation.Grid;
        if (grid is null || grid.Columns < 1 || grid.Rows < 1 || MapPreviewScrollViewer is null)
            return false;
        if (Math.Abs((SldRasterCellPx?.Value ?? 1) - _mapPreviewRasterCacheCellPx) > 1e-6)
            return false;
        if (Math.Abs(_mapPreviewZoom - _mapPreviewRasterCacheZoom) > 1e-6)
            return false;
        var hill = ChkPreviewHillshade is { IsChecked: true };
        if (hill != _mapPreviewRasterCacheHillshade)
            return false;

        const double marginOx = 40;
        const double marginOy = 40;
        var cellPx = SldRasterCellPx?.Value ?? 1;
        if (!TryGetMapPreviewVisibleNavCellRange(grid, cellPx, marginOx, marginOy, out var vc0, out var vc1, out var vr0,
                out var vr1))
            return false;
        if (vc0 < _mapPreviewRasterCacheC0 || vc1 > _mapPreviewRasterCacheC1 ||
            vr0 < _mapPreviewRasterCacheR0 || vr1 > _mapPreviewRasterCacheR1)
            return false;
        if (!TryComputeMapPreviewNavPatch(grid, cellPx, marginOx, marginOy, out _, out _, out _, out _, out var ssNow))
            return false;

        return ssNow == _mapPreviewRasterCacheSuperSample;
    }

    private bool TryGetMapPreviewVisibleNavCellRange(
        TerrainNavGridDefinition grid,
        double cellPx,
        double marginOx,
        double marginOy,
        out int vc0,
        out int vc1,
        out int vr0,
        out int vr1)
    {
        vc0 = vc1 = vr0 = vr1 = 0;
        var cols = grid.Columns;
        var rows = grid.Rows;
        if (cols < 1 || rows < 1 || MapPreviewScrollViewer is null)
            return false;
        if (!TryGetMapPreviewViewCanvasBounds(grid, cellPx, marginOx, marginOy, out var left, out var top, out var right,
                out var bottom))
            return false;

        cellPx = Math.Max(1e-6, cellPx);
        vc0 = (int)Math.Floor(left / cellPx);
        vc1 = (int)Math.Ceiling(right / cellPx) - 1;
        vr0 = (int)Math.Floor(top / cellPx);
        vr1 = (int)Math.Ceiling(bottom / cellPx) - 1;
        vc0 = Math.Clamp(vc0, 0, cols - 1);
        vc1 = Math.Clamp(vc1, 0, cols - 1);
        vr0 = Math.Clamp(vr0, 0, rows - 1);
        vr1 = Math.Clamp(vr1, 0, rows - 1);
        if (vc1 < vc0)
            (vc0, vc1) = (vc1, vc0);
        if (vr1 < vr0)
            (vr0, vr1) = (vr1, vr0);
        return true;
    }

    /// <summary>Visible map area in canvas coordinates (same space as cell placement: origin at margin corner).</summary>
    private bool TryGetMapPreviewViewCanvasBounds(
        TerrainNavGridDefinition grid,
        double cellPx,
        double marginOx,
        double marginOy,
        out double left,
        out double top,
        out double right,
        out double bottom)
    {
        left = top = right = bottom = 0;
        var cols = grid.Columns;
        var rows = grid.Rows;
        if (cols < 1 || rows < 1)
            return false;

        var sv = MapPreviewScrollViewer;
        if (sv is null)
            return false;

        sv.UpdateLayout();
        PreviewCanvas?.UpdateLayout();

        var ho = sv.HorizontalOffset;
        var vo = sv.VerticalOffset;
        var vw = sv.ViewportWidth;
        var vh = sv.ViewportHeight;
        if (vw < 1 || vh < 1)
            return false;

        cellPx = Math.Max(1e-6, cellPx);
        var logicalW = Math.Max(480.0, marginOx + cols * cellPx + 40);
        var logicalH = Math.Max(480.0, marginOy + rows * cellPx + 40);
        var extW = sv.ExtentWidth;
        var extH = sv.ExtentHeight;
        var ratioX = logicalW > 1e-6 ? extW / logicalW : 1.0;
        var ratioY = logicalH > 1e-6 ? extH / logicalH : 1.0;
        var ratio = Math.Clamp((ratioX + ratioY) * 0.5, 0.25, 256.0);
        var z = Math.Max(1e-9, _mapPreviewZoom);
        double scrollScale;
        if (extW < 2 || extH < 2)
            scrollScale = z;
        else
        {
            var matchesZoom = ratio >= z * 0.88 && ratio <= z * 1.12;
            var nearUnity = ratio >= 0.97 && ratio <= 1.03;
            if (matchesZoom)
                scrollScale = ratio;
            else if (nearUnity)
                scrollScale = 1.0;
            else
                scrollScale = ratio;
        }

        left = ho / scrollScale - marginOx;
        top = vo / scrollScale - marginOy;
        right = left + vw / scrollScale;
        bottom = top + vh / scrollScale;
        return true;
    }

    private void CommitMapPreviewRasterCache(int c0, int c1, int r0, int r1, double cellRasterPx, int superSample,
        bool hillshade)
    {
        _mapPreviewRasterCacheValid = true;
        _mapPreviewRasterCacheC0 = c0;
        _mapPreviewRasterCacheC1 = c1;
        _mapPreviewRasterCacheR0 = r0;
        _mapPreviewRasterCacheR1 = r1;
        _mapPreviewRasterCacheCellPx = cellRasterPx;
        _mapPreviewRasterCacheZoom = _mapPreviewZoom;
        _mapPreviewRasterCacheHillshade = hillshade;
        _mapPreviewRasterCacheSuperSample = superSample;
    }

    /// <summary>
    /// Visible nav cell range in map preview (unscaled canvas coords). <paramref name="superSample"/> is chosen from
    /// zoom and capped so patch bitmaps stay bounded.
    /// </summary>
    private bool TryComputeMapPreviewNavPatch(
        TerrainNavGridDefinition grid,
        double cellPx,
        double marginOx,
        double marginOy,
        out int c0,
        out int c1,
        out int r0,
        out int r1,
        out int superSample)
    {
        c0 = c1 = r0 = r1 = 0;
        superSample = 1;
        var cols = grid.Columns;
        var rows = grid.Rows;
        if (cols < 1 || rows < 1)
            return false;

        if (!TryGetMapPreviewViewCanvasBounds(grid, cellPx, marginOx, marginOy, out var left, out var top, out var right,
                out var bottom))
            return false;

        cellPx = Math.Max(1e-6, cellPx);

        const int pad = 2;
        c0 = (int)Math.Floor(left / cellPx) - pad;
        c1 = (int)Math.Ceiling(right / cellPx) + pad - 1;
        r0 = (int)Math.Floor(top / cellPx) - pad;
        r1 = (int)Math.Ceiling(bottom / cellPx) + pad - 1;

        c0 = Math.Clamp(c0, 0, cols - 1);
        c1 = Math.Clamp(c1, 0, cols - 1);
        r0 = Math.Clamp(r0, 0, rows - 1);
        r1 = Math.Clamp(r1, 0, rows - 1);
        if (c1 < c0)
            (c0, c1) = (c1, c0);
        if (r1 < r0)
            (r0, r1) = (r1, r0);

        if (_world.NavGridCellSource is INavGridCellSource
            || (grid.Columns * (long)grid.Rows >= NavGridChunkIO.AutoExportCellThreshold
                && grid.Cells is { Count: var n } && n >= (long)grid.Columns * grid.Rows))
        {
            var spanC = c1 - c0 + 1;
            var spanR = r1 - r0 + 1;
            var extra = Math.Clamp(Math.Max(spanC, spanR) / 2, 128, 400);
            c0 = Math.Clamp(c0 - extra, 0, cols - 1);
            c1 = Math.Clamp(c1 + extra, 0, cols - 1);
            r0 = Math.Clamp(r0 - extra, 0, rows - 1);
            r1 = Math.Clamp(r1 + extra, 0, rows - 1);
        }

        superSample = (int)Math.Clamp(Math.Ceiling(_mapPreviewZoom), 1, 8);
        var pc = c1 - c0 + 1;
        var pr = r1 - r0 + 1;
        const long maxPatchPixels = 14_000_000L;
        while (superSample > 1 && (long)pc * pr * superSample * superSample > maxPatchPixels)
            superSample--;

        return true;
    }

    private void ScheduleDebouncedMapPreviewDraw()
    {
        if (PreviewCanvas is null)
            return;
        if (_mapPreviewRedrawTimer is null)
        {
            _mapPreviewRedrawTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _mapPreviewRedrawTimer.Tick += (_, _) =>
            {
                _mapPreviewRedrawTimer!.Stop();
                if (_mapPreviewIgnoreScrollDrawDepth > 0)
                    return;
                DrawPreview();
            };
        }

        _mapPreviewRedrawTimer.Stop();
        _mapPreviewRedrawTimer.Start();
    }

    private void PersistLastOpenedWorldLocation(string path)
    {
        try
        {
            _preferences.LastOpenedWorldPath = System.IO.Path.GetFullPath(path);
            _preferences.Save();
        }
        catch
        {
            // ignore invalid path / IO
        }
    }

    private void ApplyMapPreviewDefaultsOrSavedForCurrentWorld()
    {
        if (string.IsNullOrEmpty(_filePath))
            return;
        if (_preferences.TryGetMapPreviewState(_filePath, out var st))
        {
            _mapPreviewZoom = Math.Clamp(st.Zoom, 1e-6, 64);
            if (SldRasterCellPx is not null)
                SldRasterCellPx.Value = Math.Clamp(st.RasterCellPx, SldRasterCellPx.Minimum, SldRasterCellPx.Maximum);
        }
        else
            _mapPreviewZoom = 1;

        _mapPreviewScaleTransform.ScaleX = _mapPreviewZoom;
        _mapPreviewScaleTransform.ScaleY = _mapPreviewZoom;
    }

    private void PersistMapPreviewViewStateCore()
    {
        if (string.IsNullOrEmpty(_filePath) || MapPreviewScrollViewer is null)
            return;
        var st = new MapPreviewViewState
        {
            HorizontalOffset = MapPreviewScrollViewer.HorizontalOffset,
            VerticalOffset = MapPreviewScrollViewer.VerticalOffset,
            Zoom = _mapPreviewZoom,
            RasterCellPx = SldRasterCellPx?.Value ?? 3,
        };
        _preferences.SetMapPreviewState(_filePath, st);
        _preferences.Save();
    }

    private void ScheduleDebouncedMapPreviewPersist()
    {
        if (string.IsNullOrEmpty(_filePath))
            return;
        if (_mapPreviewPersistTimer is null)
        {
            _mapPreviewPersistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _mapPreviewPersistTimer.Tick += (_, _) =>
            {
                _mapPreviewPersistTimer!.Stop();
                try
                {
                    PersistMapPreviewViewStateCore();
                }
                catch
                {
                    // ignore
                }
            };
        }

        _mapPreviewPersistTimer.Stop();
        _mapPreviewPersistTimer.Start();
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
            BeginProgrammaticMapScroll(() =>
            {
                _mapPreviewZoom = Math.Clamp(fit, 1e-6, 64);
                PreviewCanvas.LayoutTransform = _mapPreviewScaleTransform;
                _mapPreviewScaleTransform.ScaleX = _mapPreviewZoom;
                _mapPreviewScaleTransform.ScaleY = _mapPreviewZoom;
                MapPreviewScrollViewer.ScrollToHorizontalOffset(0);
                MapPreviewScrollViewer.ScrollToVerticalOffset(0);
            });
            ScheduleDebouncedMapPreviewPersist();
            _mapPreviewRasterCacheValid = false;
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
            BeginProgrammaticMapScroll(() =>
            {
                _mapPreviewZoom = Math.Clamp(fit, 1e-6, 64);
                PreviewCanvas.LayoutTransform = _mapPreviewScaleTransform;
                _mapPreviewScaleTransform.ScaleX = _mapPreviewZoom;
                _mapPreviewScaleTransform.ScaleY = _mapPreviewZoom;
                MapPreviewScrollViewer.ScrollToHorizontalOffset(0);
                MapPreviewScrollViewer.ScrollToVerticalOffset(0);
            });
            _mapPreviewRasterCacheValid = false;
            _mapPreviewContentStale = true;
            RequestMapPreviewIdleFlush();
            ScheduleDebouncedMapPreviewPersist();
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
        _3dFocusWorld = new Vec3 { X = cx, Y = cy, Z = ground + CameraHeightAboveGroundM };
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
        var chunkHalfSlider = Sld3DChunkDetailHalf is not null
            ? (int)Math.Clamp(Math.Round(Sld3DChunkDetailHalf.Value), 0, 512)
            : 72;
        var clipM = WorldSceneHelixBuilder.Editor3DTerrainClipHalfExtentMeters;
        var cellSz = 1d;
        if (_world.NavGridCellSource is NavGridChunkCellSource chSrc)
            cellSz = Math.Max(1e-9, chSrc.Manifest.CellSize);
        else if (_world.Navigation.Grid is { CellSize: var gcs } && gcs > 0)
            cellSz = gcs;
        var detailCellsForClip = (int)Math.Ceiling(clipM / cellSz);
        var detailHalf = Math.Clamp(Math.Min(chunkHalfSlider, detailCellsForClip), 8, 512);
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
            TerrainDetailCellHalfExtent = detailHalf,
            TerrainClipHalfExtentM = clipM,
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
        Capture3DTerrainStreamAnchor();
        Apply3DCameraOnly();
        WorldViewportDx.InvalidateRender();
    }

    private void Apply3DCameraOnly()
    {
        if (World3DCamera is null)
            return;
        var ground = WorldScene3DBuilder.SampleSurfaceElevation(_world, _3dFocusWorld.X, _3dFocusWorld.Y);
        var eyeWorld = new Vec3
        {
            X = _3dFocusWorld.X,
            Y = _3dFocusWorld.Y,
            Z = ground + CameraHeightAboveGroundM,
        };
        Point3D camPos;
        if (WorldSceneHelixBuilder.TryGetViewMapping(_world, out var origin, out var xyScale, out var hScale,
                WorldSceneHelixBuilder.Editor3DTerrainClipHalfExtentMeters))
            camPos = WorldSceneHelixBuilder.ScaledFocusFromWorld(origin, eyeWorld, xyScale, hScale);
        else
            camPos = WorldScene3DBuilder.WorldToWpf(eyeWorld);
        World3DCamera.Position = camPos;
        // World north (+Y) maps to decreasing Helix Z; horizontal look (0,0,-1) is north along the ground plane.
        World3DCamera.LookDirection = new Vector3D(0, 0, -1);
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
            || !double.TryParse(Txt3DWorldY.Text, out var y))
        {
            MessageBox.Show(this, "Enter valid world X and Y for the camera position.", "3D scene",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var g = WorldScene3DBuilder.SampleSurfaceElevation(_world, x, y);
        _3dFocusWorld = new Vec3 { X = x, Y = y, Z = g + CameraHeightAboveGroundM };
        _3dFocusInitialized = true;
        Txt3DWorldZ.Text = _3dFocusWorld.Z.ToString("F1");
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

        var z = WorldScene3DBuilder.SampleSurfaceElevation(_world, x, y) + CameraHeightAboveGroundM;
        Txt3DWorldZ.Text = z.ToString("F2");
    }

    private void World3D_CenterBounds_Click(object sender, RoutedEventArgs e)
    {
        var b = _world.GlobalBounds;
        var cx = (b.Min.X + b.Max.X) * 0.5;
        var cy = (b.Min.Y + b.Max.Y) * 0.5;
        var z = WorldScene3DBuilder.SampleSurfaceElevation(_world, cx, cy) + CameraHeightAboveGroundM;
        _3dFocusWorld = new Vec3 { X = cx, Y = cy, Z = z };
        Txt3DWorldX.Text = cx.ToString("F1");
        Txt3DWorldY.Text = cy.ToString("F1");
        Txt3DWorldZ.Text = z.ToString("F1");
        _3dFocusInitialized = true;
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
        var fov = Sld3DFov.Value + e.Delta * 0.04;
        Sld3DFov.Value = Math.Clamp(fov, Sld3DFov.Minimum, Sld3DFov.Maximum);
        Txt3DFovValue.Text = Sld3DFov.Value.ToString("F0");
        if (World3DCamera is not null)
            World3DCamera.FieldOfView = Sld3DFov.Value;
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

}

