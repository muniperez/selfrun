namespace Minicloud.Common.Providers.Linode;

public sealed class LinodeOptions
{
    public const string SectionName = "Linode";

    public string ApiToken { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.linode.com/v4/";
}
