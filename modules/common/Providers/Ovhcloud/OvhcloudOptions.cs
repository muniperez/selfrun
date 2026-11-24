namespace Minicloud.Common.Providers.Ovhcloud;

public sealed class OvhcloudOptions
{
    public const string SectionName = "Ovhcloud";

    public string BaseUrl { get; set; } = "https://eu.api.ovh.com/1.0/";
    public string Credential { get; set; } = string.Empty;
    public bool EnableBareMetalOrders { get; set; }
}
