using System.Management.Automation;
using System.Management.Automation.Language;
using System.Text.RegularExpressions;

namespace PtkMcpServer;

/// <summary>A command-resolution fact captured by trusted host code before
/// preflight. The classifier consumes only these plain values and never calls
/// back into the user runspace.</summary>
internal sealed record ResolvedCommand(
    CommandTypes CommandType,
    string? Source = null,
    string? Definition = null,
    bool IsCanonicalManagementSetContent = false,
    bool ResolutionUncertain = false);


/// <summary>Case-insensitive, data-only command facts for one preflight. A
/// missing fact is an authoritative miss; classification never performs a
/// discovery lookup of its own.</summary>
internal sealed class TrustedCommandSnapshot
{
    private readonly Dictionary<string, Dictionary<CommandTypes, ResolvedCommand?>> _commands =
        new(StringComparer.OrdinalIgnoreCase);

    internal void Set(string name, CommandTypes requestedTypes, ResolvedCommand? command)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_commands.TryGetValue(name, out var byType))
        {
            byType = [];
            _commands.Add(name, byType);
        }
        byType[requestedTypes] = command;
    }

    internal ResolvedCommand? Resolve(string name, CommandTypes requestedTypes)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _commands.TryGetValue(name, out var byType) &&
               byType.TryGetValue(requestedTypes, out var command)
            ? command
            : null;
    }

    internal TrustedCommandSnapshot Clone()
    {
        var clone = new TrustedCommandSnapshot();
        foreach (var (name, byType) in _commands)
        {
            foreach (var (requestedTypes, command) in byType)
                clone.Set(name, requestedTypes, command);
        }
        return clone;
    }
}

/// <summary>Trusted, side-effect-free equivalents of the PowerShell module's
/// routing and dialect preflight functions. Parsing is local CLR work and all
/// command-resolution inputs arrive through <see cref="TrustedCommandSnapshot"/>.</summary>
internal static class TrustedPreflightClassifier
{
    private sealed record LocalDefinition(string Name, int Start, int End);

    internal static string[] GetRequiredCommandNames(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var ast = Parser.ParseInput(script, out _, out _);
        return
        [
            .. ast.FindAll(node => node is CommandAst, searchNestedScriptBlocks: true)
                .Cast<CommandAst>()
                .Select(command => command.CommandElements.FirstOrDefault())
                .OfType<StringConstantExpressionAst>()
                .Select(element => element.Value)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }
}
