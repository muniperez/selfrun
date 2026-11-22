namespace Minicloud.Common.Providers.Scaleway;

public sealed class ScalewayOptions
{
    public const string SectionName = "Scaleway";

    public string ApiKey { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string BaseUrl { get; set; } = "https://api.scaleway.com/";
    public bool Enabled { get; set; }
}
