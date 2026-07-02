using PtkMcpServer.Tools;

namespace PtkMcpServer.Tests;

public class PingToolTests
{
    [Fact]
    public void Ping_ReturnsPong()
    {
        Assert.Equal("pong", PingTool.Ping());
    }
}
