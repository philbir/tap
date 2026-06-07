namespace Tap.Workspace.Variables;

/// <summary>
/// Whether a provider exposes only reads (<see cref="Read"/>) or also accepts writes
/// (<see cref="ReadWrite"/>). Drives whether the Studio UI offers "set variable" actions
/// against the provider and whether the variable registry will route writes to it.
/// </summary>
public enum ProviderMode
{
    Read,
    ReadWrite,
}
