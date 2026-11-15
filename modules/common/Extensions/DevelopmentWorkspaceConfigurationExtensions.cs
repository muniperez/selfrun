using Microsoft.Extensions.Configuration;

namespace Minicloud.Common.Extensions;

public static class DevelopmentWorkspaceConfigurationExtensions
{
    public static IConfigurationBuilder AddDevelopmentWorkspaceConfiguration(this IConfigurationBuilder configuration)
    {
        var path = Environment.GetEnvironmentVariable("DevelopmentWorkspaces__ConfigurationFile");
        if (string.IsNullOrWhiteSpace(path))
            return configuration;

        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("DevelopmentWorkspaces__ConfigurationFile must be an absolute file path.");

        return configuration.AddJsonFile(path, optional: false, reloadOnChange: false)
            .AddEnvironmentVariables();
    }
}
