using BabyMonitarr.Backend.Services;
using BabyMonitarr.Backend.Talkback;
using BabyMonitarr.Backend.Talkback.Foyer;

namespace BabyMonitarr.Backend.Tests.Talkback;

public class NestSdpTests
{
    [Fact]
    public void SplitCandidates_strips_candidates_and_keeps_their_media_section()
    {
        string sdp = string.Join("\r\n",
            "v=0",
            "m=audio 19305 UDP/TLS/RTP/SAVPF 111",
            "a=mid:0",
            "a=candidate: 1 udp 2113939711 74.125.247.232 19305 typ host generation 0",
            "m=video 9 UDP/TLS/RTP/SAVPF 96",
            "a=mid:1",
            "a=candidate:2 1 ssltcp 1000 74.125.247.232 443 typ host",
            "");

        var (cleaned, candidates) = NestSdp.SplitCandidates(sdp);

        Assert.DoesNotContain("a=candidate", cleaned);
        Assert.Contains("m=video", cleaned);
        Assert.Equal(2, candidates.Count);
        Assert.Equal(("0", (ushort)0), (candidates[0].Mid, candidates[0].MLineIndex));
        Assert.Equal(("1", (ushort)1), (candidates[1].Mid, candidates[1].MLineIndex));
    }

    [Fact]
    public void Normalize_synthesizes_a_missing_foundation()
    {
        Assert.True(NestSdp.TryNormalizeIceCandidate(
            "candidate: 1 UDP 2113939711 74.125.247.232 19305 typ HOST generation 0", out var normalized, out _));

        Assert.Equal("candidate:nest2113939711 1 udp 2113939711 74.125.247.232 19305 typ host generation 0", normalized);
    }

    [Fact]
    public void Normalize_maps_ssltcp_to_tcp()
    {
        Assert.True(NestSdp.TryNormalizeIceCandidate(
            "a=candidate:2 1 ssltcp 1000 74.125.247.232 443 typ host tcptype passive", out var normalized, out _));

        Assert.Equal("candidate:2 1 tcp 1000 74.125.247.232 443 typ host tcpType passive", normalized);
    }

    [Fact]
    public void Normalize_rejects_garbage()
    {
        Assert.False(NestSdp.TryNormalizeIceCandidate("candidate:x y z", out _, out var reason));
        Assert.NotEmpty(reason);
    }
}

public class TalkbackCameraMatcherTests
{
    private static Device DeviceWith(string uuid, params (string Type, string Id)[] ids)
    {
        var device = new Device { Id = new Device.Types.Id { GoogleUuid = uuid } };
        if (ids.Length > 0)
        {
            device.OtherIds = new Device.Types.OtherThirdPartyIds();
            foreach (var (type, id) in ids)
            {
                device.OtherIds.OtherThirdPartyId.Add(new Device.Types.ThirdPartyId { IdType = type, Id = id });
            }
        }
        return device;
    }

    private static GetHomeGraphResponse Graph()
    {
        var home = new Home { Name = "Home" };
        home.Devices.Add(DeviceWith("cam-1",
            ("nest-home-assistant-prod", "DEVICE_AAA"), ("PHX", "DEVICE_AAA"), ("CAMERA", "DEVICE_AAA")));
        home.Devices.Add(DeviceWith("light-1", ("home-assistant-325914", "light.kids")));
        home.Devices.Add(DeviceWith("cam-2", ("CAMERA", "DEVICE_BBB")));

        var kids = new Room { Name = "Børneværelse" };
        kids.Devices.Add(DeviceWith("cam-1"));
        kids.Devices.Add(DeviceWith("light-1"));
        var hall = new Room { Name = "Entré" };
        hall.Devices.Add(DeviceWith("cam-2"));
        home.Rooms.Add(kids);
        home.Rooms.Add(hall);

        var graph = new GetHomeGraphResponse();
        graph.Homes.Add(home);
        return graph;
    }

    [Fact]
    public void ParseCameras_keeps_only_devices_with_a_nest_camera_id()
    {
        var cameras = TalkbackCameraMatcher.ParseCameras(Graph());

        Assert.Equal(new[]
        {
            new TalkbackCamera("DEVICE_AAA", "cam-1", "Børneværelse"),
            new TalkbackCamera("DEVICE_BBB", "cam-2", "Entré"),
        }, cameras);
    }

    [Fact]
    public void Match_prefers_the_camera_in_the_room_of_the_same_name()
    {
        var cameras = TalkbackCameraMatcher.ParseCameras(Graph());

        Assert.Equal("DEVICE_AAA", TalkbackCameraMatcher.Match(" børneværelse ", cameras)?.NestDeviceId);
    }

    [Fact]
    public void Match_is_ambiguous_without_a_room_match_and_several_cameras()
    {
        var cameras = TalkbackCameraMatcher.ParseCameras(Graph());

        Assert.Null(TalkbackCameraMatcher.Match("Stue", cameras));
        Assert.Null(TalkbackCameraMatcher.Match(null, cameras));
    }

    [Fact]
    public void Match_falls_back_to_the_only_camera()
    {
        var only = new[] { new TalkbackCamera("DEVICE_AAA", "cam-1", "Ajas værelse") };

        Assert.Equal(only[0], TalkbackCameraMatcher.Match("Børneværelse", only));
    }

    [Fact]
    public void Match_refuses_two_cameras_in_the_same_room()
    {
        var cameras = new[]
        {
            new TalkbackCamera("DEVICE_AAA", "cam-1", "Stue"),
            new TalkbackCamera("DEVICE_BBB", "cam-2", "Stue"),
        };

        Assert.Null(TalkbackCameraMatcher.Match("Stue", cameras));
    }
}
