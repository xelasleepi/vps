using System;
using System.Collections.Specialized;
using System.Windows;
using Setup.UI.Interop;
using Setup.UI.ViewModels;

namespace Setup;

/// <summary>
/// The single application window: a terminal-style deployment console. The
/// integrator assigns a <see cref="MainViewModel"/> as the DataContext; this
/// code-behind only wires the caption buttons, auto-scroll of the log, and the
/// Windows 11 window effects (dark title bar, rounded corners, Mica).
/// </summary>
public partial class MainWindow : Window
{
    private INotifyCollectionChanged? _observedLog;

    /// <summary>Initializes the window and hooks lifecycle events.</summary>
    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Apply Mica / dark chrome / rounded corners; degrades gracefully.
        WindowEffects.Apply(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Fallback DataContext for standalone runs (integrator normally sets one).
        if (DataContext is null)
            DataContext = MainViewModel.DesignInstance;

        HookLog(DataContext as MainViewModel);
        ScrollLogToEnd();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        HookLog(e.NewValue as MainViewModel);
        ScrollLogToEnd();
    }

    /// <summary>Subscribes to the log collection so new lines auto-scroll into view.</summary>
    private void HookLog(MainViewModel? viewModel)
    {
        if (_observedLog is not null)
        {
            _observedLog.CollectionChanged -= OnLogChanged;
            _observedLog = null;
        }

        if (viewModel?.LogLines is INotifyCollectionChanged incc)
        {
            _observedLog = incc;
            _observedLog.CollectionChanged += OnLogChanged;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollLogToEnd();
    }

    /// <summary>Scrolls the log list to its newest entry, if any.</summary>
    private void ScrollLogToEnd()
    {
        if (LogList is null || LogList.Items.Count == 0) return;

        // Defer to render priority so virtualization has laid out the new item.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var last = LogList.Items[LogList.Items.Count - 1];
                if (last is not null)
                    LogList.ScrollIntoView(last);
            }
            catch
            {
                // Non-fatal: a transient layout race, ignore.
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => Close();
}
