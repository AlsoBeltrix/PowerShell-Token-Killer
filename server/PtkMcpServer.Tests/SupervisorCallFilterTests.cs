using ModelContextProtocol.Protocol;

namespace PtkMcpServer.Tests;

/// <summary>
/// GitHub #34: PTK's tools return a string, which the SDK always wraps as a
/// successful call, so every refusal arrived with <c>isError=false</c>. A
/// client trusting that flag read "nothing was executed" as success; the real
/// outcome was legible only by parsing the bracketed in-band text.
/// </summary>
public sealed class SupervisorCallFilterTests
{
    [Theory]
    // Refusals: nothing ran.
    [InlineData("[ptk invoke] refused session=missing detail=session_not_found; Session 'missing' is not open. Nothing was executed.", true)]
    [InlineData("[ptk session] refused session=default detail=session_capacity_exceeded", true)]
    [InlineData("[ptk invoke] refused session=alpha detail=session_recovering; Session 'alpha' is not ready.", true)]
    [InlineData("[ptk output] invalid request: action=list accepts only session.", true)]
    [InlineData("[ptk output] invalid request: offset or maxBytes is outside the bounded contract.", true)]
    [InlineData("[operation not started] The operation was not started.", true)]
    // Ordinary results, including ones that merely mention failure: the work
    // ran, so the call succeeded whatever the script concluded.
    [InlineData("hello", false)]
    [InlineData("(no output)", false)]
    [InlineData("objects: 3 (PSCustomObject)", false)]
    [InlineData("[exit] 1", false)]
    [InlineData("[errors]\nsomething threw", false)]
    [InlineData("[ptk output] action=read state=available complete=true", false)]
    [InlineData("[ptk session] opened", false)]
    // A user's own output must never be mistaken for PTK's refusal marker.
    [InlineData("my script printed: [ptk invoke] refused session=x", false)]
    public void A_refusal_is_flagged_as_an_error_and_a_result_is_not(
        string text,
        bool expectedIsError)
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
        };

        var marked = SupervisorCallFilter.MarkRefusalAsErrorForTests(result);

        Assert.Equal(expectedIsError ? true : null, marked.IsError);
    }

    [Fact]
    public void An_already_flagged_error_is_left_alone()
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "hello" }],
        };

        Assert.True(SupervisorCallFilter.MarkRefusalAsErrorForTests(result).IsError);
    }

    [Fact]
    public void An_empty_result_is_not_flagged()
    {
        var result = new CallToolResult { Content = [] };

        Assert.Null(SupervisorCallFilter.MarkRefusalAsErrorForTests(result).IsError);
    }
}
