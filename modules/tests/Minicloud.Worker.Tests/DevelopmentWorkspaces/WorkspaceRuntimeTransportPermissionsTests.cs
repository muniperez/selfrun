using System.Runtime.CompilerServices;
using Minicloud.Worker.DevelopmentWorkspaces;
using Renci.SshNet.Sftp;

namespace Minicloud.Worker.Tests.DevelopmentWorkspaces;

public sealed class WorkspaceRuntimeTransportPermissionsTests
{
    [Theory]
    [InlineData(WorkspaceRuntimeTransport.PrivateDirectoryMode, "rwx------")]
    [InlineData(WorkspaceRuntimeTransport.PrivateFileMode, "rw-------")]
    [InlineData(WorkspaceRuntimeTransport.RepositoryCredentialDirectoryMode, "rwx--x--x")]
    [InlineData(WorkspaceRuntimeTransport.RepositoryCredentialFileMode, "r--------")]
    public void RuntimeModesAreAcceptedBySshNetAndGrantExactlyTheIntendedAccess(short mode, string expected)
    {
        var attributes = (SftpFileAttributes)RuntimeHelpers.GetUninitializedObject(typeof(SftpFileAttributes));

        attributes.SetPermissions(mode);

        var permissions = new[]
        {
            attributes.OwnerCanRead, attributes.OwnerCanWrite, attributes.OwnerCanExecute,
            attributes.GroupCanRead, attributes.GroupCanWrite, attributes.GroupCanExecute,
            attributes.OthersCanRead, attributes.OthersCanWrite, attributes.OthersCanExecute,
        };
        Assert.Equal(expected, new string(permissions.Select((granted, index) =>
            granted ? "rwx"[index % 3] : '-').ToArray()));
    }
}
