using Tap.Execution.Agent;
using Tap.Workspace.Rendering;

namespace Tap.Studio.Cli.Output;

/// <summary>
/// Machine output for the <c>--json</c> modes: <see cref="AgentJson"/>'s document — the same
/// one the MCP tools return — written to the real stdout, not through Spectre. Spectre wraps
/// text at the console width, and a hard line break in the middle of a JSON string is a
/// broken document.
/// </summary>
public static class JsonOutput
{
    public static void Write<T>(T payload, SecretRedactor? redactor = null)
        => Console.Out.WriteLine(AgentJson.Serialize(payload, redactor));
}
