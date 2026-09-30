namespace BabyMonitarr.Backend.Talkback;

/// <summary>Wire values of <c>unavailableReason</c> / <c>reason</c> in the talkback contract (docs/TALKBACK.md).</summary>
public static class TalkbackReasons
{
    public const string NotNest = "not_nest";
    public const string NotConfigured = "not_configured";
    public const string CredentialFailing = "credential_failing";
    public const string CameraNotMapped = "camera_not_mapped";
    public const string CameraError = "camera_error";
    public const string Busy = "busy";
    public const string Cancelled = "cancelled";
}

/// <summary>Talkback cannot proceed for a reason the app shows to the user.</summary>
public sealed class TalkbackUnavailableException(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}
