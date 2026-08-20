using Tap.Workspace.Model;
using Tap.Workspace.Security;
using Tap.Workspace.Variables;
using Tap.Workspace.Variables.Providers;

namespace Tap.Tests.Variables;

/// <summary>
/// The machine key's resolution rules. The precedence is the whole contract: an exported
/// <c>TAP_ENCRYPTION_KEY</c> must beat a key file left on the box, or a CI run picks up a
/// developer's key and writes secrets nobody else can read.
/// </summary>
public sealed class EncryptionKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tap-key-").FullName;
    private readonly string? _savedEnv = Environment.GetEnvironmentVariable(MachineEncryptionKeySource.EnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, _savedEnv);
        Directory.Delete(_dir, recursive: true);
    }

    private MachineEncryptionKeySource Source() => new(_dir);

    [Fact]
    public void No_key_anywhere_reports_none()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        var source = Source();
        Assert.Null(source.GetPassphrase());
        Assert.Equal(EncryptionKeyOrigin.None, source.Origin);
    }

    [Fact]
    public void The_key_file_is_read_and_its_trailing_newline_ignored()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        // `echo secret > encryption.key` is how a human writes this file; the newline must
        // not become part of the passphrase, or a hand-written key stops matching a
        // generated one and every stored secret reads as corrupt.
        File.WriteAllText(Path.Combine(_dir, MachineEncryptionKeySource.FileName), "from-file\n");

        var source = Source();
        Assert.Equal("from-file", source.GetPassphrase());
        Assert.Equal(EncryptionKeyOrigin.KeyFile, source.Origin);
    }

    [Fact]
    public void The_environment_variable_wins_over_the_key_file()
    {
        File.WriteAllText(Path.Combine(_dir, MachineEncryptionKeySource.FileName), "from-file\n");
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, "from-env");

        var source = Source();
        Assert.Equal("from-env", source.GetPassphrase());
        Assert.Equal(EncryptionKeyOrigin.Environment, source.Origin);
    }

    [Fact]
    public void An_empty_key_file_counts_as_no_key()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        File.WriteAllText(Path.Combine(_dir, MachineEncryptionKeySource.FileName), "   \n");

        Assert.Null(Source().GetPassphrase());
    }

    [Fact]
    public void Generate_writes_a_readable_key_and_refuses_to_clobber_it()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        var source = Source();

        var generated = source.GenerateKeyFile();
        Assert.NotEmpty(generated);
        Assert.Equal(generated, source.GetPassphrase());

        // The old key is the only thing that can read data written with it, so replacing it
        // has to be an explicit act.
        Assert.Throws<InvalidOperationException>(() => source.GenerateKeyFile());
        Assert.Equal(generated, source.GetPassphrase());

        var forced = source.GenerateKeyFile(force: true);
        Assert.NotEqual(generated, forced);
    }

    [Fact]
    public void Ensure_generates_a_key_when_the_machine_has_none()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        var source = Source();
        Assert.Equal(EncryptionKeyOrigin.None, source.Origin);

        var created = source.EnsurePassphrase();

        Assert.NotNull(created);
        Assert.NotEmpty(created);
        Assert.True(File.Exists(source.KeyFilePath));
        Assert.Equal(EncryptionKeyOrigin.KeyFile, source.Origin);
        // Stable: the second call reads what the first wrote rather than minting a new key,
        // or every save would encrypt under a key the previous save's data can't be read with.
        Assert.Equal(created, source.EnsurePassphrase());
        Assert.Equal(created, source.GetPassphrase());
    }

    [Fact]
    public void Ensure_does_not_write_a_key_file_the_environment_would_shadow()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, "from-env");
        var source = Source();

        Assert.Equal("from-env", source.EnsurePassphrase());
        // A key file here would be dead weight — GetPassphrase would never reach it, and it
        // would then block `key init` for the user who later unsets the variable.
        Assert.False(File.Exists(source.KeyFilePath));
    }

    [Fact]
    public void Ensure_keeps_an_existing_key_rather_than_replacing_it()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        File.WriteAllText(Path.Combine(_dir, MachineEncryptionKeySource.FileName), "from-file\n");

        Assert.Equal("from-file", Source().EnsurePassphrase());
    }

    [Fact]
    public void Ensure_replaces_a_blank_key_file()
    {
        // `touch encryption.key` leaves a file holding no key. It has nothing to lose, so it
        // must not wedge the machine into a state where generation always fails.
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        File.WriteAllText(Path.Combine(_dir, MachineEncryptionKeySource.FileName), "   \n");

        var created = Source().EnsurePassphrase();

        Assert.NotNull(created);
        Assert.Equal(created, Source().GetPassphrase());
    }

    [Fact]
    public void A_key_generated_under_a_race_is_never_clobbered()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        var winner = Source().GenerateKeyFile();

        // A second process that got past its own "is there a key?" check before the winner
        // wrote: the create fails on the file itself, and Ensure falls back to reading the
        // key that won. Anything else would strand data the winner had already encrypted.
        Assert.Throws<InvalidOperationException>(() => Source().GenerateKeyFile());
        Assert.Equal(winner, Source().EnsurePassphrase());
    }

    /// <summary>
    /// The end of the feature, not just its mechanism: a machine that has never seen a key
    /// stores a secret successfully, and the key file exists afterwards. Lives here rather
    /// than beside the other provider tests because it drives the real
    /// <see cref="MachineEncryptionKeySource"/>, and only this class serializes the
    /// process-wide environment variable those depend on.
    /// </summary>
    [Fact]
    public async Task Storing_a_secret_on_a_keyless_machine_creates_the_key_and_encrypts()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        var workspace = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(workspace);
        var source = Source();
        var provider = new FileVariableProvider(
            new VariableProviderConfig { Name = "vault", Type = "file", Origin = ProviderOrigin.Workspace },
            workspace,
            source);

        Assert.False(provider.HasEncryptionKey);

        await provider.SetAsync("api.key", "sk-live-4242", isSecret: true, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(source.KeyFilePath));
        var onDisk = await File.ReadAllTextAsync(
            Path.Combine(workspace, ".vars", "vault.yml"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("sk-live-4242", onDisk, StringComparison.Ordinal);

        var read = await provider.GetAsync("api.key", TestContext.Current.CancellationToken);
        Assert.Equal("sk-live-4242", read!.Value);
    }

    /// <summary>Reading is not a request to protect anything, so it must not mint a key —
    /// a missing key on the decrypt path means the ciphertext is orphaned, and the user needs
    /// to be told that rather than handed a key that still cannot read it.</summary>
    [Fact]
    public async Task Reading_a_secret_never_generates_a_key()
    {
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        var workspace = Path.Combine(_dir, "orphaned");
        Directory.CreateDirectory(Path.Combine(workspace, ".vars"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".vars", "vault.yml"),
            $"variables:\n  api.key:\n    value: {FileVariableProvider.EnvelopePrefix}not-decryptable\n    secret: true\n",
            TestContext.Current.CancellationToken);

        var source = Source();
        var provider = new FileVariableProvider(
            new VariableProviderConfig { Name = "vault", Type = "file", Origin = ProviderOrigin.Workspace },
            workspace,
            source);

        await Assert.ThrowsAsync<WorkspaceParseException>(async () =>
            await provider.GetAsync("api.key", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(source.KeyFilePath));
    }

    [Fact]
    public void Generated_key_files_are_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable(MachineEncryptionKeySource.EnvVar, null);
        var source = Source();
        source.GenerateKeyFile();

        var mode = File.GetUnixFileMode(source.KeyFilePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
