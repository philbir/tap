namespace Tap.Workspace.Variables;

/// <summary>
/// Static, user-facing metadata for one provider <c>type</c> (<c>env</c>, <c>file</c>,
/// <c>azkv</c>, …). Owned by the <see cref="IVariableProviderFactory"/> so the backend stays
/// the single source of truth for what a type is called, how it should be pictured, and
/// which settings it understands. The Studio UI renders its provider picker and its typed
/// settings form purely from this descriptor — adding a provider type requires no UI change.
///
/// <para>The descriptor is a presentation/validation layer only: persisted configs keep the
/// untyped <see cref="VariableProviderConfig.Settings"/> bag, so descriptors can gain or
/// reorder fields without a storage migration. Setting keys not covered by
/// <see cref="Fields"/> are preserved and surfaced by the UI as "advanced" entries.</para>
/// </summary>
public sealed record ProviderTypeDescriptor
{
    /// <summary>The <c>type:</c> identifier used in configuration. Mirrors
    /// <see cref="IVariableProviderFactory.Type"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Human-readable name shown in pickers (e.g. <c>Azure Key Vault</c>).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Semantic icon key the UI maps to an actual glyph (<c>azure</c>,
    /// <c>terminal</c>, <c>file</c>, <c>settings</c>). Kept abstract so the backend doesn't
    /// depend on any icon library.</summary>
    public required string Icon { get; init; }

    /// <summary>One-line description shown under the display name in the picker.</summary>
    public required string Description { get; init; }

    /// <summary>Static read/write capability of the provider type. Mode is not configurable
    /// per instance — see <see cref="VariableProviderConfig"/>.</summary>
    public required ProviderMode Mode { get; init; }

    /// <summary>The settings this type understands, in display order. Empty when the type
    /// takes no per-instance settings (env, system).</summary>
    public IReadOnlyList<ProviderSettingField> Fields { get; init; } = [];

    /// <summary>Keys of fields whose values must be masked (<c>***</c>) in any payload that
    /// leaves the server. Derived from <see cref="ProviderFieldKind.Secret"/> fields.</summary>
    public IEnumerable<string> SecretFieldKeys =>
        Fields.Where(f => f.Kind == ProviderFieldKind.Secret).Select(f => f.Key);
}

/// <summary>One typed setting on a provider type. <see cref="Key"/> is the key stored in
/// the settings bag; the rest drives the generated form field.</summary>
public sealed record ProviderSettingField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public ProviderFieldKind Kind { get; init; } = ProviderFieldKind.Text;

    /// <summary>Whether the provider refuses to instantiate without this setting. Enforced
    /// on save by the Studio endpoints and shown as a required marker in the form.</summary>
    public bool Required { get; init; }

    public string? Placeholder { get; init; }

    /// <summary>Optional picker key the UI attaches to this field — e.g.
    /// <c>"azure-keyvault"</c> renders a browse button that opens the subscription → vault
    /// selector and writes the chosen vault back into the field. Null = plain input.</summary>
    public string? Picker { get; init; }

    /// <summary>Choices for a <see cref="ProviderFieldKind.Select"/> field, in display order.
    /// Ignored for every other kind.</summary>
    public IReadOnlyList<ProviderFieldOption> Options { get; init; } = [];

    /// <summary>Value the UI shows when the settings bag has nothing for this key. Only the
    /// user's own edit is ever persisted — a default is a display convenience, never a
    /// silent write.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>Renders this field only while another setting holds one of the listed values.
    /// Lets a type present a mode switch that reveals just the inputs that mode uses, instead
    /// of showing every setting at once and letting the user guess which apply.</summary>
    public ProviderFieldVisibility? VisibleWhen { get; init; }

    /// <summary>Optional guidance rendered under the input — a prerequisite, a version floor,
    /// a link to the tool's install page. Kept on the descriptor so the UI stays generic.</summary>
    public ProviderFieldNote? Note { get; init; }
}

/// <summary>One choice on a <see cref="ProviderFieldKind.Select"/> field. <see cref="Value"/>
/// is what lands in the settings bag; the rest drives the dropdown row.</summary>
public sealed record ProviderFieldOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
}

/// <summary>"Show this field while <see cref="Key"/> is one of <see cref="Values"/>." The
/// referenced field's own default applies when the settings bag has no entry for it.</summary>
public sealed record ProviderFieldVisibility
{
    public required string Key { get; init; }
    public required IReadOnlyList<string> Values { get; init; }
}

/// <summary>A short note under a field, optionally carrying one link.</summary>
public sealed record ProviderFieldNote
{
    public required string Text { get; init; }
    public string? Url { get; init; }
    public string? UrlLabel { get; init; }
}

/// <summary>How a setting field is rendered and treated on the wire. <see cref="Secret"/>
/// fields render as password inputs and are masked in server responses;
/// <see cref="Select"/> fields render as a dropdown over <see cref="ProviderSettingField.Options"/>.</summary>
public enum ProviderFieldKind
{
    Text,
    Secret,
    Select,
}
