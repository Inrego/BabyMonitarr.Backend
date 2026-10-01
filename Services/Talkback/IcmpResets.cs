using SIPSorcery.Net;

namespace BabyMonitarr.Backend.Talkback;

/// <summary>
/// Windows reports an ICMP "port unreachable" as ConnectionReset on a UDP socket's next receive,
/// and SIPSorcery 10.0.3 then stops receiving on that socket for good, so a single ICE check to an
/// unreachable candidate (the app trickles its own 127.0.0.1 and ::1) leaves a peer connection
/// deaf. Linux does not report ICMP errors on unconnected UDP sockets.
/// </summary>
internal static class IcmpResets
{
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>Turns the report off for the peer connection's RTP socket; throws if the socket refuses.</summary>
    public static void Ignore(RTCPeerConnection pc)
    {
        if (OperatingSystem.IsWindows())
        {
            pc.GetRtpChannel().RtpSocket.IOControl(SioUdpConnReset, new byte[4], null);
        }
    }
}
