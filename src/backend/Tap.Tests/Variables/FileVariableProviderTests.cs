using Tap.Workspace.Model;
using Tap.Workspace.Security;
using Tap.Workspace.Variables;
using Tap.Workspace.Variables.Providers;

namespace Tap.Tests.Variables;

/// <summary>
/// The file provider's storage contract: what lands on disk, what comes back, and what
/// happens on a machine with no key. Uses <see cref="StaticEncryptionKeySource"/> throughout —
/// these tests must never depend on (or disturb) the ambient environment.
/// </summary>
public sealed class FileVariableProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tap-fileprov-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string StorePath(string name = "vault") => Path.Combine(_root, ".vars", name + ".yml");

    private FileVariableProvider Provider(string? passphrase, string name = "vault")
        => new(
            new VariableProviderConfig { Name = name, Type = "file", Origin = ProviderOrigin.Workspace },
            _root,
            new StaticEncryptionKeySource(passphrase));

    [Fact]
    public async Task A_secret_round_trips_and_never_hits_disk_in_clear()
    {
        var provider = Provider("pass-1");
        await provider.SetAsync("api.key", "sk-live-4242", isSecret: true, TestContext.Current.CancellationToken);

        var onDisk = await File.ReadAllTextAsync(StorePath(), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("sk-live-4242", onDisk, StringComparison.Ordinal);
        Assert.Contains(FileVariableProvider.EnvelopePrefix, onDisk, StringComparison.Ordinal);

        var read = await provider.GetAsync("api.key", TestContext.Current.CancellationToken);
        Assert.NotNull(read);
        Assert.Equal("sk-live-4242", read!.Value);
        Assert.True(read.IsSecret);
    }

    [Fact]
    public async Task Plain_values_work_on_a_machine_with_no_key_at_all()
    {
        var provider = Provider(null);
        await provider.SetAsync("base.url", "https://example.test", isSecret: false, TestContext.Current.CancellationToken);

        var read = await provider.GetAsync("base.url", TestContext.Current.CancellationToken);
        Assert.Equal("https://example.test", read!.Value);
        Assert.False(read.IsSecret);
        Assert.False(provider.HasEncryptionKey);
    }

    [Fact]
    public async Task Storing_a_secret_without_a_key_fails_with_both_sources_named()
    {
        var provider = Provider(null);

        var ex = await Assert.ThrowsAsync<WorkspaceParseException>(async () =>
            await provider.SetAsync("api.key", "sk-live", isSecret: true, TestContext.Current.CancellationToken));

        // The only useful thing to tell someone who can't encrypt is where to put a key.
        Assert.Contains(MachineEncryptionKeySource.EnvVar, ex.Error.Message, StringComparison.Ordinal);
        Assert.Contains(MachineEncryptionKeySource.FileName, ex.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_key_cannot_read_a_secret_and_listing_survives_it()
    {
        await Provider("right-key").SetAsync("api.key", "sk-live", isSecret: true, TestContext.Current.CancellationToken);

        var wrong = Provider("wrong-key");
        await Assert.ThrowsAsync<WorkspaceParseException>(async () =>
            await wrong.GetAsync("api.key", TestContext.Current.CancellationToken));

        // ListAsync must not take the whole variables panel down over one undecryptable
        // entry — it surfaces the envelope so the UI can show the row as broken.
        var list = await wrong.ListAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(list);
        Assert.StartsWith(FileVariableProvider.EnvelopePrefix, row.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_generated_after_construction_takes_effect_without_a_new_instance()
    {
        // The Studio caches provider instances across requests. Generating a key in Settings
        // and immediately saving a secret has to work, or the fix reads as "restart Tap".
        var key = new MutableKeySource(null);
        var provider = new FileVariableProvider(
            new VariableProviderConfig { Name = "vault", Type = "file", Origin = ProviderOrigin.Workspace },
            _root, key);

        Assert.False(provider.HasEncryptionKey);
        key.Passphrase = "generated-later";

        await provider.SetAsync("api.key", "sk-live", isSecret: true, TestContext.Current.CancellationToken);
        var read = await provider.GetAsync("api.key", TestContext.Current.CancellationToken);
        Assert.Equal("sk-live", read!.Value);
    }

    [Fact]
    public async Task Delete_removes_the_entry_and_reports_whether_there_was_one()
    {
        var provider = Provider("pass-1");
        await provider.SetAsync("a", "1", isSecret: false, TestContext.Current.CancellationToken);
        await provider.SetAsync("b", "2", isSecret: true, TestContext.Current.CancellationToken);

        Assert.True(await provider.DeleteAsync("a", TestContext.Current.CancellationToken));
        Assert.False(await provider.DeleteAsync("a", TestContext.Current.CancellationToken));

        var remaining = await provider.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("b", Assert.Single(remaining).Name);
    }

    [Fact]
    public async Task Two_providers_sharing_one_machine_key_do_not_share_ciphertext()
    {
        // The PBKDF2 salt is per-provider, so one machine key still yields a distinct derived
        // key per store. Same passphrase, same plaintext, different envelope.
        await Provider("one-key", "alpha").SetAsync("k", "same-value", isSecret: true, TestContext.Current.CancellationToken);
        await Provider("one-key", "beta").SetAsync("k", "same-value", isSecret: true, TestContext.Current.CancellationToken);

        var alpha = await File.ReadAllTextAsync(StorePath("alpha"), TestContext.Current.CancellationToken);
        var beta = await File.ReadAllTextAsync(StorePath("beta"), TestContext.Current.CancellationToken);
        Assert.NotEqual(alpha, beta);

        // …and each still reads its own back.
        Assert.Equal("same-value", (await Provider("one-key", "alpha").GetAsync("k", TestContext.Current.CancellationToken))!.Value);
        Assert.Equal("same-value", (await Provider("one-key", "beta").GetAsync("k", TestContext.Current.CancellationToken))!.Value);
    }

    [Fact]
    public void An_unwritten_store_still_knows_where_it_lives_and_opens_on_a_skeleton()
    {
        var provider = Provider("pass-1");

        Assert.Equal(StorePath(), provider.StorePath);
        Assert.False(File.Exists(provider.StorePath));

        // "Nothing stored yet" is a valid state, so the source view opens on the shape a
        // store has rather than on an error or a blank page.
        var source = provider.ReadSource();
        Assert.Contains("variables:", source, StringComparison.Ordinal);
        Assert.Contains(MachineEncryptionKeySource.EnvVar, source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_edits_land_as_variables_the_table_reads_back()
    {
        var provider = Provider("pass-1");
        provider.WriteSource("variables:\n  base.url: https://example.test\n  retries: '3'\n");

        var list = await provider.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, list.Count);
        Assert.Equal("https://example.test", (await provider.GetAsync("base.url", TestContext.Current.CancellationToken))!.Value);

        // And what comes back out is what went in — the source view is the file, not a
        // re-render of it.
        Assert.Contains("retries: '3'", provider.ReadSource(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_written_through_source_survives_a_source_round_trip()
    {
        var provider = Provider("pass-1");
        await provider.SetAsync("api.key", "sk-live-4242", isSecret: true, TestContext.Current.CancellationToken);

        // Hand-editing around an encrypted entry (renaming a plain neighbour, say) must not
        // cost the secret: the envelope is carried across verbatim.
        provider.WriteSource(provider.ReadSource() + "  base.url: https://example.test\n");

        Assert.Equal("sk-live-4242", (await provider.GetAsync("api.key", TestContext.Current.CancellationToken))!.Value);
        Assert.Equal("https://example.test", (await provider.GetAsync("base.url", TestContext.Current.CancellationToken))!.Value);
    }

    [Theory]
    // A secret nobody could decrypt — clear text under a flag claiming otherwise.
    [InlineData("variables:\n  api.key:\n    value: sk-live-typed-by-hand\n    secret: true\n", "envelope")]
    // Typo'd key, silently dropped by a tolerant load.
    [InlineData("variables:\n  api.key:\n    value: x\n    secrets: true\n", "unknown key")]
    [InlineData("variables:\n  api.key:\n    secret: true\n", "no `value:`")]
    [InlineData("variables:\n  - one\n  - two\n", "mapping")]
    [InlineData("variables:\n  api.key: [broken\n", "not valid YAML")]
    public async Task Source_writes_that_could_not_be_read_back_are_refused_and_change_nothing(
        string text, string expected)
    {
        var provider = Provider("pass-1");
        await provider.SetAsync("kept", "value", isSecret: false, TestContext.Current.CancellationToken);
        var before = await File.ReadAllTextAsync(StorePath(), TestContext.Current.CancellationToken);

        var ex = Assert.Throws<WorkspaceParseException>(() => provider.WriteSource(text));
        Assert.Contains(expected, ex.Error.Message, StringComparison.OrdinalIgnoreCase);

        // The refusal is the point: the file has to be exactly as it was.
        Assert.Equal(before, await File.ReadAllTextAsync(StorePath(), TestContext.Current.CancellationToken));
        Assert.Equal("value", (await provider.GetAsync("kept", TestContext.Current.CancellationToken))!.Value);
    }

    [Fact]
    public async Task An_emptied_store_is_valid_source_not_a_malformed_one()
    {
        var provider = Provider("pass-1");
        await provider.SetAsync("gone", "value", isSecret: false, TestContext.Current.CancellationToken);

        provider.WriteSource("variables:\n");

        Assert.Empty(await provider.ListAsync(TestContext.Current.CancellationToken));
    }

    private sealed class MutableKeySource(string? passphrase) : IEncryptionKeySource
    {
        public string? Passphrase { get; set; } = passphrase;
        public string? GetPassphrase() => Passphrase;
        /// <summary>Stands in for a host that resolves its key elsewhere: there is nothing
        /// for Tap to create, so "ensure" is just "get".</summary>
        public string? EnsurePassphrase() => Passphrase;
        public EncryptionKeyOrigin Origin
            => Passphrase is null ? EncryptionKeyOrigin.None : EncryptionKeyOrigin.KeyFile;
    }
}
