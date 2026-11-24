namespace Minicloud.Common.Providers.GoogleCloud;

public sealed class GoogleCloudOptions
{
    public const string SectionName = "GoogleCloud";

    // A service-account JSON or workload-identity federation JSON document.
    // A short-lived OAuth access token is accepted only for synthetic tests.
    public string CredentialJson { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string BaseUrl { get; set; } = "https://compute.googleapis.com/compute/v1/";
}
