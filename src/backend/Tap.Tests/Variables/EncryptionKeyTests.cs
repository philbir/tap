using Tap.Workspace.Security;

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
