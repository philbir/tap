using System.Security.Cryptography;
using System.Text;
using Tap.Workspace.Model;
using YamlDotNet.RepresentationModel;

namespace Tap.Workspace.Variables.Providers;

/// <summary>
/// Read-write provider that persists variables to a dedicated YAML file under the workspace:
/// <c>.tap/.vars/&lt;provider-name&gt;.yml</c>. Each value can be marked <c>secret</c>; secret
/// values are encrypted at rest using AES-256-GCM with a key derived (PBKDF2-HMAC-SHA256)
/// from a shared passphrase declared in the provider's settings.
///
/// <para>Settings consumed:</para>
/// <list type="bullet">
///   <item><c>encryptionKey</c> — passphrase used to derive the AES key. Required when storing
///     any <c>secret: true</c> variable; if absent and a secret write is attempted, the
///     provider throws. Plain (non-secret) values are still stored when the key is absent.</item>
/// </list>
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
    /// envelope on decrypt failure instead of throwing).</summary>
    public const string EnvelopePrefix = "enc:v1:";
    private const int Pbkdf2Iterations = 200_000;
    private const int KeyBytes = 32;
    private const int IvBytes = 12;
    private const int TagBytes = 16;

    private readonly string _storePath;
    private readonly byte[]? _key;
    private readonly Lock _gate = new();
    private VariableProviderConfig _config;

    public FileVariableProvider(VariableProviderConfig config, string workspaceRoot)
    {
        _config = config;
        _storePath = Path.Combine(workspaceRoot, ".vars", config.Name + ".yml");

        // Encryption key is optional at construction — it's only required if a write touches
        // a secret. Reads return ciphertext-on-failure so the operator can fix the key.
        if (config.Settings.TryGetValue("encryptionKey", out var passphrase) && !string.IsNullOrEmpty(passphrase))
        {
            var salt = Encoding.UTF8.GetBytes("tap-file-provider:" + config.Name);
            _key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyBytes);
        }
    }

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
        if (isSecret && _key is null)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                $"File provider '{Name}' has no 'encryptionKey' set — cannot store secret variable '{name}'."));
        }

        lock (_gate)
        {
            var store = LoadStore();
            var stored = isSecret ? Encrypt(value) : value;
            store[name] = new Entry(stored, isSecret);
            SaveStore(store);
        }
        return ValueTask.CompletedTask;
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
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);

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
        sb.Append("# Do NOT hand-edit secret values; rotate the passphrase via the provider settings.\n");

        var doc = new YamlDocument(root);
        var stream = new YamlStream(doc);
        using var writer = new StringWriter(sb);
        stream.Save(writer, assignAnchors: false);
        var text = sb.ToString().Replace("---\r\n", string.Empty).Replace("---\n", string.Empty);
        var dotIdx = text.IndexOf("\n...", StringComparison.Ordinal);
        if (dotIdx > 0) text = text[..dotIdx] + "\n";

        File.WriteAllText(_storePath, text);
    }

    private string Encrypt(string clear)
    {
        if (_key is null) throw new InvalidOperationException("Encryption key not configured.");
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var plain = Encoding.UTF8.GetBytes(clear);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(iv, plain, cipher, tag);
        return EnvelopePrefix
            + Convert.ToBase64String(iv) + ":"
            + Convert.ToBase64String(cipher) + ":"
            + Convert.ToBase64String(tag);
    }

    private string Decrypt(string stored, string name)
    {
        if (!stored.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_DECRYPT_FAILED,
                $"File provider '{Name}' variable '{name}' is marked secret but the stored value is not in the v1 envelope."));
        }
        if (_key is null)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_CONFIG_INVALID,
                $"File provider '{Name}' has no encryptionKey set, cannot decrypt secret variable '{name}'."));
        }
        var parts = stored[EnvelopePrefix.Length..].Split(':');
        if (parts.Length != 3)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_DECRYPT_FAILED,
                $"File provider '{Name}' variable '{name}' has a malformed envelope."));
        }
        try
        {
            var iv = Convert.FromBase64String(parts[0]);
            var cipher = Convert.FromBase64String(parts[1]);
            var tag = Convert.FromBase64String(parts[2]);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(iv, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            throw new WorkspaceParseException(new WorkspaceError(
                WorkspaceErrorCode.E_PROVIDER_DECRYPT_FAILED,
                $"File provider '{Name}' could not decrypt variable '{name}': {ex.Message}"));
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
        Description = "Stores variables in a YAML file under the workspace; secret values are encrypted with a passphrase.",
        Mode = ProviderMode.ReadWrite,
        Fields =
        [
            new ProviderSettingField
            {
                Key = "encryptionKey",
                Label = "Encryption passphrase",
                Description = "Used to derive the AES key for secret values. Plain values work without it; storing a secret requires it.",
                Kind = ProviderFieldKind.Secret,
            },
        ],
    };

    public IVariableProvider Create(VariableProviderConfig config, ProviderFactoryContext context)
        => new FileVariableProvider(config, context.WorkspaceRoot);
}
