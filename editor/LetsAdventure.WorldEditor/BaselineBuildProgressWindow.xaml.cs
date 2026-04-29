using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace LetsAdventure.WorldEditor;

public partial class BaselineBuildProgressWindow : Window
{
    /// <summary>False while closing or after close — background progress must not touch controls.</summary>
    private volatile bool _acceptsUiUpdates = true;

    private CancellationTokenSource? _cancellation;

    public BaselineBuildProgressWindow()
    {
        InitializeComponent();
    }

    /// <summary>Token observed by baseline generation; cancel to stop cooperative work on the pool thread.</summary>
    public CancellationToken CancellationToken =>
        _cancellation?.Token ?? CancellationToken.None;

    /// <summary>Call after <see cref="Show"/> so <see cref="CancellationToken"/> is valid.</summary>
    public void BeginCancellableBuild()
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        BtnCancel.IsEnabled = true;
        BtnClose.IsEnabled = false;
        TxtStatus.Text = "Building baseline — please wait…";
        PrgBusy.IsIndeterminate = true;
        Mouse.OverrideCursor = Cursors.Wait;
    }

    /// <summary>Use <see cref="Dispatcher.BeginInvoke"/> (not <see cref="Dispatcher.Invoke"/>) for cross-thread
    /// updates so background progress never blocks on the UI thread.</summary>
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

        PrgBusy.IsIndeterminate = busy;
        if (busy)
            TxtStatus.Text = "Working…";
    }

    public void NotifyCancelled()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(NotifyCancelled);
            return;
        }

        if (!_acceptsUiUpdates)
            return;

        TxtStatus.Text = "Build cancelled.";
        PrgBusy.IsIndeterminate = false;
        PrgBusy.Value = 0;
        BtnCancel.IsEnabled = false;
        BtnClose.IsEnabled = true;
        Mouse.OverrideCursor = null;
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

        TxtStatus.Text = "Finished.";
        PrgBusy.IsIndeterminate = false;
        PrgBusy.Value = 100;
        BtnCancel.IsEnabled = false;
        BtnClose.IsEnabled = true;
        Mouse.OverrideCursor = null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!BtnClose.IsEnabled)
        {
            _cancellation?.Cancel();
            e.Cancel = true;
            TxtStatus.Text = "Cancelling — please wait for the current step to stop…";
            return;
        }

        _acceptsUiUpdates = false;
        _cancellation?.Dispose();
        _cancellation = null;
        Mouse.OverrideCursor = null;
        base.OnClosing(e);
    }

    private void BtnCancel_OnClick(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        BtnCancel.IsEnabled = false;
        TxtStatus.Text = "Cancelling — please wait for the current step to stop…";
    }

    private void BtnClose_OnClick(object sender, RoutedEventArgs e) => Close();
}
