using Tap.Studio.Contracts;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Parsing;

namespace Tap.Tests.Format;

/// <summary>
/// The Markdown body of a workspace file is user-authored documentation, and the Studio
/// rewrites the whole file from the parsed model on every save. So for every kind that can
/// carry a body, parse → emit → parse has to leave the prose intact: otherwise an unrelated
/// edit — renaming a variable, adding a tag — silently deletes it.
///
/// <para>Regression cover for env / auth / workspace, whose parsers used to skip the body
/// entirely, so the first save from the Studio truncated the file to its frontmatter.</para>
/// </summary>
public class SpecBodyRoundTripTests
{
    private const string Doc = "# Local\n\nDefault env for the demo. Plain values only — no secret refs.";

    public static TheoryData<string, string, string> Files => new()
    {
        {
            "request", "collections/orders/get-order.req.tap",
            """
            ---
            kind: request
            name: Get order
            ---

            ```http
            GET /orders/{{orderId}}
            Accept: application/json
            ```

            """ + Doc + "\n"
        },
        {
            "collection", "collections/orders/_collection.tap",
            """
            ---
            kind: collection
            name: Orders
            baseUrl: https://api.example.com
            ---

            """ + Doc + "\n"
        },
        {
            "env", "environments/local.env.tap",
            """
            ---
            kind: env
            name: Local
            vars:
              user.name: Jane Doe
            ---

            """ + Doc + "\n"
        },
        {
            "auth", "auth/bearer.auth.tap",
            """
            ---
            kind: auth
            name: Bearer
            type: bearer
            token: '{{apiToken}}'
            ---

            """ + Doc + "\n"
        },
        {
            "flow", "tests/checkout.flow.tap",
            """
            ---
            kind: flow
            name: Checkout
            steps:
            - request: ./a.req.tap
            ---

            """ + Doc + "\n"
        },
        {
            "test", "tests/orders.test.tap",
            """
            ---
            kind: test
            name: Orders
            tests:
            - request: ./a.req.tap
            ---

            """ + Doc + "\n"
        },
        {
            "workspace", "workspace.tap",
            """
            ---
            kind: workspace
            name: Demo
            ---

            """ + Doc + "\n"
        },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void Body_survives_parse_emit_parse(string kind, string path, string source)
    {
        _ = kind; // names the case in the test report

        var first = FileParser.Parse(path, source);
        Assert.Equal(Doc, DocBodyOf(first));

        var emitted = Emit(first);
        Assert.Contains("Default env for the demo", emitted, StringComparison.Ordinal);

        var second = FileParser.Parse(path, emitted);
        Assert.Equal(Doc, DocBodyOf(second));

        // …and re-saving an untouched file is a no-op, so the body can't drift a line at a time.
        Assert.Equal(emitted, Emit(second));
    }

    [Fact]
    public void Editing_an_env_variable_keeps_the_documentation()
    {
        var env = (EnvFile)FileParser.Parse(
            "environments/local.env.tap",
            "---\nkind: env\nname: Local\nvars:\n  user.name: Jane Doe\n---\n\n" + Doc + "\n");

        var emitted = EnvSpecEmitter.ToFileSource(new EnvSpecDto
        {
            Path = env.RelativePath,
            Name = env.Name!,
            Vars = new Dictionary<string, string> { ["user.name"] = "John Doe" },
            Body = env.Body,
        });

        Assert.Contains("user.name: John Doe", emitted, StringComparison.Ordinal);
        Assert.Equal(Doc, ((EnvFile)FileParser.Parse(env.RelativePath, emitted)).Body.Trim());
    }

    /// <summary>The documentation part of the body. A request's body also holds its fenced
    /// <c>http</c> block, which the emitter rebuilds from the parsed fields — the Studio
    /// strips it the same way before handing the body to the editor.</summary>
    private static string DocBodyOf(WorkspaceFile file) => file is RequestFile request
        ? RequestSpecProjection.StripHttpFence(request.Body)
        : file.Body.Trim();

    /// <summary>Round-trips a parsed file through the real emitter for its kind — the same
    /// one the <c>PUT /api/{kind}/spec</c> endpoints use.</summary>
    private static string Emit(WorkspaceFile file) => file switch
    {
        RequestFile r => RequestSpecEmitter.ToFileSource(RequestSpecProjection.ToSpec(r)),

        CollectionFile c => CollectionSpecEmitter.ToFileSource(new CollectionSpecDto
        {
            Slug = "orders",
            Id = c.Id,
            Name = c.Name!,
            BaseUrl = c.BaseUrl,
            Vars = Defaults(c.Vars),
            Tags = c.Tags,
            Body = c.Body,
        }),

        EnvFile e => EnvSpecEmitter.ToFileSource(new EnvSpecDto
        {
            Path = e.RelativePath,
            Id = e.Id,
            Name = e.Name!,
            Vars = Defaults(e.Vars),
            Tags = e.Tags,
            Body = e.Body,
        }),

        AuthFile a => AuthSpecEmitter.ToFileSource(new AuthSpecDto
        {
            Path = a.RelativePath,
            Id = a.Id,
            Name = a.Name!,
            Type = a.Type,
            Token = a.Fields.GetValueOrDefault("token"),
            Tags = a.Tags,
            Body = a.Body,
        }),

        FlowFile f => FlowSpecEmitter.ToFileSource(new FlowSpecDto
        {
            Path = f.RelativePath,
            Id = f.Id,
            Name = f.Name!,
            Vars = Defaults(f.Vars),
            Tags = f.Tags,
            Body = f.Body,
            Steps = TestingSpecMapper.ToDto(f.Steps),
        }),

        TestSetFile t => TestSetSpecEmitter.ToFileSource(new TestSetSpecDto
        {
            Path = t.RelativePath,
            Id = t.Id,
            Name = t.Name!,
            Vars = Defaults(t.Vars),
            OnFailure = t.OnFailure.ToWire(),
            Tags = t.Tags,
            Body = t.Body,
            Tests = TestingSpecMapper.ToDto(t.Tests),
        }),

        WorkspaceManifestFile m => WorkspaceSpecEmitter.ToFileSource(new WorkspaceSpecDto
        {
            Id = m.Id,
            Name = m.Name!,
            Vars = Defaults(m.Vars),
            Tags = m.Tags,
            Body = m.Body,
        }),

        _ => throw new InvalidOperationException($"No emitter wired for {file.GetType().Name}."),
    };

    private static IReadOnlyDictionary<string, string>? Defaults(IReadOnlyDictionary<string, VarSpec> vars)
        => vars.Count == 0
            ? null
            : vars.ToDictionary(kv => kv.Key, kv => kv.Value.Default ?? string.Empty, StringComparer.Ordinal);
}
