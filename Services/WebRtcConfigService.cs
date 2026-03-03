using System.Net;
using BabyMonitarr.Backend.Models;
using Microsoft.Extensions.Options;
using SIPSorcery.Net;
using SIPSorcery.Sys;

namespace BabyMonitarr.Backend.Services;

public interface IWebRtcConfigService
{
    WebRtcPeerConnectionSettings GetPeerConnectionSettings(string? hostHint);
    WebRtcClientConfig GetClientConfig();
    RTCIceCandidate? CreateAdvertisedCandidate(RTCIceCandidate candidate, IPAddress? advertisedAddress);
}

public sealed class WebRtcPeerConnectionSettings
{
    public RTCConfiguration Configuration { get; init; } = new();
    public int BindPort { get; init; }
    public PortRange? PortRange { get; init; }
    public IPAddress? AdvertisedAddress { get; init; }
}

public sealed class WebRtcConfigService : IWebRtcConfigService
{
    private const string DefaultStunServer = "stun:stun.l.google.com:19302";

    private readonly ILogger<WebRtcConfigService> _logger;
    private readonly WebRtcOptions _options;
    private readonly List<RTCIceServer> _iceServers;
    private readonly PortRange? _sharedPortRange;

    public WebRtcConfigService(
        ILogger<WebRtcConfigService> logger,
        IOptions<WebRtcOptions> options)
    {
        _logger = logger;
        _options = options.Value ?? new WebRtcOptions();
        _iceServers = BuildIceServers(_options.IceServers);
        _sharedPortRange = BuildPortRange(_options.RtpPortRange);
    }

    public WebRtcPeerConnectionSettings GetPeerConnectionSettings(string? hostHint)
    {
        var config = new RTCConfiguration
        {
            iceServers = _iceServers.Select(CloneIceServer).ToList(),
            bundlePolicy = RTCBundlePolicy.max_bundle,
            rtcpMuxPolicy = RTCRtcpMuxPolicy.require,
            X_ICEIncludeAllInterfaceAddresses = _options.IncludeAllInterfaceAddresses
        };

        if (_options.GatherTimeoutMs > 0)
        {
            config.X_GatherTimeoutMs = _options.GatherTimeoutMs;
        }

        if (TryResolveAddress(_options.BindAddress, "BindAddress", out var bindAddress))
        {
            config.X_BindAddress = bindAddress;
        }

        var advertisedAddress = ResolveAdvertisedAddress(hostHint);
        var bindPort = _options.BindPort > 0 ? _options.BindPort : 0;

        return new WebRtcPeerConnectionSettings
        {
            Configuration = config,
            BindPort = bindPort,
            PortRange = _sharedPortRange,
            AdvertisedAddress = advertisedAddress
        };
    }

    public WebRtcClientConfig GetClientConfig()
    {
        return new WebRtcClientConfig
        {
            IceServers = _iceServers.Select(ice => new WebRtcClientIceServer
            {
                Urls = ice.urls,
                Username = string.IsNullOrWhiteSpace(ice.username) ? null : ice.username,
                Credential = string.IsNullOrWhiteSpace(ice.credential) ? null : ice.credential
            }).ToList()
        };
    }

    public RTCIceCandidate? CreateAdvertisedCandidate(RTCIceCandidate candidate, IPAddress? advertisedAddress)
    {
        if (advertisedAddress == null)
        {
            return null;
        }

        if (candidate.type != RTCIceCandidateType.host || candidate.protocol != RTCIceProtocol.udp)
        {
            return null;
        }

        if (!IPAddress.TryParse(candidate.address, out var sourceAddress))
        {
            return null;
        }

        if (!IsPrivateOrLocalAddress(sourceAddress))
        {
            return null;
        }

        if (sourceAddress.Equals(advertisedAddress))
        {
            return null;
        }

        return new RTCIceCandidate(candidate.protocol, advertisedAddress, candidate.port, candidate.type)
        {
            sdpMid = candidate.sdpMid,
            sdpMLineIndex = candidate.sdpMLineIndex
        };
    }

    private IPAddress? ResolveAdvertisedAddress(string? hostHint)
    {
        if (TryResolveAddress(_options.AdvertisedAddress, "AdvertisedAddress", out var configuredAddress))
        {
            return configuredAddress;
        }

        if (_options.InferAdvertisedAddressFromForwardedHost &&
            TryResolveAddress(hostHint, "ForwardedHost", out var inferredAddress))
        {
            return inferredAddress;
        }

        return null;
    }

    private bool TryResolveAddress(string? value, string sourceName, out IPAddress address)
    {
        address = IPAddress.None;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();

        if (IPAddress.TryParse(normalized, out var parsedAddress))
        {
            address = parsedAddress;
            return true;
        }

        try
        {
            var resolvedAddresses = Dns.GetHostAddresses(normalized);
            var selectedAddress = resolvedAddresses.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                 ?? resolvedAddresses.FirstOrDefault();

            if (selectedAddress != null)
            {
                _logger.LogInformation("Resolved WebRTC {SourceName} host '{Host}' to {Address}",
                    sourceName, normalized, selectedAddress);
                address = selectedAddress;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve WebRTC {SourceName} host '{Host}'", sourceName, normalized);
        }

        return false;
    }

    private List<RTCIceServer> BuildIceServers(List<WebRtcIceServerOptions>? configuredServers)
    {
        var servers = new List<RTCIceServer>();

        if (configuredServers != null)
        {
            foreach (var configuredServer in configuredServers)
            {
                if (string.IsNullOrWhiteSpace(configuredServer.Urls))
                {
                    continue;
                }

                var server = new RTCIceServer
                {
                    urls = configuredServer.Urls.Trim()
                };

                if (!string.IsNullOrWhiteSpace(configuredServer.Username))
                {
                    server.username = configuredServer.Username.Trim();
                }

                if (!string.IsNullOrWhiteSpace(configuredServer.Credential))
                {
                    server.credential = configuredServer.Credential.Trim();
                }

                servers.Add(server);
            }
        }

        if (servers.Count == 0)
        {
            servers.Add(new RTCIceServer { urls = DefaultStunServer });
        }

        return servers;
    }

    private PortRange? BuildPortRange(WebRtcRtpPortRangeOptions? options)
    {
        if (options == null)
        {
            return null;
        }

        if (options.Start <= 0 || options.End <= 0 || options.End <= options.Start)
        {
            _logger.LogWarning("Ignoring invalid WebRTC RTP port range: start={Start}, end={End}",
                options.Start, options.End);
            return null;
        }

        if (options.Start % 2 != 0)
        {
            _logger.LogWarning("Ignoring invalid WebRTC RTP port range start (must be even): {Start}", options.Start);
            return null;
        }

        try
        {
            return new PortRange(options.Start, options.End, options.Shuffle, options.RandomSeed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring invalid WebRTC RTP port range options.");
            return null;
        }
    }

    private static RTCIceServer CloneIceServer(RTCIceServer source)
    {
        return new RTCIceServer
        {
            urls = source.urls,
            username = source.username,
            credential = source.credential,
            credentialType = source.credentialType
        };
    }

    private static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 10)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }
        }

        return false;
    }
}
