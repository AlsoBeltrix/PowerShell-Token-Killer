using System.Text;
using System.Text.Json;

namespace PtkHook;

public static class RedirectHook
{
    public static string? Decide(string input, Func<bool> serverIsRunning)
    {
        string? command;
        string? cwd;
        try
        {
            using var payload = JsonDocument.Parse(input);
            var root = payload.RootElement;
            command = ReadString(root.GetProperty("tool_input"), "command");
            cwd = ReadString(root, "cwd");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(command) ||
            command.Contains("PTK_DIRECT", StringComparison.OrdinalIgnoreCase))
            return null;

        var cwdAdvice = string.IsNullOrEmpty(cwd) ? " " :
            " The warm runspace keeps its current directory across calls; if needed, prefix: Set-Location '" +
            cwd.Replace("'", "''") + "'; ";
        var reason =
            "Shell commands run through ptk: call the ptk_invoke MCP tool with \"script\" set to this command written as PowerShell." +
            cwdAdvice +
            "It runs in a persistent warm PowerShell runspace (state and imported modules survive across calls) " +
            "and output comes back token-compressed. The dialect is PowerShell 7, not bash: write PowerShell directly. " +
            "git, grep, sed, python and other native tools run as ordinary commands; translate bash-only syntax " +
            "(heredocs, export, [ ] tests) instead of wrapping the command in bash -lc. Reserve bash for a script " +
            "that is itself bash, run from a file or passed as a multi-line here-string, bash -lc @'...'@ where bash exists " +
            "(@' ends its line, '@ opens one): a quoted '...' argument is a PowerShell string and breaks at the first " +
            "embedded apostrophe. " +
            "Only if the command genuinely needs this harness shell " +
            "(interactive/TTY, or ptk is unavailable), re-run it here with PTK_DIRECT in a comment." +
            (serverIsRunning() ? "" :
                " NOTE: no ptk server process is running on this machine right now - if the " +
                "ptk tools are not available in this session, re-run this command here with PTK_DIRECT now.");

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartObject("hookSpecificOutput");
            json.WriteString("hookEventName", "PreToolUse");
            json.WriteString("permissionDecision", "deny");
            json.WriteString("permissionDecisionReason", reason);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
