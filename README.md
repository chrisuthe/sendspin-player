# Sendspin Linux Client

A Linux desktop application for synchronized multi-room audio playback using the Sendspin protocol. Built with Avalonia UI and PipeWire audio.

## Features

- **Synchronized multi-room audio**: Play audio in perfect sync with other Sendspin clients
- **Native Linux experience**: PipeWire audio, D-Bus notifications, XDG directory support
- **Low-latency audio**: Sub-millisecond sync accuracy via Kalman filter clock synchronization

## Requirements

- .NET 8.0 Runtime (or use self-contained build)
- PipeWire or PulseAudio audio server
- Linux x64

## Installation

### AppImage (Recommended)
```bash
chmod +x Sendspin-x86_64.AppImage
./Sendspin-x86_64.AppImage
```

### From Source
```bash
dotnet restore
dotnet build
dotnet run --project src/SendspinClient.Linux
```

## Architecture

This client uses the [Sendspin.SDK](https://www.nuget.org/packages/Sendspin.SDK) NuGet package for all protocol handling, clock synchronization, and audio pipeline orchestration. The SDK is shared with the Windows client.

```
src/
├── SendspinClient.Linux/           # Avalonia UI application
└── SendspinClient.Linux.Services/  # Linux platform services
    ├── Audio/                      # PipeWire audio player
    ├── Notifications/              # D-Bus notifications
    └── Discord/                    # Rich presence integration
```

## Related Projects

- [Sendspin Windows Client](https://github.com/your-org/windowsSpin) - Windows WPF client
- [Sendspin CLI](https://github.com/your-org/sendspin-cli) - Python CLI reference implementation
- [Music Assistant](https://music-assistant.io/) - The server this client connects to

## License

MIT License - see LICENSE file for details.
