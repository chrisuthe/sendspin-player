using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sendspin.Core.Platform;
using Sendspin.Player.ViewModels;
using Sendspin.Player.Services.Client;

#if WINDOWS
using Sendspin.Platform.Windows.Platform;
#else
using Sendspin.Platform.Linux.Platform;
#endif

namespace Sendspin.Player;

/// <summary>
/// The main Avalonia application class for the Sendspin Linux client.
/// Configures dependency injection, services, and the application lifecycle.
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _serviceProvider;

    /// <summary>
    /// Gets the current application instance cast to <see cref="App"/>.
    /// </summary>
    public new static App? Current => Application.Current as App;

    /// <summary>
    /// Gets the service provider for dependency injection.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when services are not yet initialized.</exception>
    public IServiceProvider Services => _serviceProvider
        ?? throw new InvalidOperationException("Services have not been initialized. Ensure the application has started.");

    /// <summary>
    /// Initializes the AXAML components.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Called when the framework initialization is completed.
    /// Sets up dependency injection and creates the main window.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        // Line below is needed to remove Avalonia data validation.
        // Without this line you will get duplicate validations from both Avalonia and CT.
        BindingPlugins.DataValidators.RemoveAt(0);

        // Configure and build services
        ConfigureServices();

        // Ensure platform-specific directories exist
        var paths = Services.GetRequiredService<IPlatformPaths>();
        paths.EnsureDirectoriesExist();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = Services.GetRequiredService<MainViewModel>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            // Handle application shutdown
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Configures the dependency injection container with all required services.
    /// </summary>
    /// <remarks>
    /// Uses the cross-platform architecture with IPlatformInitializer for platform-specific
    /// service registration. The platform is detected at runtime and the appropriate
    /// initializer is used.
    /// </remarks>
    private void ConfigureServices()
    {
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddConsole();
        });

        // Select platform-specific initializer based on compile-time target
#if WINDOWS
        IPlatformInitializer platformInitializer = new WindowsPlatformInitializer();
#else
        IPlatformInitializer platformInitializer = new LinuxPlatformInitializer();
#endif

        // Register all platform-specific services (audio, notifications, paths, etc.)
        platformInitializer.RegisterServices(services);

        // Sendspin client manager - orchestrates SDK components
        // Uses the platform-specific audio player via the factory from the initializer
        services.AddSingleton<SendspinClientManager>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            // Player factory uses the platform-specific IAudioPlayer registration
            Sendspin.SDK.Audio.IAudioPlayer PlayerFactory()
            {
                return sp.GetRequiredService<Sendspin.SDK.Audio.IAudioPlayer>();
            }
            return new SendspinClientManager(loggerFactory, PlayerFactory);
        });

        // ViewModels
        services.AddTransient<MainViewModel>();

        // Build the service provider
        _serviceProvider = services.BuildServiceProvider();

        var logger = _serviceProvider.GetRequiredService<ILogger<App>>();
#if WINDOWS
        logger.LogInformation("Sendspin Windows client initialized");
#else
        logger.LogInformation("Sendspin Linux client initialized");
#endif
    }

    /// <summary>
    /// Handles application shutdown, disposing of services as needed.
    /// </summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        var logger = Services.GetService<ILogger<App>>();
#if WINDOWS
        logger?.LogInformation("Sendspin Windows client shutting down");
#else
        logger?.LogInformation("Sendspin Linux client shutting down");
#endif

        // Dispose the service provider if it implements IDisposable
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
