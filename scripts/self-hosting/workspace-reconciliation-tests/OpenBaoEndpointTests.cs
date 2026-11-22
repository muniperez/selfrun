using Minicloud.Common.Services;
using WorkspaceReconciliation;

public sealed class OpenBaoEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpsRemainsAvailableWithoutRedirects(bool allowLegacyHttp)
    {
        using var handler = OpenBaoEndpoint.CreateHandler(new() { Address = "https://openbao:8200/" }, allowLegacyHttp);
        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public void ExactLegacyOriginRequiresOptInAndDisablesProxyAndRedirects()
    {
        var options = new OpenBaoSecretStoreOptions { Address = "http://openbao:8200" };
        Assert.Throws<RecoveryRefused>(() => OpenBaoEndpoint.CreateHandler(options, allowLegacyHttp: false));
        using var handler = OpenBaoEndpoint.CreateHandler(options, allowLegacyHttp: true);
        var sockets = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(sockets.UseProxy);
        Assert.False(sockets.AllowAutoRedirect);
    }

    [Theory]
    [InlineData("")]
    [InlineData("openbao:8200")]
    [InlineData("file:///var/run/openbao.sock")]
    [InlineData("http://openbao")]
    [InlineData("http://openbao:8201")]
    [InlineData("http://127.0.0.1:8200")]
    [InlineData("http://openbao.example.test:8200")]
    [InlineData("http://openbao.:8200")]
    [InlineData("http://OPENBAO:8200")]
    [InlineData("http://openbao:08200")]
    [InlineData(" http://openbao:8200")]
    [InlineData("http://openbao:8200/")]
    [InlineData("http://openbao:8200/v1")]
    [InlineData("http://openbao:8200?debug=true")]
    [InlineData("http://openbao:8200#fragment")]
    [InlineData("http://operator@openbao:8200")]
    [InlineData("https://openbao:8200/v1")]
    [InlineData("https://openbao:8200?debug=true")]
    [InlineData("https://openbao:8200#fragment")]
    [InlineData("https://operator@openbao:8200")]
    public void OptInDoesNotPermitOtherInsecureOrNonOriginAddresses(string address)
    {
        foreach (var allowLegacyHttp in new[] { false, true })
            Assert.Throws<RecoveryRefused>(() => OpenBaoEndpoint.CreateHandler(new() { Address = address }, allowLegacyHttp));
    }
}
