using System.Text.Json;
using PtkHook;

namespace PtkMcpServer.Tests;

public class RedirectHookTests
{
    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tool_input\":null}")]
    [InlineData("{\"tool_input\":{\"command\":42}}")]
    [InlineData("{\"tool_input\":{\"command\":\"  \"}}")]
    [InlineData("{\"tool_input\":{\"command\":\"echo ok # ptk_direct\"}}")]
    public void AllowsWithoutEvenProbingLiveness(string input)
    {
        Assert.Null(RedirectHook.Decide(input, () => throw new Exception("Must not probe")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeniesWithValidJsonAndEscapesUnicodeWorkingDirectory(bool running)
    {
        const string cwd = "C:\\Users\\O'Brien\\日本語\nrepo";
        var response = RedirectHook.Decide(JsonSerializer.Serialize(new
        {
            tool_input = new { command = "echo ignored" }, cwd
        }), () => running);
        using var json = JsonDocument.Parse(response!);
        var decision = json.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", decision.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", decision.GetProperty("permissionDecision").GetString());
        var reason = decision.GetProperty("permissionDecisionReason").GetString()!;
        Assert.Contains("Set-Location 'C:\\Users\\O''Brien\\日本語\nrepo';", reason);
        Assert.Equal(!running, reason.Contains("no ptk server process"));
    }
}
