namespace Selfrun.Common.Providers.Linode;

public sealed class LinodeOptions
{
    public const string SectionName = "Linode";

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.linode.com/v4/";
    public string ApiToken { get; set; } = string.Empty;
    public string DefaultRegion { get; set; } = "us-east";
    public string DefaultImage { get; set; } = "linode/ubuntu24.04";
}
