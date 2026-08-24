using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// One API the Studio was pointed at with <see cref="TapStudioExtensions.WithApi"/>. Recorded so
/// the Studio can scaffold a collection per API on first run; the endpoint plumbing itself is
/// standard <c>WithReference</c>.
/// </summary>
/// <param name="OpenApiRoute">
/// Path to the API's OpenAPI document, relative to its base URL. On first run the Studio fetches
/// it and generates real requests instead of a placeholder. Null opts out.
/// </param>
public sealed record TapStudioApi(string Name, string? OpenApiRoute);

/// <summary>Annotation carrying the APIs a Studio resource was pointed at.</summary>
public sealed class TapStudioAnnotation : IResourceAnnotation
{
    public List<TapStudioApi> Apis { get; } = [];

    /// <summary>
    /// Serialized as objects rather than bare names so each API's OpenAPI route travels with it.
    /// The Studio still accepts the old <c>["orders-api"]</c> array — an AppHost and a Studio can
    /// be different versions during an upgrade.
    /// </summary>
    internal string SerializeApis()
        => JsonSerializer.Serialize(Apis.Select(a => new ApiPayload(a.Name, a.OpenApiRoute)).ToArray());

    private sealed record ApiPayload(string Name, string? OpenApiRoute);

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
    /// <summary>Where <c>Microsoft.AspNetCore.OpenApi</c> serves its document by default, which
    /// every ASP.NET Core project scaffolded since .NET 9 has enabled.</summary>
    public const string DefaultOpenApiRoute = "/openapi/v1.json";


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
    /// <param name="openApi">
    /// Where this API serves its OpenAPI document. Defaults to the path
    /// <c>Microsoft.AspNetCore.OpenApi</c> uses out of the box, so a stock ASP.NET Core project
    /// needs no configuration: on first run the Studio fetches it and scaffolds a request per
    /// operation. Pass null for an API that doesn't publish one — a failed fetch is not an error
    /// either way, it just falls back to a starter request.
    /// </param>
    public TapStudioHandle WithApi<T>(
        IResourceBuilder<T> api, bool waitFor = true, string? openApi = DefaultOpenApiRoute)
        where T : IResource, IResourceWithServiceDiscovery, IResourceWithEndpoints
    {
        As<IResourceWithEnvironment>().WithReference(Facet<IResourceWithServiceDiscovery>(api.Resource));
        if (waitFor) As<IResourceWithWaitSupport>().WaitFor(Facet<IResource>(api.Resource));

        if (!Annotation.Apis.Any(a => string.Equals(a.Name, api.Resource.Name, StringComparison.OrdinalIgnoreCase)))
            Annotation.Apis.Add(new TapStudioApi(api.Resource.Name, openApi));

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

        if (Resource is ContainerResource)
        {
            // A container reaches the workspace through a bind mount, and a mount is part of
            // the resource definition rather than something resolved at start time — so this
            // call has to rewrite it. Drop the previous one first: a second WithWorkspaceFolder
            // would otherwise stack a second source on the same target, which Docker rejects.
            foreach (var stale in Resource.Annotations
                         .OfType<ContainerMountAnnotation>()
                         .Where(m => m.Target == TapStudioExtensions.ContainerWorkspacePath)
                         .ToList())
            {
                Resource.Annotations.Remove(stale);
            }

            // Docker creates a missing bind-mount source itself, owned by root — after which
            // the developer cannot write the workspace the Studio just scaffolded into it.
            Directory.CreateDirectory(Annotation.WorkspaceRoot);

            As<ContainerResource>()
                .WithBindMount(Annotation.WorkspaceRoot, TapStudioExtensions.ContainerWorkspacePath);
        }

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
            ctx.EnvironmentVariables["Studio__Aspire__Apis"] = annotation.SerializeApis();
        });

        return handle;
    }

    /// <summary>Default image for <see cref="AddTapStudioContainer"/>, published by this repo's
    /// <c>docker-publish</c> workflow from <c>src/backend/Tap.Studio/Dockerfile</c>.</summary>
    public const string DefaultImage = "ghcr.io/philbir/tap-studio";

    /// <summary>
    /// The image tag that pairs with this build of the hosting library.
    ///
    /// <para><b>Not <c>latest</c>.</b> The publish workflow only tags <c>latest</c> for stable
    /// releases — any tag without a pre-release suffix — so on a preview <c>latest</c> either
    /// does not exist or points at an older stable image. Defaulting to it would leave
    /// <c>AddTapStudioContainer()</c> failing to pull for exactly the people trying a preview.
    /// The NuGet package and the image are published from the same git tag, so the library's own
    /// version always names an image that exists.</para>
    /// </summary>
    public static string DefaultImageTag { get; } = ResolveDefaultImageTag();

    private static string ResolveDefaultImageTag()
    {
        var informational = typeof(TapStudioExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational)) return "latest";

        // Strip SourceLink's "+<sha>" build metadata — the registry tag is the version alone.
        var plus = informational.IndexOf('+');
        var version = plus >= 0 ? informational[..plus] : informational;

        // A local build ("0.1.0-local", the Directory.Build.props fallback) names no published
        // image, so there is nothing better to try than latest.
        return version.EndsWith("-local", StringComparison.Ordinal) || version.Length == 0
            ? "latest"
            : version;
    }

    /// <summary>Where the image expects the workspace to be mounted. Matches the image's own
    /// <c>Studio__WorkspaceRoot</c> default.</summary>
    internal const string ContainerWorkspacePath = "/workspace";

    /// <summary>Where the image keeps <c>system.json</c>, the known-workspace list, the state DB,
    /// and the OAuth token cache (<c>TAP_SYSTEM_DIR</c>).</summary>
    internal const string ContainerStatePath = "/state";

    /// <summary>The port the image listens on inside the container.</summary>
    internal const int ContainerPort = 8080;

    /// <summary>
    /// Same Studio, hosted as a Docker container instead of a project:
    ///
    /// <code>
    /// var studio = builder.AddTapStudioContainer()
    ///     .WithWorkspaceFolder("tap")
    ///     .WithApi(orders);
    /// </code>
    ///
    /// <para>This is the route that needs nothing installed. <see cref="AddTapStudio"/> compiles
    /// the Studio from source, so it needs a <c>ProjectReference</c> to <c>Tap.Studio</c> and
    /// yarn on PATH to build its React UI; the image already carries both. The trade-off is that
    /// a container is not your machine: the AI assistant cannot spawn the coding CLI you have
    /// installed, an interactive OAuth flow cannot open your browser, and provider back ends that
    /// shell out to a local binary (1Password's <c>op</c>, the Azure CLI) are not there. Use the
    /// project route when you want those; use this one for a workspace that authenticates
    /// headlessly.</para>
    ///
    /// <para>The workspace folder is bind-mounted rather than copied — Studio writes the files it
    /// edits, and they belong in your repository. <paramref name="persistState"/> keeps the token
    /// cache and system settings in a named volume so they survive a restart.</para>
    /// </summary>
    /// <param name="exposeOnAllInterfaces">
    /// Publish the endpoint on every host interface instead of loopback only. Studio reads and
    /// writes the workspace and holds cached tokens, so widening it is a deliberate act — and a
    /// published container port binds every interface unless it is given an explicit host IP.
    /// </param>
    public static TapStudioHandle AddTapStudioContainer(
        this IDistributedApplicationBuilder builder,
        string name = "tap-studio",
        string image = DefaultImage,
        string? tag = null,
        int? port = null,
        ImagePullPolicy imagePullPolicy = ImagePullPolicy.Missing,
        bool persistState = true,
        bool exposeOnAllInterfaces = false)
    {
        var annotation = new TapStudioAnnotation();

        // Missing rather than Always for the same reason AddTapContainer defaults to it: a
        // locally-built tag (tap-studio:local) then Just Works without an unintended registry pull.
        var container = builder.AddContainer(name, image, tag ?? DefaultImageTag)
            .WithImagePullPolicy(imagePullPolicy)
            .WithHttpEndpoint(port: port, targetPort: ContainerPort, name: "http")
            .WithAnnotation(annotation)
            .WithHttpHealthCheck("/health", endpointName: "http")
            .WithUrlForEndpoint("http", url => url.DisplayText = "Tap Studio")
            .WithIconName("PlugConnected")
            .ExcludeFromManifest();

        if (!exposeOnAllInterfaces)
        {
            container.WithEndpoint("http", e => e.TargetHost = "127.0.0.1", createIfNotExists: false);
        }

        if (persistState)
        {
            // Without this the OAuth token cache dies with the container, so every AppHost run
            // starts by asking the developer to sign in again.
            container.WithVolume($"{name}-state", ContainerStatePath);
        }

        var handle = new TapStudioHandle(builder, container.Resource, annotation, container.GetEndpoint("http"));

        // Adds the bind mount as well, because the resource is a container.
        handle.WithWorkspaceFolder(DefaultWorkspaceFolder);

        container.WithEnvironment(ctx =>
        {
            ctx.EnvironmentVariables["Studio__Mode"] = "aspire";
            // The path inside the container, not on the host: the mount is what connects them.
            ctx.EnvironmentVariables["Studio__WorkspaceRoot"] = ContainerWorkspacePath;
            ctx.EnvironmentVariables["Studio__Aspire__Apis"] = annotation.SerializeApis();

            if (!exposeOnAllInterfaces)
            {
                // The image binds the wildcard address — it has to, since a published port
                // arrives on the container's external interface — which leaves it answering any
                // Host header. Publishing on loopback is what makes pinning the allowlist safe,
                // and pinning it is what closes DNS rebinding.
                ctx.EnvironmentVariables["Studio__AllowedHosts"] = "localhost,127.0.0.1,::1";
            }
        });

        return handle;
    }
}
