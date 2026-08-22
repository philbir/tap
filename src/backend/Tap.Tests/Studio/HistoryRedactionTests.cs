using Microsoft.Extensions.Logging.Abstractions;
using Tap.Studio.Contracts;
using Tap.Studio.History;
using Tap.Studio.Specs;
using Tap.Workspace.Model;
using Tap.Workspace.Rendering;

namespace Tap.Tests.Studio;

/// <summary>
/// What a recorded entry is allowed to contain. A plaintext entry must never hold a credential;
/// an encrypted one is allowed to, and that asymmetry is the reason the two travel together.
/// The redactor itself is covered by <c>SecretRedactorTests</c> — these check that history
/// actually runs it, on every surface a secret can reach.
/// </summary>
public class HistoryRedactionTests
{
    private const string Token = "sk-live-9f3ac1e2b7d84";

    [Fact]
    public void A_plaintext_entry_masks_credential_headers_and_secret_values_everywhere()
    {
        var entry = Record(encrypt: false);

        // By header name — a Basic credential is derived rather than resolved, so it never
        // appears verbatim in any provider's output and value-matching alone would miss it.
        Assert.Equal("***", entry.Request.Headers["Authorization"]);
        Assert.Equal("***", entry.Response!.Headers["Set-Cookie"]);

        // By value — wherever it landed: the URL, the request body, the response body.
        Assert.DoesNotContain(Token, entry.Request.Url);
        Assert.DoesNotContain(Token, entry.Request.Body);
        Assert.DoesNotContain(Token, entry.Response.Body);

        Assert.True(entry.Redacted);
    }

    [Fact]
    public void An_encrypted_entry_keeps_what_actually_went_on_the_wire()
    {
        // The point of the option: the file is unreadable without this machine's key, so it can
        // answer "what token did we actually send" — which a masked entry never can.
        var entry = Record(encrypt: true);

        Assert.Contains(Token, entry.Request.Headers["Authorization"]);
        Assert.Contains(Token, entry.Request.Url);
        Assert.False(entry.Redacted);
    }

    [Fact]
    public void Variables_are_recorded_by_name_never_by_value()
    {
        var entry = Record(encrypt: false);
        var variable = Assert.Single(entry.VariablesUsed);

        Assert.Equal("vault", variable.Provider);
        Assert.Equal("API_TOKEN", variable.Name);
        Assert.True(variable.Secret);
        // The type has nowhere to put a value, which is the guarantee — not a filtering step
        // somebody could forget to apply.
    }

    [Fact]
    public void A_body_past_the_cap_is_trimmed_and_says_so()
    {
        var entry = Record(encrypt: false, maxBodyBytes: 16, responseBody: new string('x', 100));

        Assert.Equal(16, entry.Response!.Body!.Length);
        Assert.True(entry.Response.BodyTruncated);
        // The reported size stays what the upstream sent, not what we chose to keep.
        Assert.Equal(100, entry.Response.BodyBytes);
    }

    /// <summary>Runs one exchange through the recorder and returns the entry it wrote.</summary>
    private static HistoryEntry Record(bool encrypt, long maxBodyBytes = 256 * 1024, string? responseBody = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "tap-history-redaction-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new HistoryStore(root, new Tap.Workspace.Security.StaticEncryptionKeySource("key"));
            var options = new HistoryOptions { Enabled = true, Encrypt = encrypt, MaxBodyBytes = maxBodyBytes };

            var rendered = new ResolvedRequest
            {
                Method = "POST",
                Url = $"https://api.test/things?token={Token}",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = $"Bearer {Token}",
                    ["Accept"] = "application/json",
                },
                Body = $$"""{"key":"{{Token}}"}""",
                History = options,
                Redactor = new SecretRedactor([Token], ["X-Api-Key"]),
                Metadata = new ResolvedRequestMetadata
                {
                    SourceRequestPath = "collections/demo/thing.req.tap",
                    VariablesUsed = [new Tap.Workspace.Variables.VariableResolution(
                        "vault", "API_TOKEN", Resolved: true, IsSecret: true, TimeSpan.Zero)],
                },
            };

            var response = new HistoryResponse(
                200, "OK",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Set-Cookie"] = $"session={Token}",
                    ["Content-Type"] = "application/json",
                },
                "application/json",
                responseBody ?? $$"""{"echo":"{{Token}}"}""",
                responseBody?.Length ?? 40,
                BodyTruncated: false);

            var file = new RequestFile
            {
                Kind = WorkspaceKind.Request,
                RelativePath = "collections/demo/thing.req.tap",
                Id = "req-1",
                Name = "Thing",
            };
            var workspace = new Tap.Workspace.LoadedWorkspace(root, root, [file], []);

            var recorder = new HistoryRecorder(new FixedStore(store), NullLogger<HistoryRecorder>.Instance);
            recorder.TryRecord(workspace, rendered, response, 12.5, [], null, null);

            var written = Assert.Single(store.ListForRequest("req-1", workspace));
            return store.Read("req-1", written.Id)!;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>The recorder's one dependency, pinned to a store in a temp folder.</summary>
    private sealed class FixedStore(HistoryStore store) : IHistoryStores
    {
        public HistoryStore Current => store;
    }
}
