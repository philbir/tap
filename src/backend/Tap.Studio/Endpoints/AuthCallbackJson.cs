using System.Text.Json.Serialization;

namespace Tap.Studio.Endpoints;

/// <summary>
/// Source-generated serializer for the OAuth callback page's <c>postMessage</c> payload.
/// Kept private to <see cref="AuthFlowEndpoints"/> — we want a tiny, reflection-free encoder
/// that always emits the same shape regardless of trim settings.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AuthFlowEndpoints.AuthCallbackPayload))]
[JsonSerializable(typeof(string))]
internal partial class AuthCallbackJson : JsonSerializerContext;
