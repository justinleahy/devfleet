using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PiCommandCenter.Node.Runtime.Muse;

/// <summary>
/// MSP v1 wire constants and the handshake shared by the session adapter and the model catalog
/// reader. Only the stable read-only surface is ever spoken: <c>initialize</c>/<c>initialized</c>,
/// <c>session/start</c>, <c>turn/start</c>, <c>turn/cancel</c>, <c>view/unsubscribe</c>,
/// <c>model/list</c>.
/// </summary>
internal static class MuseProtocol
{
    public const int SchemaVersion = 1;

    /// <summary>Stable-surface fingerprint of Muse Code 1.0.3; a mismatch is a warning, not an error.</summary>
    public const string KnownFingerprint = "sha256:03312c213efd14277a0e0a102f70adeae497a469ca4edf7242f479953ed758b7";

    /// <summary>MSP <c>clientInfo.name</c>; must match <c>[a-z0-9_]+</c>.</summary>
    public const string ClientName = "devfleet";

    public const string ClientTitle = "DevFleet";

    /// <summary>Every unmatched approval is denied; the host is never asked to prompt.</summary>
    public const string ApprovalMode = "denyUnmatched";

    /// <summary>
    /// Read-only host argv. Never <c>--yolo</c>, <c>--disable-sandbox</c>, <c>--api-key-stdin</c>,
    /// or any login/logout/auth subcommand.
    /// </summary>
    public static readonly IReadOnlyList<string> LaunchArguments =
        ["serve", "--disable-write", "--disable-shell", "--no-session-log"];

    /// <summary>UUIDv7 idempotency handle for <c>session/start</c>, <c>turn/start</c>, <c>turn/cancel</c>.</summary>
    public static string NewCommandId() => Guid.CreateVersion7().ToString("D");

    /// <summary>
    /// <c>initialize</c> then the <c>initialized</c> notification. Fails closed on an envelope
    /// schema version other than <see cref="SchemaVersion"/>.
    /// </summary>
    public static async Task<JsonElement> HandshakeAsync(
        MuseHostClient client,
        string clientVersion,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var result = await client.RequestAsync(
            "initialize",
            new { clientInfo = new { name = ClientName, title = ClientTitle, version = clientVersion } },
            timeout,
            cancellationToken).ConfigureAwait(false);

        if (!TryGetObject(result, "schema", out var schema)
            || !schema.TryGetProperty("version", out var version)
            || !version.TryGetInt32(out var schemaVersion))
        {
            throw new MuseProtocolException("Muse host did not report an envelope schema version.");
        }

        if (schemaVersion != SchemaVersion)
        {
            throw new NotSupportedException(
                $"Muse host speaks MSP envelope schema {schemaVersion}; this node supports {SchemaVersion}.");
        }

        var fingerprint = GetString(schema, "fingerprint");
        if (!string.Equals(fingerprint, KnownFingerprint, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Muse host stable-surface fingerprint {Fingerprint} differs from the verified {Known}; continuing on the v1 envelope.",
                fingerprint ?? "(absent)",
                KnownFingerprint);
        }

        await client.NotifyAsync("initialized", null, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }
}
