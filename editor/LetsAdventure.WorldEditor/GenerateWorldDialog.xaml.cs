using System.Windows;
using LetsAdventure.Core.World;

namespace LetsAdventure.WorldEditor;

public partial class GenerateWorldDialog : Window
{
    public ProceduralWorldSpec? ResultSpec { get; private set; }

    public GenerateWorldDialog()
    {
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(TxtWidth.Text, out var w) || w <= 0
            || !double.TryParse(TxtHeight.Text, out var h) || h <= 0)
        {
            MessageBox.Show(this, "Width and height must be positive numbers.", "Generate world",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(TxtMinX.Text, out var minX) || !double.TryParse(TxtMinY.Text, out var minY))
        {
            MessageBox.Show(this, "Invalid min X / min Y.", "Generate world", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!double.TryParse(TxtCellSize.Text, out var cell) || cell <= 0
            || !int.TryParse(TxtSeed.Text, out var seed)
            || !double.TryParse(TxtMaxStep.Text, out var maxStep) || maxStep <= 0
            || !double.TryParse(TxtAmplitude.Text, out var amp) || amp <= 0
            || !double.TryParse(TxtFlowAngle.Text, out var flowDeg)
            || !double.TryParse(TxtLakeRadius.Text, out var lakeR) || lakeR <= 0
            || !double.TryParse(TxtRiverHalf.Text, out var riverHalf) || riverHalf <= 0
            || !double.TryParse(TxtLakeDepth.Text, out var lakeDepth) || lakeDepth <= 0)
        {
            MessageBox.Show(this, "Check numeric fields.", "Generate world", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var rad = flowDeg * (Math.PI / 180.0);
        var flow = new GeoVec2 { X = Math.Cos(rad), Y = Math.Sin(rad) };

        var rid = TxtRegionId.Text.Trim();
        if (string.IsNullOrEmpty(rid))
            rid = "region.generated";

        ResultSpec = new ProceduralWorldSpec
        {
            MinX = minX,
            MinY = minY,
            MaxX = minX + w,
            MaxY = minY + h,
            CellSize = cell,
            Seed = seed,
            MaxLandStepOrthogonal = maxStep,
            TerrainAmplitude = amp,
            FlowDirectionDownstream = flow,
            LakeRadiusWorld = lakeR,
            RiverChannelHalfWidthWorld = riverHalf,
            LakeDepth = lakeDepth,
            GeneratedRegionId = rid,
        };

        DialogResult = true;
    }
}
