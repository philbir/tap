using System.Security.Cryptography;
using System.Text;
using Tap.Workspace.Model;
using Tap.Workspace.Security;
using YamlDotNet.RepresentationModel;

namespace Tap.Workspace.Variables.Providers;

/// <summary>
/// Read-write provider that persists variables to a dedicated YAML file under the workspace:
/// <c>.vars/&lt;provider-name&gt;.yml</c>. Each value can be marked <c>secret</c>; secret
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
/// Plain values are stored as YAML scalars. The file sits beside the workspace's own files so
/// it round-trips with them, and outside the per-scope file cascade — nothing parses it as a
/// workspace file, which is why raw-source editing goes through this provider
/// (<see cref="IFileBackedVariableProvider"/>) rather than the shared source endpoint.</para>
///
/// <para>This provider intentionally does NOT touch per-scope <c>vars:</c> blocks (in
/// workspace/collection/env/request files). Those continue to be edited through the
/// existing scope-specific file editors. The file provider is the durable place for
/// "workspace-wide" variables that don't belong to one scope.</para>
/// </summary>
public sealed class FileVariableProvider : IVariableProvider, IFileBackedVariableProvider
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

    /// <summary>Absolute path of the YAML store: <c>&lt;workspace&gt;/.vars/&lt;name&gt;.yml</c>.
    /// Present whether or not anything has been written yet — a store with no variables in it
    /// still has a place it would live, and that place is what someone has to back up.</summary>
    public string StorePath => _storePath;

    /// <inheritdoc />
    public string ReadSource()
    {
        lock (_gate)
        {
            return File.Exists(_storePath) ? File.ReadAllText(_storePath) : Header + "variables:\n";
        }
    }

    /// <inheritdoc />
    public void WriteSource(string text)
    {
        // Parse strictly first: the whole point of validating before writing is that a file
        // this provider cannot read back never reaches disk. Every entry is checked, including
        // ones a tolerant load would have skipped over in silence.
        ParseStore(text, strict: true);
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_storePath)!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, OwnerOnlyDirectory);
            WriteAtomicOwnerOnly(_storePath, text);
        }
    }

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
            return ParseStore(File.ReadAllText(_storePath), strict: false);
        }
    }

    /// <summary>
    /// Reads the store's YAML into entries. Two modes, one grammar: the load path is tolerant
    /// (anything it doesn't recognise is skipped, so one bad line can't hide every other
    /// variable), while <paramref name="strict"/> — used by <see cref="WriteSource"/> — refuses
    /// the same input and says where. Sharing the walk is what keeps "it validated" and "it
    /// loads" from drifting apart.
    /// </summary>
    private Dictionary<string, Entry> ParseStore(string text, bool strict)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            if (!strict) return result;
            throw Invalid($"{Name}'s store is not valid YAML: {ex.Message}", ex.Start.Line > 0 ? (int)ex.Start.Line : null);
        }

        if (stream.Documents.Count == 0) return result;   // empty file — an empty store.
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            if (!strict) return result;
            throw Invalid("The store must be a YAML mapping with a top-level `variables:` key.", null);
        }
        if (!root.Children.TryGetValue(new YamlScalarNode("variables"), out var varsNode))
        {
            if (!strict) return result;
            throw Invalid("The store has no top-level `variables:` key.", Line(root));
        }
        // `variables:` with nothing under it parses as a null scalar. That's an empty store,
        // not a malformed one — it is exactly what a store reads as after its last delete.
        if (varsNode is YamlScalarNode { Value: null or "" }) return result;
        if (varsNode is not YamlMappingNode vars)
        {
            if (!strict) return result;
            throw Invalid("`variables:` must be a mapping of name to value.", Line(varsNode));
        }

        foreach (var (k, v) in vars)
        {
            if (k is not YamlScalarNode keyNode || keyNode.Value is null) continue;
            var name = keyNode.Value;
            if (v is YamlScalarNode scalar)
            {
                // Compact form: name: value (always non-secret).
                result[name] = new Entry(scalar.Value ?? string.Empty, IsSecret: false);
            }
            else if (v is YamlMappingNode obj)
            {
                var valueText = (obj.Children.TryGetValue(new YamlScalarNode("value"), out var vnode)
                    && vnode is YamlScalarNode vs) ? (vs.Value ?? string.Empty) : string.Empty;
                var secretFlag = obj.Children.TryGetValue(new YamlScalarNode("secret"), out var snode)
                    && snode is YamlScalarNode ss
                    && string.Equals(ss.Value, "true", StringComparison.OrdinalIgnoreCase);

                if (strict)
                {
                    foreach (var (ck, _) in obj.Children)
                    {
                        if (ck is YamlScalarNode { Value: "value" or "secret" }) continue;
                        throw Invalid(
                            $"'{name}' has an unknown key '{(ck as YamlScalarNode)?.Value}'. An entry holds `value:` and `secret:`.",
                            Line(ck));
                    }
                    if (!obj.Children.ContainsKey(new YamlScalarNode("value")))
                        throw Invalid($"'{name}' has no `value:`.", Line(obj));
                    // A hand-typed secret would be clear text sitting in the file under a flag
                    // claiming otherwise — and unreadable to every consumer, since resolving it
                    // expects an envelope. Refuse it here rather than let it be committed.
                    if (secretFlag && !SecretEnvelope.IsEnvelope(valueText))
                    {
                        throw Invalid(
                            $"'{name}' is marked `secret: true` but its value is not an {EnvelopePrefix}… envelope. "
                            + "Secrets are encrypted when you set them from the Variables tab (or the API); "
                            + "writing one here would store it in clear.",
                            Line(obj));
                    }
                }

                result[name] = new Entry(valueText, secretFlag);
            }
            else if (strict)
            {
                throw Invalid(
                    $"'{name}' must be a value, or a mapping with `value:` and `secret:`.", Line(v));
            }
        }
        return result;
    }

    /// <summary>1-based line for an error marker; YamlDotNet counts from 1 already, and 0 means
    /// "it didn't say".</summary>
    private static int? Line(YamlNode node) => node.Start.Line > 0 ? (int)node.Start.Line : null;

    private WorkspaceParseException Invalid(string message, int? line) => new(new WorkspaceError(
        WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
        message,
        ".vars/" + Path.GetFileName(_storePath),
        line));

    /// <summary>Comment block every written store carries, and what an unwritten one is shown
    /// as. It is the only documentation someone opening this file in an editor gets.</summary>
    private static string Header =>
        "# Tap file provider store — written by the Studio UI / API.\n"
        + "# Secret values use the envelope: enc:v1:<iv-b64>:<ciphertext-b64>:<tag-b64>\n"
        + $"# Do NOT hand-edit secret values; they are keyed to this machine's {MachineEncryptionKeySource.EnvVar}\n"
        + $"# (or {MachineEncryptionKeySource.FileName}). Committing this file without that key commits nothing usable.\n";

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
        sb.Append(Header);

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
