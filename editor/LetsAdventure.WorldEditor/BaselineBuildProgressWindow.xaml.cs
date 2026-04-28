using System.Windows;

namespace LetsAdventure.WorldEditor;

public partial class BaselineBuildProgressWindow : Window
{
    public BaselineBuildProgressWindow()
    {
        InitializeComponent();
    }

    public void AppendLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLine(line));
            return;
        }

        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        TxtLog.AppendText($"[{stamp}] {line}{Environment.NewLine}");
        TxtLog.CaretIndex = TxtLog.Text.Length;
        TxtLog.ScrollToEnd();
    }

    public void SetBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetBusy(busy));
            return;
        }

        PrgBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PrgBusy.IsIndeterminate = busy;
    }

    public void NotifyComplete()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(NotifyComplete);
            return;
        }

        PrgBusy.IsIndeterminate = false;
        PrgBusy.Visibility = Visibility.Collapsed;
        BtnClose.IsEnabled = true;
    }

    private void BtnClose_OnClick(object sender, RoutedEventArgs e) => Close();
}
