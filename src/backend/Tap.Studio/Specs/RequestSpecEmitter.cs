using System.Text;
using Tap.Studio.Contracts;
using YamlDotNet.RepresentationModel;

namespace Tap.Studio.Specs;

/// <summary>
/// Emits canonical YAML + a fenced <c>```http</c> block for a request. The HTTP block is
/// assembled deterministically: method &amp; URL line, each header on its own line, blank
/// line, body.
///
/// On-disk layout:
/// <code>
/// ---
/// kind: request
/// name: …
/// ---
///
/// ```http
/// GET /users/{{userId}}
/// Accept: application/json
/// ```
///
/// markdown documentation goes here
/// </code>
/// </summary>
public static class RequestSpecEmitter
{
    public static string ToFileSource(RequestSpecDto spec)
    {
        var fm = new YamlMappingNode();
        fm.Set("kind", "request");
        fm.SetIfNotEmpty("id", spec.Id);
        fm.Set("name", spec.Name);
        fm.SetIfNotEmpty("auth", spec.Auth);
        // Default protocol (http) is the absence of the field — keeps existing files
        // diff-clean and only writes the marker when it actually flips behavior.
        if (!string.IsNullOrEmpty(spec.Protocol) && !spec.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            fm.Set("protocol", spec.Protocol.ToLowerInvariant());
        }
        fm.SetTransport(spec.Transport);
        fm.SetVarMap("vars", spec.Vars, spec.Secrets);
        // Mapped through the model so a client can't save an assertion the parser would
        // reject — the PUT fails with E_ASSERT_INVALID instead of writing an unloadable file.
        fm.SetAssertions(AssertSpecMapper.ToModel(spec.Assertions, spec.Path));
        fm.SetStringList("tags", spec.Tags);

        var http = BuildHttpBlock(spec);
        return SpecYaml.ToFrontmatter(fm, body: spec.Body, httpBlock: http);
    }

    private static string BuildHttpBlock(RequestSpecDto spec)
    {
        var sb = new StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(spec.Method) ? "GET" : spec.Method.Trim().ToUpperInvariant());
        sb.Append(' ');
        sb.Append(spec.Url ?? string.Empty);
        sb.Append('\n');

        if (spec.Headers is { Count: > 0 })
        {
            foreach (var h in spec.Headers)
            {
                if (string.IsNullOrEmpty(h.Name)) continue;
                sb.Append(h.Name).Append(": ").Append(h.Value ?? string.Empty).Append('\n');
            }
        }

        if (!string.IsNullOrEmpty(spec.RequestBody))
        {
            sb.Append('\n');
            sb.Append(spec.RequestBody);
            if (!spec.RequestBody.EndsWith('\n')) sb.Append('\n');
        }

        return sb.ToString();
    }
}
