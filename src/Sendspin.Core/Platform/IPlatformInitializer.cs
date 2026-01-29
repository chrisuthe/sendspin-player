using Microsoft.Extensions.DependencyInjection;

namespace Sendspin.Core.Platform;

/// <summary>
/// Platform-specific initialization and dependency injection registration.
/// Each target platform (Linux, Windows, macOS, iOS, Android) provides its own implementation.
/// </summary>
public interface IPlatformInitializer
{
    /// <summary>
    /// Registers platform-specific services with the DI container.
    /// Called during application startup before UI initialization.
    /// </summary>
    /// <param name="services">The service collection to register services with.</param>
    void RegisterServices(IServiceCollection services);

    /// <summary>
    /// Performs any platform-specific initialization before the application starts.
    /// </summary>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>A task representing the asynchronous initialization operation.</returns>
    Task InitializeAsync(CancellationToken ct = default);
}
