using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using Google.Protobuf;

namespace BabyMonitarr.Backend.Talkback;

public sealed class FoyerException(string message, string? grpcStatus) : Exception(message)
{
    public string? GrpcStatus { get; } = grpcStatus;

    /// <summary>gRPC UNAUTHENTICATED: the access token is no longer accepted.</summary>
    public bool IsUnauthenticated => GrpcStatus == "16";
}

/// <summary>
/// Unary gRPC over HTTP/2 against Google Home's private Foyer host, framed by hand so every
/// header is under our control (grpc-dotnet would replace the user-agent).
/// </summary>
public sealed class FoyerClient(HttpClient http, string accessToken)
{
    private const string Host = "https://googlehomefoyer-pa.googleapis.com";
    private const string Prefix = "google.internal.home.foyer.v1.";

    /// <summary>The Google Home / Nest iOS app identity, as used by homebridge-nest-accfactory.</summary>
    public const string UserAgent = "Nest/5.87.0 (iOScom.nestlabs.jasper.release) os=26.4";

    public async Task<TResponse> CallAsync<TResponse>(
        string service, string method, IMessage request, MessageParser<TResponse> parser,
        CancellationToken ct = default)
        where TResponse : IMessage<TResponse>
    {
        byte[] payload = request.ToByteArray();
        var framed = new byte[5 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(framed, 5);

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Host}/{Prefix}{service}/{method}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(framed),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        message.Headers.TE.Add(new TransferCodingWithQualityHeaderValue("trailers"));
        message.Headers.TryAddWithoutValidation("user-agent", UserAgent);
        message.Headers.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
        message.Headers.TryAddWithoutValidation("grpc-timeout", "15S");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(message, ct);
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);

        // A failing call can come back "trailers-only", with grpc-status in the headers.
        string? status = Header(response.TrailingHeaders, "grpc-status") ?? Header(response.Headers, "grpc-status");
        string? grpcMessage = Header(response.TrailingHeaders, "grpc-message") ?? Header(response.Headers, "grpc-message");

        if (response.StatusCode != HttpStatusCode.OK || status != "0")
        {
            throw new FoyerException(
                $"{service}/{method} failed: http={(int)response.StatusCode} grpc-status={status ?? "?"} " +
                $"grpc-message={(grpcMessage is null ? "" : Uri.UnescapeDataString(grpcMessage))}",
                status);
        }

        if (body.Length < 5)
        {
            return parser.ParseFrom(ByteString.Empty);
        }

        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1));
        return parser.ParseFrom(body, 5, length);
    }

    private static string? Header(HttpHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
