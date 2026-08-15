using System.Text.Json;

namespace Tap.Studio.Cli;

/// <summary>
/// Collects the run's input variables from <c>--var</c> and <c>--var-file</c>.
///
/// <para>These land in the run's override tier — above every file scope — which is the same
/// slot the Studio's per-run overrides use. That is what makes
/// <c>--var customer=cus_ci</c> mean "for this run, the customer is cus_ci" regardless of what
/// the environment or the request declares.</para>
///
/// <para>Precedence: later files beat earlier ones, and <c>--var</c> beats every file. The rule
/// is "the more specific and the more recent wins", which is what someone assembling a command
/// line expects without being told.</para>
/// </summary>
public static class VariableInputs
{
    /// <summary>
    /// Merges files then inline assignments. Returns false with a reason when something can't
    /// be read or parsed — a mistyped variable file must not silently produce an empty set and
    /// a run against the wrong data.
    /// </summary>
    public static bool TryCollect(
        IReadOnlyList<string>? files,
        IReadOnlyList<string>? assignments,
        out Dictionary<string, string> variables,
        out string error)
    {
        variables = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;

        foreach (var file in files ?? [])
        {
            if (!TryReadFile(file, variables, out error)) return false;
        }

        foreach (var assignment in assignments ?? [])
        {
            var cut = assignment.IndexOf('=');
            if (cut <= 0)
            {
                error = $"--var '{assignment}' is not a name=value pair.";
                return false;
            }
            var name = assignment[..cut].Trim();
            if (name.Length == 0)
            {
                error = $"--var '{assignment}' has an empty name.";
                return false;
            }
            variables[name] = assignment[(cut + 1)..];
        }

        return true;
    }

    private static bool TryReadFile(string path, Dictionary<string, string> into, out string error)
    {
        error = string.Empty;
        if (!File.Exists(path))
        {
            error = $"--var-file '{path}' does not exist.";
            return false;
        }

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"--var-file '{path}' could not be read: {ex.Message}";
            return false;
        }

        return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? TryReadJson(path, text, into, out error)
            : TryReadDotEnv(path, text, into, out error);
    }

    /// <summary>A flat JSON object. Scalars are stringified — a variable is always a string by
    /// the time it reaches a template, so accepting a number here and converting is friendlier
    /// than making the author quote it.</summary>
    private static bool TryReadJson(string path, string text, Dictionary<string, string> into, out string error)
    {
        error = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"--var-file '{path}' must contain a JSON object of name/value pairs.";
                return false;
            }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value;
                if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    error = $"--var-file '{path}': '{property.Name}' is a {value.ValueKind.ToString().ToLowerInvariant()}; "
                          + "variables are flat name/value pairs.";
                    return false;
                }
                into[property.Name] = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Null => string.Empty,
                    _ => value.GetRawText(),
                };
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"--var-file '{path}' is not valid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary><c>.env</c> shape: <c>NAME=value</c> per line, <c>#</c> comments, optional
    /// surrounding quotes, and a tolerated <c>export</c> prefix so a file that also works with
    /// <c>source</c> can be reused as-is.</summary>
    private static bool TryReadDotEnv(string path, string text, Dictionary<string, string> into, out string error)
    {
        error = string.Empty;
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var cut = line.IndexOf('=');
            if (cut <= 0)
            {
                error = $"--var-file '{path}' line {lineNumber}: expected NAME=value.";
                return false;
            }

            var name = line[..cut].Trim();
            var value = line[(cut + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];

            into[name] = value;
        }
        return true;
    }
}
