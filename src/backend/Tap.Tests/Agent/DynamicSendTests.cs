using Tap.Execution.Agent;
using Tap.Execution.Contracts;
using Tap.Execution.Testing;
using Tap.Execution.Workspace;
using static Tap.Tests.Agent.AgentTestData;

namespace Tap.Tests.Agent;

/// <summary>
/// The runner-side half of the dynamic story: an in-memory request goes through
/// <see cref="TestRunner.SendAsync(Tap.Workspace.Model.RequestFile, RunTestRequestDto, CancellationToken)"/>,
/// and <see cref="TestRunner.OnRendered"/> is the last gate before the wire — a guard that
/// throws there must fail the step without sending anything.
/// </summary>
public class DynamicSendTests
{
    [Fact]
    public async Task A_guard_thrown_from_OnRendered_fails_the_step_before_it_is_sent()
    {
        var ws = BuildWorkspace(DemoCollection(), BearerAuth);
        var requestFile = DynamicRequestFactory.Create(ws, "Demo", new DynamicRequestSpec
        {
            Method = "GET",
            Url = "{{TARGET}}/x",
        });

        string? renderedUrl = null;
        var runner = new TestRunner(new RequestPipeline(new FakeWorkspaceHost(ws)))
        {
            OnRendered = rendered =>
            {
                renderedUrl = rendered.Url;
                DynamicRequestFactory.EnsureCollectionScoped(rendered, allowAnyUrl: false);
            },
        };

        var step = await runner.SendAsync(
            requestFile,
            new RunTestRequestDto(
                Path: requestFile.RelativePath, Env: null, Only: null,
                Overrides: new Dictionary<string, string> { ["TARGET"] = "http://evil.example" }),
            CancellationToken.None);

        // The render happened (the guard saw the expanded URL), the step failed with the
        // guard's message, and no exchange took place — status 0, nothing read back.
        Assert.Equal("http://evil.example/x", renderedUrl);
        Assert.False(step.Ok);
        Assert.Contains("absolute URL", step.Error);
        Assert.Equal(0, step.Status);
        Assert.Equal(0L, step.ResponseBodyBytes);
    }
}
