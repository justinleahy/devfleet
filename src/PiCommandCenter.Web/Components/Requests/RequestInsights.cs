using System.Text.Json;
using PiCommandCenter.Application.Sessions;

namespace PiCommandCenter.Web.Components.Requests;

/// <summary>One numbered step of a submitted plan.</summary>
public sealed record PlanStep(int Ordinal, string Text);

/// <summary>
/// One persisted plan submission or revision, read from a <c>request.phase_changed</c> event
/// whose payload carries phase <c>plan</c> or <c>plan_revision</c>.
/// </summary>
public sealed record PlanRevision(
    long Sequence,
    DateTimeOffset OccurredAt,
    string? SessionId,
    bool IsRevision,
    string? Reason,
    IReadOnlyList<PlanStep> Steps,
    string PayloadJson);

/// <summary>One path in a captured repository diff, with whatever attribution the node recorded.</summary>
public sealed record ChangedPath(
    string Path,
    string? ChangeKind,
    int? LinesAdded,
    int? LinesRemoved,
    string? OwnerSessionId,
    bool? Reserved);

/// <summary>
/// One captured repository diff snapshot (<c>repository.changed</c> or
/// <c>repository.checkpoint_created</c>).
/// </summary>
public sealed record DiffSnapshot(
    long Sequence,
    DateTimeOffset OccurredAt,
    string? SessionId,
    string EventType,
    string? Branch,
    string? BaseCommit,
    string? HeadCommit,
    IReadOnlyList<ChangedPath> Paths,
    string PayloadJson);

/// <summary>One <c>repository.external_change_detected</c> fact: changes no lease accounts for.</summary>
public sealed record ExternalChange(
    long Sequence,
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> Paths,
    string? Detail,
    string PayloadJson);

/// <summary>One <c>reservation.handoff_requested</c> fact that no transfer or release has answered.</summary>
public sealed record HandoffRequest(
    long Sequence,
    DateTimeOffset OccurredAt,
    string? SessionId,
    string? LeaseId,
    string? Reason,
    string PayloadJson);

/// <summary>
/// One persisted completion attempt: either the accepted <c>request.completed</c> fact or an event
/// carrying the gate's <c>missingRequirements</c> rejection codes.
/// </summary>
public sealed record CompletionAttempt(
    long Sequence,
    DateTimeOffset OccurredAt,
    string? SessionId,
    string EventType,
    bool Accepted,
    IReadOnlyList<string> MissingRequirements,
    string PayloadJson);

/// <summary>
/// Everything the request page derives from the persisted event stream: plan, diff, external
/// changes, open handoff requests, and completion attempts. Verification runs and the request
/// result are read from their own stores, not from here.
/// </summary>
/// <remarks>
/// Payload properties are matched case-insensitively so a node writing PascalCase reads the same
/// as one writing camelCase. A payload that omits a property leaves the field null; the raw JSON
/// is always retained so the UI can show exactly what was persisted.
/// </remarks>
public sealed record RequestInsights(
    IReadOnlyList<PlanRevision> PlanRevisions,
    IReadOnlyList<DiffSnapshot> DiffSnapshots,
    IReadOnlyList<ExternalChange> ExternalChanges,
    IReadOnlyList<HandoffRequest> OpenHandoffRequests,
    IReadOnlyList<CompletionAttempt> CompletionAttempts)
{
    /// <summary>Insights of a request with no persisted events.</summary>
    public static readonly RequestInsights Empty = new(
        Array.Empty<PlanRevision>(),
        Array.Empty<DiffSnapshot>(),
        Array.Empty<ExternalChange>(),
        Array.Empty<HandoffRequest>(),
        Array.Empty<CompletionAttempt>());

    /// <summary>The plan in force: the newest submission or revision.</summary>
    public PlanRevision? CurrentPlan => PlanRevisions.Count == 0 ? null : PlanRevisions[^1];

    /// <summary>The newest captured diff snapshot.</summary>
    public DiffSnapshot? LatestDiff => DiffSnapshots.Count == 0 ? null : DiffSnapshots[^1];

    /// <summary>The newest external-change detection, which blocks completion until answered.</summary>
    public ExternalChange? LatestExternalChange =>
        ExternalChanges.Count == 0 ? null : ExternalChanges[^1];

    /// <summary>The newest rejected completion attempt, if one is persisted.</summary>
    public CompletionAttempt? LatestRejection
    {
        get
        {
            for (var index = CompletionAttempts.Count - 1; index >= 0; index--)
            {
                if (!CompletionAttempts[index].Accepted)
                {
                    return CompletionAttempts[index];
                }
            }

            return null;
        }
    }
}

/// <summary>
/// Reduces the persisted session-event stream of one request into <see cref="RequestInsights"/>.
/// The reducer is total: an event whose payload is missing, malformed, or shaped differently than
/// expected still contributes its type, time, and raw JSON, and never invents a value.
/// </summary>
public static class RequestInsightsReader
{
    private const string PhaseChangedType = "request.phase_changed";
    private const string PlanPhase = "plan";
    private const string PlanRevisionPhase = "plan_revision";

    /// <summary>Reduces events (oldest first, as the store returns them) into the page's read model.</summary>
    public static RequestInsights Read(IReadOnlyList<SessionEventDto> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return RequestInsights.Empty;
        }

        var plans = new List<PlanRevision>();
        var diffs = new List<DiffSnapshot>();
        var external = new List<ExternalChange>();
        var handoffs = new List<HandoffRequest>();
        var completions = new List<CompletionAttempt>();

        foreach (var evt in events)
        {
            var type = evt.Type;
            using var payload = Parse(evt.PayloadJson);
            var data = payload?.RootElement is { ValueKind: JsonValueKind.Object } element
                ? element
                : (JsonElement?)null;

            if (string.Equals(type, PhaseChangedType, StringComparison.Ordinal))
            {
                var phase = Text(data, "phase");
                var isRevision = string.Equals(phase, PlanRevisionPhase, StringComparison.OrdinalIgnoreCase);
                if (isRevision || string.Equals(phase, PlanPhase, StringComparison.OrdinalIgnoreCase))
                {
                    plans.Add(new PlanRevision(
                        evt.Sequence,
                        evt.OccurredAt,
                        evt.SessionId,
                        isRevision,
                        Text(data, "reason"),
                        ReadSteps(data),
                        evt.PayloadJson));
                }

                continue;
            }

            if (IsDiffSnapshot(type))
            {
                diffs.Add(new DiffSnapshot(
                    evt.Sequence,
                    evt.OccurredAt,
                    evt.SessionId,
                    type,
                    Text(data, "branch"),
                    Text(data, "baseCommit", "base"),
                    Text(data, "headCommit", "head"),
                    ReadChangedPaths(data),
                    evt.PayloadJson));
                continue;
            }

            switch (type)
            {
                case "repository.external_change_detected":
                    external.Add(new ExternalChange(
                        evt.Sequence,
                        evt.OccurredAt,
                        ReadStrings(data, "paths", "files"),
                        Text(data, "detail", "reason"),
                        evt.PayloadJson));
                    continue;

                case "reservation.handoff_requested":
                    handoffs.Add(new HandoffRequest(
                        evt.Sequence,
                        evt.OccurredAt,
                        evt.SessionId,
                        Text(data, "leaseId"),
                        Text(data, "reason"),
                        evt.PayloadJson));
                    continue;

                case "reservation.transferred":
                case "reservation.released":
                case "reservation.force_released":
                    CloseHandoff(handoffs, Text(data, "leaseId"));
                    continue;
            }

            var missing = ReadStrings(data, "missingRequirements");
            var accepted = string.Equals(type, "request.completed", StringComparison.Ordinal);
            if (accepted || missing.Count > 0)
            {
                completions.Add(new CompletionAttempt(
                    evt.Sequence,
                    evt.OccurredAt,
                    evt.SessionId,
                    type,
                    accepted && missing.Count == 0,
                    missing,
                    evt.PayloadJson));
            }
        }

        return new RequestInsights(plans, diffs, external, handoffs, completions);
    }

    /// <summary>
    /// Drops the open handoff request for a lease once a transfer or release answered it. A
    /// payload without a lease id closes nothing rather than guessing which request was answered.
    /// </summary>
    private static void CloseHandoff(List<HandoffRequest> handoffs, string? leaseId)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
        {
            return;
        }

        for (var index = handoffs.Count - 1; index >= 0; index--)
        {
            if (string.Equals(handoffs[index].LeaseId, leaseId, StringComparison.OrdinalIgnoreCase))
            {
                handoffs.RemoveAt(index);
            }
        }
    }

    private static bool IsDiffSnapshot(string type) =>
        string.Equals(type, "repository.changed", StringComparison.Ordinal)
        || string.Equals(type, "repository.checkpoint_created", StringComparison.Ordinal);

    private static IReadOnlyList<PlanStep> ReadSteps(JsonElement? data)
    {
        if (!TryArray(data, out var array, "steps"))
        {
            return Array.Empty<PlanStep>();
        }

        var steps = new List<PlanStep>(array.GetArrayLength());
        var ordinal = 0;
        foreach (var item in array.EnumerateArray())
        {
            var text = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => Text(item, "text", "title") ?? Text(item, "summary", "description"),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            steps.Add(new PlanStep(++ordinal, text));
        }

        return steps;
    }

    private static IReadOnlyList<ChangedPath> ReadChangedPaths(JsonElement? data)
    {
        if (!TryArray(data, out var array, "paths", "changedFiles") && !TryArray(data, out array, "files"))
        {
            return Array.Empty<ChangedPath>();
        }

        var paths = new List<ChangedPath>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    paths.Add(new ChangedPath(value, null, null, null, null, null));
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = Text(item, "path", "file");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            paths.Add(new ChangedPath(
                path,
                Text(item, "changeKind", "status"),
                Integer(item, "linesAdded", "added"),
                Integer(item, "linesRemoved", "removed"),
                Text(item, "ownerSessionId", "owner"),
                Boolean(item, "reserved", "attributed")));
        }

        return paths;
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement? data, params string[] names)
    {
        if (!TryArray(data, out var array, names))
        {
            return Array.Empty<string>();
        }

        var values = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            var text = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object => Text(item, "path", "requirement") ?? Text(item, "name", "summary"),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text);
            }
        }

        return values;
    }

    private static JsonDocument? Parse(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryArray(JsonElement? data, out JsonElement array, params string[] names)
    {
        if (data is { } element && TryProperty(element, names, out var value)
            && value.ValueKind == JsonValueKind.Array)
        {
            array = value;
            return true;
        }

        array = default;
        return false;
    }

    private static string? Text(JsonElement? data, params string[] names) =>
        data is { } element ? Text(element, names) : null;

    private static string? Text(JsonElement element, params string[] names)
    {
        if (!TryProperty(element, names, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    private static int? Integer(JsonElement element, params string[] names) =>
        TryProperty(element, names, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? Boolean(JsonElement element, params string[] names) =>
        TryProperty(element, names, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>
    /// Finds the first of <paramref name="names"/> present on the object, matching exactly first
    /// and then case-insensitively so PascalCase and camelCase payloads read identically.
    /// </summary>
    private static bool TryProperty(JsonElement element, string[] names, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out value))
                {
                    return true;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }
}
