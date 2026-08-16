using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// One API the Studio was pointed at with <see cref="TapStudioExtensions.WithApi"/>. Recorded so
/// the Studio can scaffold a collection per API on first run; the endpoint plumbing itself is
/// standard <c>WithReference</c>.
/// </summary>
public sealed record TapStudioApi(string Name);

/// <summary>Annotation carrying the APIs a Studio resource was pointed at.</summary>
public sealed class TapStudioAnnotation : IResourceAnnotation
{
    public List<TapStudioApi> Apis { get; } = [];

    /// <summary>Absolute path to the version-controlled workspace folder.</summary>
    public string? WorkspaceRoot { get; set; }
}

/// <summary>
/// Handle returned by <see cref="TapStudioExtensions.AddTapStudio"/>.
///
/// <para>It exists for the same reason <c>TapHandle</c> does, plus one more: the M1 shape here is
/// a project resource, and the packaged distribution will host the very same Studio as an
/// executable. Returning a handle rather than an <c>IResourceBuilder&lt;ProjectResource&gt;</c>
/// means that swap doesn't break a single call site.</para>
/// </summary>
public sealed class TapStudioHandle
{
    internal TapStudioHandle(
        IDistributedApplicationBuilder applicationBuilder,
        IResource resource,
        TapStudioAnnotation annotation,
        EndpointReference endpoint)
    {
        ApplicationBuilder = applicationBuilder;
        Resource = resource;
        Annotation = annotation;
        Endpoint = endpoint;
    }

    public IDistributedApplicationBuilder ApplicationBuilder { get; }
    public IResource Resource { get; }
    public TapStudioAnnotation Annotation { get; }

    /// <summary>The Studio's HTTP endpoint.</summary>
    public EndpointReference Endpoint { get; }

    /// <summary>
    /// Re-wraps the underlying resource as a builder of whichever facet a hosting API needs.
    /// This is what keeps the handle independent of how the Studio is hosted: a project resource
    /// today, an executable in the packaged distribution, and both satisfy these interfaces.
    /// </summary>
    private IResourceBuilder<T> As<T>() where T : IResource
        => ApplicationBuilder.CreateResourceBuilder((T)Resource);

    /// <summary>Re-wraps another resource as the exact builder facet a hosting API expects.
    /// <c>IResourceBuilder&lt;T&gt;</c> is invariant, so an <c>IResourceBuilder&lt;MyApi&gt;</c>
    /// is not an <c>IResourceBuilder&lt;IResourceWithServiceDiscovery&gt;</c> even though
    /// <c>MyApi</c> implements it.</summary>
    private IResourceBuilder<TFacet> Facet<TFacet>(IResource resource) where TFacet : IResource
        => ApplicationBuilder.CreateResourceBuilder((TFacet)resource);

    /// <summary>
    /// The Studio's OAuth redirect URI, for seeding an identity provider's client registration:
    /// <c>identity.WithEnvironment("STUDIO_CALLBACK_URL", studio.CallbackUrl)</c>.
    ///
    /// <para>A <see cref="ReferenceExpression"/> rather than a string because the port isn't
    /// allocated until start time — building it eagerly would bake in whatever the endpoint
    /// looked like during registration, which is nothing.</para>
    /// </summary>
    public ReferenceExpression CallbackUrl =>
        ReferenceExpression.Create($"{Endpoint}/api/auth/callback");

    /// <summary>
    /// Points the Studio at an API: injects the standard service-discovery variables so
    /// <c>{{aspire:&lt;name&gt;}}</c> resolves to its allocated URL, and records it for
    /// first-run scaffolding. Repeatable — one call per API.
    /// </summary>
    /// <param name="waitFor">
    /// Wait for the API to be running before starting the Studio. On by default so the first
    /// request you send actually reaches something; pass false for an API that is slow to start
    /// and not worth blocking the Studio on.
    /// </param>
    public TapStudioHandle WithApi<T>(IResourceBuilder<T> api, bool waitFor = true)
        where T : IResource, IResourceWithServiceDiscovery, IResourceWithEndpoints
    {
        As<IResourceWithEnvironment>().WithReference(Facet<IResourceWithServiceDiscovery>(api.Resource));
        if (waitFor) As<IResourceWithWaitSupport>().WaitFor(Facet<IResource>(api.Resource));

        if (!Annotation.Apis.Any(a => string.Equals(a.Name, api.Resource.Name, StringComparison.OrdinalIgnoreCase)))
            Annotation.Apis.Add(new TapStudioApi(api.Resource.Name));

        return this;
    }

    /// <summary>
    /// The folder holding the workspace, relative to the AppHost project directory (or
    /// absolute). Version-controlled alongside the solution — that is the point of the feature.
    /// </summary>
    public TapStudioHandle WithWorkspaceFolder(string relativeOrAbsolutePath)
    {
        var root = Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(ApplicationBuilder.AppHostDirectory, relativeOrAbsolutePath);

        Annotation.WorkspaceRoot = Path.GetFullPath(root);
        return this;
    }

    public TapStudioHandle WithEnvironment(string name, string value)
    {
        As<IResourceWithEnvironment>().WithEnvironment(name, value);
        return this;
    }

    public TapStudioHandle WithEnvironment(Action<EnvironmentCallbackContext> callback)
    {
        As<IResourceWithEnvironment>().WithEnvironment(callback);
        return this;
    }

    /// <summary>Injects a value only known once endpoints are allocated — another resource's
    /// URL, or a property of one.</summary>
    public TapStudioHandle WithEnvironment(string name, ReferenceExpression value)
    {
        As<IResourceWithEnvironment>().WithEnvironment(name, value);
        return this;
    }

    public TapStudioHandle WithEnvironment(string name, EndpointReference endpoint)
    {
        As<IResourceWithEnvironment>().WithEnvironment(name, endpoint);
        return this;
    }

    public TapStudioHandle WithEnvironment(string name, EndpointReferenceExpression value)
    {
        As<IResourceWithEnvironment>().WithEnvironment(name, value);
        return this;
    }

    /// <summary>Wait for another resource before starting the Studio. <see cref="WithApi"/> does
    /// this for you; this is for everything else (a database, an identity provider).</summary>
    public TapStudioHandle WaitFor<T>(IResourceBuilder<T> other) where T : IResource
    {
        As<IResourceWithWaitSupport>().WaitFor(Facet<IResource>(other.Resource));
        return this;
    }

    /// <summary>Publish the Studio's endpoint beyond loopback. Off by default — the Studio reads
    /// and writes the workspace and holds cached tokens, so exposing it is a deliberate act.</summary>
    public TapStudioHandle WithExternalHttpEndpoints()
    {
        As<IResourceWithEndpoints>().WithExternalHttpEndpoints();
        return this;
    }
}

public static class TapStudioExtensions
{
    /// <summary>Default folder name for the workspace, resolved against the AppHost directory.</summary>
    public const string DefaultWorkspaceFolder = "tap";

    /// <summary>
    /// Runs Tap Studio as a companion resource of this AppHost, pinned to a version-controlled
    /// workspace folder in the solution and pointed at the APIs under development:
    ///
    /// <code>
    /// var studio = builder.AddTapStudio&lt;Projects.Tap_Studio&gt;()
    ///     .WithWorkspaceFolder("tap")
    ///     .WithApi(orders)
    ///     .WithApi(billing);
    /// </code>
    ///
    /// <para><b>Run mode only.</b> The Studio is a development tool; it is excluded from the
    /// manifest and not created at all during publish, so a deployed app never carries it.</para>
    ///
    /// <para><b>Consumer csproj requirements</b> (same rules as <c>AddTap</c>): a plain
    /// <c>ProjectReference</c> to <c>Tap.Studio</c>, which is what makes Aspire's source
    /// generator emit <c>Projects.Tap_Studio</c>, plus a reference to this library with
    /// <c>IsAspireProjectResource="false"</c> because it is a library, not a launchable project.
    /// Building Tap.Studio builds its React UI, so the consumer needs yarn on PATH — the
    /// packaged distribution removes that requirement.</para>
    /// </summary>
    /// <typeparam name="TStudio">
    /// The Tap.Studio project metadata generated in the consumer's AppHost — typically
    /// <c>Projects.Tap_Studio</c>. Aspire's source generator only emits that type inside the
    /// AppHost project, so this library cannot name it and the caller must supply it.
    /// </typeparam>
    public static TapStudioHandle AddTapStudio<TStudio>(
        this IDistributedApplicationBuilder builder,
        string name = "tap-studio")
        where TStudio : IProjectMetadata, new()
    {
        var annotation = new TapStudioAnnotation();

        var project = builder.AddProject<TStudio>(name)
            .WithHttpEndpoint(name: "http", env: "Studio__Port")
            .WithAnnotation(annotation)
            .WithHttpHealthCheck("/health", endpointName: "http")
            .WithUrlForEndpoint("http", url => url.DisplayText = "Tap Studio")
            .WithIconName("PlugConnected")
            .ExcludeFromManifest();

        var handle = new TapStudioHandle(builder, project.Resource, annotation, project.GetEndpoint("http"));

        handle.WithWorkspaceFolder(DefaultWorkspaceFolder);

        // Deferred: the workspace root can still be changed by a WithWorkspaceFolder call after
        // this one, and the API list grows with every WithApi. Reading the annotation at start
        // time rather than here is what lets the builder methods be called in any order.
        project.WithEnvironment(ctx =>
        {
            ctx.EnvironmentVariables["Studio__Mode"] = "aspire";
            ctx.EnvironmentVariables["Studio__WorkspaceRoot"] =
                annotation.WorkspaceRoot ?? Path.Combine(builder.AppHostDirectory, DefaultWorkspaceFolder);
            ctx.EnvironmentVariables["Studio__Aspire__Apis"] =
                JsonSerializer.Serialize(annotation.Apis.Select(a => a.Name).ToArray());
        });

        return handle;
    }

}
