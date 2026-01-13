using System;
using Avalonia.Controls;

namespace SendspinClient.Linux;

/// <summary>
/// Main application window for the Sendspin Linux client.
/// Provides the primary user interface for audio playback control and server connection management.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Called when the window is closing. Ensures proper cleanup of async resources.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is IAsyncDisposable vm)
        {
            await vm.DisposeAsync();
        }
        base.OnClosing(e);
    }
}
