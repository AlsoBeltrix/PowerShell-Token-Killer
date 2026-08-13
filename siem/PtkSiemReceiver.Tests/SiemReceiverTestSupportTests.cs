using Microsoft.Data.Sqlite;

namespace PtkSiemReceiver.Tests;

[Collection(SiemReceiverProcessCollection.Name)]
public sealed class SiemReceiverTestSupportTests
{
    [Fact]
    public async Task Failed_start_clears_sqlite_pools_before_owned_root_deletion()
    {
        using var authority = new TestCertificateAuthority();
        using var root = authority.Root;
        using var server = authority.IssueServer();
        var expected = new InvalidOperationException("injected start failure");
        var cleanup = new List<string>();

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SiemReceiverTestHost.StartAsync(
                server,
                [root],
                failureHooks: new SiemReceiverTestHostFailureHooks(
                    _ => Task.FromException(expected),
                    () =>
                    {
                        SqliteConnection.ClearAllPools();
                        cleanup.Add("clear");
                    },
                    (path, recursive) =>
                    {
                        cleanup.Add("delete");
                        Directory.Delete(path, recursive);
                    })));

        Assert.Same(expected, observed);
        Assert.Equal(["clear", "delete", "delete"], cleanup);
    }
}
