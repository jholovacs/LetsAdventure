using System.Windows;
using System.Windows.Threading;

namespace LetsAdventure.WorldEditor;

public partial class BaselineBuildProgressWindow : Window
{
    /// <summary>False while closing or after close — background progress must not touch controls.</summary>
    private volatile bool _acceptsUiUpdates = true;

    public BaselineBuildProgressWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Use <see cref="Dispatcher.BeginInvoke"/> (not <see cref="Dispatcher.Invoke"/>) for cross-thread
    /// updates so background progress never blocks on the UI thread — synchronous Invoke deadlocks when
    /// the UI is busy (e.g. closing this window or rebuilding the 3D scene).
    /// </summary>
    public void AppendLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(AppendLine, DispatcherPriority.Normal, line);
            return;
        }

        if (!_acceptsUiUpdates)
            return;

        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        TxtLog.AppendText($"[{stamp}] {line}{Environment.NewLine}");
        TxtLog.CaretIndex = TxtLog.Text.Length;
        TxtLog.ScrollToEnd();
    }

    public void SetBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(SetBusy, DispatcherPriority.Normal, busy);
            return;
        }

        if (!_acceptsUiUpdates)
            return;

        PrgBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrgBusy.IsIndeterminate = busy;
    }

    public void NotifyComplete()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(NotifyComplete);
            return;
        }

        if (!_acceptsUiUpdates)
            return;

        PrgBusy.IsIndeterminate = false;
        PrgBusy.Visibility = Visibility.Collapsed;
        BtnClose.IsEnabled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!BtnClose.IsEnabled)
        {
            e.Cancel = true;
            return;
        }

        _acceptsUiUpdates = false;
        base.OnClosing(e);
    }

    private void BtnClose_OnClick(object sender, RoutedEventArgs e) => Close();
}
