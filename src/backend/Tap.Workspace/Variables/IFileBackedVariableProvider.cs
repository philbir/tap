namespace Tap.Workspace.Variables;

/// <summary>
/// A provider whose whole state is one file a person could reasonably open in an editor.
/// Lets a host offer raw-source editing over the same store the typed surface writes, without
/// knowing which concrete provider it is holding.
///
/// <para>Implementing this is a statement that the file <b>is</b> the store — not a cache of
/// one, and not a config pointing at a remote. A provider that fetches from elsewhere must not
/// implement it: hand-editing its file would either be discarded or would silently disagree
/// with the source of truth.</para>
/// </summary>
public interface IFileBackedVariableProvider
{
    /// <summary>Absolute path of the backing file. It need not exist yet — a provider that has
    /// never been written to still knows where it would write.</summary>
    string StorePath { get; }

    /// <summary>The file's text, or the skeleton an empty store would be written as when it
    /// isn't there yet. Never throws for a missing file: "nothing stored" is a valid state and
    /// the editor should open on something rather than on an error.</summary>
    string ReadSource();

    /// <summary>Validates <paramref name="text"/> against the store format and writes it.
    /// Throws <see cref="Tap.Workspace.Model.WorkspaceParseException"/> — leaving the file
    /// exactly as it was — for anything the provider could not read back.</summary>
    void WriteSource(string text);
}
