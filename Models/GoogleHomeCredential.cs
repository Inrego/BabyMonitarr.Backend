namespace BabyMonitarr.Backend.Models;

/// <summary>
/// The browser-captured Google Home session used for talkback (Foyer API). Both values are
/// secrets: never returned to clients and never logged.
/// </summary>
public class GoogleHomeCredential
{
    public int Id { get; set; }

    /// <summary>The captured <c>accounts.google.com/o/oauth2/iframerpc?action=issueToken…</c> URL.</summary>
    public string IssueTokenUrl { get; set; } = string.Empty;

    /// <summary>The Cookie request header of that request; rotated values are merged back after each exchange.</summary>
    public string Cookie { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSuccessAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAtUtc { get; set; }
}
