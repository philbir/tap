using System.Security.Cryptography;
using System.Text;
using Tap.Workspace.Model;
using Tap.Workspace.Security;
using YamlDotNet.RepresentationModel;

namespace Tap.Workspace.Variables.Providers;

/// <summary>
/// Read-write provider that persists variables to a dedicated YAML file under the workspace:
/// <c>.tap/.vars/&lt;provider-name&gt;.yml</c>. Each value can be marked <c>secret</c>; secret
/// values are encrypted at rest using AES-256-GCM with a key derived (PBKDF2-HMAC-SHA256)
/// from the machine's encryption passphrase (<see cref="IEncryptionKeySource"/>:
/// <c>TAP_ENCRYPTION_KEY</c>, else <c>&lt;system-dir&gt;/encryption.key</c>).
///
/// <para>The passphrase is deliberately <b>not</b> a provider setting. A key configured
/// alongside the workspace travels with the ciphertext it unlocks — into Git, into the
/// container image, into whatever the workspace gets copied to — which is not encryption at
/// all. Making it a property of the machine rather than of the provider removes the option.
/// The PBKDF2 salt is still per-provider (<c>tap-file-provider:&lt;name&gt;</c>), so one
/// machine key yields a distinct derived key per store.</para>
///
/// <para>The key is only needed to read or write <c>secret: true</c> values; plain values work
/// on a machine with no key at all.</para>
///
/// <para>On-disk envelope for secret values: <c>enc:v1:&lt;iv-b64&gt;:&lt;ciphertext-b64&gt;:&lt;tag-b64&gt;</c>.
/// Plain values are stored as YAML scalars. The provider file lives entirely under the
/// workspace's <c>.tap/</c> directory so it round-trips with the rest of the workspace and
/// stays out of the per-scope file cascade.</para>
///
/// <para>This provider intentionally does NOT touch per-scope <c>vars:</c> blocks (in
/// workspace/api/stage/env/request files). Those continue to be edited through the
/// existing scope-specific file editors. The file provider is the durable place for
/// "workspace-wide" variables that don't belong to one scope.</para>
/// </summary>
public sealed class FileVariableProvider : IVariableProvider
{
    /// <summary>On-disk marker for encrypted values. Public so the Studio's provider-test
    /// endpoint can detect secrets that failed to decrypt (ListAsync surfaces the raw
    /// envelope on decrypt failure instead of throwing). The format itself lives in
    /// <see cref="SecretEnvelope"/>, shared with request history.</summary>
    public const string EnvelopePrefix = SecretEnvelope.Prefix;

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _storePath;
    private readonly IEncryptionKeySource _keySource;
    private readonly DerivedKey _derived;
    private readonly Lock _gate = new();
    private VariableProviderConfig _config;

    public FileVariableProvider(VariableProviderConfig config, string workspaceRoot, IEncryptionKeySource keySource)
    {
        _config = config;
        _keySource = keySource;
        _storePath = Path.Combine(workspaceRoot, ".vars", config.Name + ".yml");
        // Per-provider salt: one machine key, a distinct derived key per store.
        _derived = new DerivedKey(keySource, "tap-file-provider:" + config.Name);
    }

    /// <summary>True when this machine has no encryption passphrase, so secret values can
    /// neither be written nor read back. Surfaced to the Studio so the provider editor can
    /// offer to generate one instead of failing on save.</summary>
    public bool HasEncryptionKey => _keySource.GetPassphrase() is not null;

    /// <summary>The AES key for this store, or <c>null</c> when the machine has no passphrase.
    /// Resolved per call rather than at construction: providers are cached across requests, and
    /// a key generated after the instance was built must take effect immediately.</summary>
    private byte[]? Key(bool create = false) => _derived.Get(create);

    /// <summary>The message every "no key" failure shares. Names both sources, because the
    /// only useful thing to say to someone holding undecryptable data is where to put the key.
    ///
    /// <para>On the write path this is now the rare case: storing a secret generates a key when
    /// the machine has none. Reaching it there means generation itself failed — an unwritable
    /// system directory — which is why the message still names the manual routes.</para></summary>
    private static string NoKeyHint =>
        $"No encryption key on this machine — set {MachineEncryptionKeySource.EnvVar}, or generate "
        + $"'{MachineEncryptionKeySource.Default.KeyFilePath}' from Settings or `tap-studio key init`.";

    public string Name => _config.Name;
    public ProviderMode Mode => ProviderMode.ReadWrite;  // static — the file provider's whole point is durable, writable storage.
    public VariableProviderConfig Config => _config;

    public ValueTask<VariableValue?> GetAsync(string name, CancellationToken ct)
    {
        var store = LoadStore();
        if (!store.TryGetValue(name, out var entry)) return ValueTask.FromResult<VariableValue?>(null);
        var clear = entry.IsSecret ? Decrypt(entry.Value, name) : entry.Value;
        return ValueTask.FromResult<VariableValue?>(new VariableValue(name, clear, entry.IsSecret, Name));
    }

    public ValueTask<IReadOnlyList<VariableValue>> ListAsync(CancellationToken ct)
    {
        var store = LoadStore();
        var result = new List<VariableValue>(store.Count);
        foreach (var (name, entry) in store)
        {
            // Secret values still get returned as their clear text — the caller (registry +
            // view builder) is responsible for masking them in UI-facing payloads. Internal
            // execution paths need the clear value to actually issue the request.
            var clear = entry.IsSecret ? TryDecrypt(entry.Value) : entry.Value;
            result.Add(new VariableValue(name, clear, entry.IsSecret, Name));
        }
        return ValueTask.FromResult<IReadOnlyList<VariableValue>>(result);
    }

    public ValueTask SetAsync(string name, string value, bool isSecret, CancellationToken ct)
    {
        // Derive before taking the gate: PBKDF2 is the slow part, and it needs nothing the
        // store holds.
        var key = isSecret ? Key(create: true) : null;
        if (isSecret && key is null)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                $"Cannot store secret variable '{name}' in file provider '{Name}'. {NoKeyHint}"));
        }

        lock (_gate)
        {
            var store = LoadStore();
            var stored = isSecret ? Encrypt(value, key!) : value;
            store[name] = new Entry(stored, isSecret);
            SaveStore(store);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteAsync(string name, CancellationToken ct)
    {
        lock (_gate)
        {
            var store = LoadStore();
            if (!store.Remove(name)) return ValueTask.FromResult(false);
            SaveStore(store);
            return ValueTask.FromResult(true);
        }
    }

    private Dictionary<string, Entry> LoadStore()
    {
        lock (_gate)
        {
            if (!File.Exists(_storePath)) return new Dictionary<string, Entry>(StringComparer.Ordinal);
            using var reader = new StreamReader(_storePath);
            var stream = new YamlStream();
            stream.Load(reader);
            var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
            if (stream.Documents.Count == 0) return result;
            if (stream.Documents[0].RootNode is not YamlMappingNode root) return result;
            if (!root.Children.TryGetValue(new YamlScalarNode("variables"), out var varsNode)) return result;
            if (varsNode is not YamlMappingNode vars) return result;

            foreach (var (k, v) in vars)
            {
                if (k is not YamlScalarNode keyNode || keyNode.Value is null) continue;
                if (v is YamlScalarNode scalar)
                {
                    // Compact form: name: value (always non-secret).
                    result[keyNode.Value] = new Entry(scalar.Value ?? string.Empty, IsSecret: false);
                }
                else if (v is YamlMappingNode obj)
                {
                    var valueText = (obj.Children.TryGetValue(new YamlScalarNode("value"), out var vnode)
                        && vnode is YamlScalarNode vs) ? (vs.Value ?? string.Empty) : string.Empty;
                    var secretFlag = obj.Children.TryGetValue(new YamlScalarNode("secret"), out var snode)
                        && snode is YamlScalarNode ss
                        && string.Equals(ss.Value, "true", StringComparison.OrdinalIgnoreCase);
                    result[keyNode.Value] = new Entry(valueText, secretFlag);
                }
            }
            return result;
        }
    }

    private void SaveStore(IReadOnlyDictionary<string, Entry> store)
    {
        var dir = Path.GetDirectoryName(_storePath)!;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, OwnerOnlyDirectory);

        var root = new YamlMappingNode();
        var varsNode = new YamlMappingNode();
        foreach (var (name, entry) in store.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (entry.IsSecret)
            {
                var obj = new YamlMappingNode
                {
                    { "value", new YamlScalarNode(entry.Value) { Style = YamlDotNet.Core.ScalarStyle.SingleQuoted } },
                    { "secret", new YamlScalarNode("true") { Style = YamlDotNet.Core.ScalarStyle.Plain } },
                };
                varsNode.Add(name, obj);
            }
            else
            {
                varsNode.Add(name, new YamlScalarNode(entry.Value) { Style = QuoteStyleFor(entry.Value) });
            }
        }
        root.Add("variables", varsNode);

        var sb = new StringBuilder();
        sb.Append("# Tap file provider store — written by the Studio UI / API.\n");
        sb.Append("# Secret values use the envelope: enc:v1:<iv-b64>:<ciphertext-b64>:<tag-b64>\n");
        sb.Append($"# Do NOT hand-edit secret values; they are keyed to this machine's {MachineEncryptionKeySource.EnvVar}\n");
        sb.Append($"# (or {MachineEncryptionKeySource.FileName}). Committing this file without that key commits nothing usable.\n");

        var doc = new YamlDocument(root);
        var stream = new YamlStream(doc);
        using var writer = new StringWriter(sb);
        stream.Save(writer, assignAnchors: false);
        var text = sb.ToString().Replace("---\r\n", string.Empty).Replace("---\n", string.Empty);
        var dotIdx = text.IndexOf("\n...", StringComparison.Ordinal);
        if (dotIdx > 0) text = text[..dotIdx] + "\n";

        WriteAtomicOwnerOnly(_storePath, text);
    }

    /// <summary>Writes the store crash-safely and owner-only. <c>File.WriteAllText</c>
    /// truncates the real file first, so a crash mid-write loses every variable in it; and the
    /// default creation mode is world-readable on Linux, which matters for the plain values
    /// stored here and hands an offline attacker the ciphertext for free. The temp file is
    /// created 0600 up front and renamed over the target, so neither window exists.</summary>
    private static void WriteAtomicOwnerOnly(string path, string text)
    {
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;

            using (var fs = new FileStream(tmp, options))
            {
                fs.Write(Encoding.UTF8.GetBytes(text));
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, OwnerOnlyFile); }
        catch
        {
            // ignore — the file is still owned by the current user
        }
    }

    private static string Encrypt(string clear, byte[] key) => SecretEnvelope.Protect(clear, key);

    private string Decrypt(string stored, string name)
    {
        if (!SecretEnvelope.IsEnvelope(stored))
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_DECRYPT_FAILED,
                $"File provider '{Name}' variable '{name}' is marked secret but the stored value is not in the v1 envelope."));
        }
        var key = Key();
        if (key is null)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                $"Cannot decrypt secret variable '{name}' in file provider '{Name}'. {NoKeyHint}"));
        }
        try
        {
            return SecretEnvelope.Unprotect(stored, key);
        }
        catch (CryptographicException ex)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_DECRYPT_FAILED,
                $"File provider '{Name}' could not decrypt variable '{name}': {ex.Message}"));
        }
        catch (FormatException ex)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_DECRYPT_FAILED,
                $"File provider '{Name}' variable '{name}' has a malformed envelope: {ex.Message}"));
        }
    }

    /// <summary>For ListAsync, swallow decrypt failures: we surface the encrypted blob as the
    /// "value" so the UI shows the key is broken without taking down the entire variables
    /// panel. Calls that need the clear text (resolve / render) use Decrypt() directly and
    /// surface the error.</summary>
    private string TryDecrypt(string stored)
    {
        try { return Decrypt(stored, "(list)"); }
        catch { return stored; }
    }

    private static YamlDotNet.Core.ScalarStyle QuoteStyleFor(string value)
    {
        if (value.Length == 0) return YamlDotNet.Core.ScalarStyle.Any;
        var first = value[0];
        if (first == '$' || first == '{' || first == '*' || first == '&' || first == '!' || first == '@')
            return YamlDotNet.Core.ScalarStyle.SingleQuoted;
        if (value.Contains(": ", StringComparison.Ordinal) || value.EndsWith(':'))
            return YamlDotNet.Core.ScalarStyle.SingleQuoted;
        return YamlDotNet.Core.ScalarStyle.Any;
    }

    private sealed record Entry(string Value, bool IsSecret);
}

public sealed class FileVariableProviderFactory : IVariableProviderFactory
{
    public string Type => "file";

    public ProviderTypeDescriptor Descriptor { get; } = new()
    {
        Type = "file",
        DisplayName = "Encrypted file",
        Icon = "file",
        Description = "Stores variables in a YAML file under the workspace; secret values are encrypted with this machine's encryption key.",
        Mode = ProviderMode.ReadWrite,
        // No settings. The one thing this provider needs — the passphrase — is a property of
        // the machine, not of the provider: see FileVariableProvider's remarks.
        Fields = [],
    };

    public IVariableProvider Create(VariableProviderConfig config, ProviderFactoryContext context)
        => new FileVariableProvider(config, context.WorkspaceRoot, context.KeySource);
}
