using Sendspin.SDK.Discovery;

namespace SendspinClient.Linux.Services.Client;

/// <summary>
/// Server information obtained from discovery handshake.
/// Contains authoritative data from the server/hello message.
/// </summary>
public record ProbedServerInfo(
    string ServerId,
    string Name,
    string Host,
    int Port,
    IReadOnlyList<string> IpAddresses,
    string? ConnectionReason,
    DiscoveredServer OriginalServer
);
