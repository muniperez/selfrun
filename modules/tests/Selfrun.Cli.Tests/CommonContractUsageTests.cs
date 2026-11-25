// SPDX-License-Identifier: MPL-2.0

using Selfrun.Cli.Api;

namespace Selfrun.Tests;

public sealed class CommonContractUsageTests
{
    [Fact]
    public void CliApiNamespaceContainsNoLocalWireContracts()
    {
        var allowedTypes = new HashSet<Type>
        {
            typeof(SelfrunApiClient),
            typeof(ApiException)
        };

        var localWireTypes = typeof(SelfrunApiClient).Assembly.ExportedTypes
            .Where(type => type.Namespace == typeof(SelfrunApiClient).Namespace)
            .Where(type => !allowedTypes.Contains(type))
            .Select(type => type.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(localWireTypes);
    }

    [Fact]
    public void ApiClientPublicSurfaceUsesNoCliLocalContractTypes()
    {
        var cliAssembly = typeof(SelfrunApiClient).Assembly;
        var localContractTypes = typeof(SelfrunApiClient).GetMethods()
            .Where(method => method.DeclaringType == typeof(SelfrunApiClient))
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .SelectMany(LeafTypes)
            .Where(type => type.Assembly == cliAssembly)
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(localContractTypes);
    }

    private static IEnumerable<Type> LeafTypes(Type type)
    {
        if (!type.IsGenericType)
        {
            yield return type;
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var leaf in LeafTypes(argument))
            {
                yield return leaf;
            }
        }
    }
}
