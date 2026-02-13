namespace BabyMonitarr.Backend.Models;

public class GoogleNestSettings
{
    public int Id { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? ProjectId { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? TokenExpiresAt { get; set; }
    public bool IsLinked { get; set; }
}
