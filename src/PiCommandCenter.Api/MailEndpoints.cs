using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Api;

/// <summary>
/// Mail and session endpoints (SPEC §16, §30.3, §30.4): request thread history, human guidance
/// to root/specific/all targets, session-directed messages and cancellation, and message
/// acknowledgement. Delivered messages are live-routed through <see cref="INativeApiRealtimeGateway"/>
/// to any node currently hosting a recipient session.
/// </summary>
internal static class MailEndpoints
{
    /// <summary>
    /// Maps the mail routes relative to <paramref name="group"/>; <paramref name="locationPrefix"/>
    /// is the group's absolute prefix, used only to build <c>Location</c> headers.
    /// </summary>
    public static void MapMailEndpoints(this RouteGroupBuilder group, string locationPrefix)
    {
        var sessions = group.MapGroup("/sessions").WithTags("Sessions");
        sessions.MapPost("/{sessionId}/message", SendSessionMessageAsync);
        sessions.MapPost("/{sessionId}/cancel", (
            string sessionId,
            [FromBody] CancelSessionRequest? body,
            IAgentSessionStore sessionStore,
            INativeApiRealtimeGateway realtime,
            CancellationToken cancellationToken) =>
            CancelSessionAsync(sessionId, body, sessionStore, realtime, locationPrefix, cancellationToken));

        var messages = group.MapGroup("/messages").WithTags("Messages");
        messages.MapPost("/{messageId}/acknowledge", AcknowledgeAsync);

        var requests = group.MapGroup("/requests/{requestId:guid}").WithTags("Messages");
        requests.MapGet("/messages", ListThreadAsync);
        requests.MapPost("/messages", SendAsync);
        requests.MapPost("/reply", ReplyAsync);
        requests.MapPost("/guidance", SendGuidanceAsync);
    }

    /// <summary>GET {prefix}/requests/{requestId}/messages?projectId=…[&amp;threadId=…]</summary>
    private static async Task<Results<Ok<MessageListResponse>, BadRequest<ProblemDetails>>> ListThreadAsync(
        Guid requestId,
        [FromQuery] Guid projectId,
        [FromQuery] string? threadId,
        IMessageService messages,
        CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
        {
            return TypedResults.BadRequest(Problem("Invalid request", "A projectId query parameter is required."));
        }

        var thread = await messages.GetThreadAsync(
            new ProjectId(projectId),
            threadId ?? requestId.ToString(),
            cancellationToken);
        return TypedResults.Ok(new MessageListResponse(thread));
    }

    /// <summary>POST {prefix}/requests/{requestId}/messages</summary>
    private static async Task<Results<Ok<AgentMessageDto>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>>> SendAsync(
        Guid requestId,
        [FromBody] SendAgentMessageRequest body,
        IMessageService messages,
        INativeApiRealtimeGateway realtime,
        CancellationToken cancellationToken)
    {
        if (!body.TryValidate(out var error, out var importance))
        {
            return TypedResults.BadRequest(Problem("Invalid request", error));
        }

        try
        {
            var delivered = await messages.SendAsync(new SendAgentMessageCommand(
                new ProjectId(body.ProjectId),
                new WorkRequestId(requestId),
                string.IsNullOrWhiteSpace(body.ThreadId) ? requestId.ToString() : body.ThreadId,
                body.SenderSessionId,
                body.Recipients,
                body.Subject,
                body.BodyMarkdown,
                importance,
                body.AckRequired), cancellationToken);

            await realtime.RouteMailAsync(delivered, cancellationToken);
            return TypedResults.Ok(delivered);
        }
        catch (MailSessionNotFoundException ex)
        {
            return TypedResults.NotFound(Problem("Session not found", ex.Message));
        }
        catch (MailValidationException ex)
        {
            return TypedResults.BadRequest(Problem("Invalid request", ex.Message));
        }
    }

    /// <summary>
    /// POST {prefix}/requests/{requestId}/reply — explicit reply operation (SPEC §16.3): recipients
    /// are derived from the thread's participants, excluding the replying session.
    /// </summary>
    private static async Task<Results<Ok<AgentMessageDto>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>>> ReplyAsync(
        Guid requestId,
        [FromBody] ReplyAgentMessageRequest body,
        IMessageService messages,
        INativeApiRealtimeGateway realtime,
        CancellationToken cancellationToken)
    {
        if (body.ProjectId == Guid.Empty
            || string.IsNullOrWhiteSpace(body.SenderSessionId)
            || string.IsNullOrWhiteSpace(body.BodyMarkdown))
        {
            return TypedResults.BadRequest(Problem("Invalid request", "ProjectId, SenderSessionId, and BodyMarkdown are required."));
        }

        try
        {
            var delivered = await messages.ReplyAsync(new ReplyAgentMessageCommand(
                new ProjectId(body.ProjectId),
                string.IsNullOrWhiteSpace(body.ThreadId) ? requestId.ToString() : body.ThreadId,
                body.SenderSessionId,
                body.BodyMarkdown,
                ParseImportance(body.Importance),
                body.AckRequired), cancellationToken);

            await realtime.RouteMailAsync(delivered, cancellationToken);
            return TypedResults.Ok(delivered);
        }
        catch (MailThreadNotFoundException ex)
        {
            return TypedResults.NotFound(Problem("Thread not found", ex.Message));
        }
        catch (MailSessionNotFoundException ex)
        {
            return TypedResults.NotFound(Problem("Session not found", ex.Message));
        }
        catch (MailValidationException ex)
        {
            return TypedResults.BadRequest(Problem("Invalid request", ex.Message));
        }
    }

    /// <summary>
    /// POST {prefix}/requests/{requestId}/guidance — high-priority human guidance (SPEC §16.5).
    /// Target "root" reaches the request's root session, "all" reaches every active session,
    /// and any other value must name one session exactly.
    /// </summary>
    private static async Task<Results<Ok<GuidanceDeliveryResponse>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>>> SendGuidanceAsync(
        Guid requestId,
        [FromBody] SendGuidanceRequest body,
        IMessageService messages,
        IAgentSessionStore sessions,
        INativeApiRealtimeGateway realtime,
        CancellationToken cancellationToken)
    {
        if (body.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(body.BodyMarkdown))
        {
            return TypedResults.BadRequest(Problem("Invalid request", "ProjectId and BodyMarkdown are required."));
        }

        var target = body.Target?.Trim().ToLowerInvariant() ?? "root";
        var requestSessions = await sessions.ListAsync(new WorkRequestId(requestId), cancellationToken);
        var recipients = target switch
        {
            "root" => requestSessions.Where(s => s.ParentSessionId is null).Select(s => s.Id).ToList(),
            "all" => requestSessions.Select(s => s.Id).ToList(),
            _ => requestSessions.Select(s => s.Id)
                .Where(id => string.Equals(id, body.Target, StringComparison.Ordinal))
                .ToList(),
        };

        if (recipients.Count == 0)
        {
            return TypedResults.NotFound(Problem(
                "Guidance target not found",
                $"No active session matches guidance target '{body.Target}' for request '{requestId}'."));
        }

        try
        {
            var delivered = await messages.SendAsync(new SendAgentMessageCommand(
                new ProjectId(body.ProjectId),
                new WorkRequestId(requestId),
                string.IsNullOrWhiteSpace(body.ThreadId) ? requestId.ToString() : body.ThreadId,
                SenderSessionId: null,
                recipients,
                string.IsNullOrWhiteSpace(body.Subject) ? $"[guidance] {requestId}" : body.Subject,
                body.BodyMarkdown,
                MessageImportance.High,
                AckRequired: true), cancellationToken);

            await realtime.RouteMailAsync(delivered, cancellationToken);
            return TypedResults.Ok(new GuidanceDeliveryResponse(delivered.Id, delivered.ThreadId, recipients));
        }
        catch (MailValidationException ex)
        {
            return TypedResults.BadRequest(Problem("Invalid request", ex.Message));
        }
        catch (MailSessionNotFoundException ex)
        {
            return TypedResults.NotFound(Problem("Session not found", ex.Message));
        }
    }

    /// <summary>POST {prefix}/sessions/{sessionId}/message — a direct message to one session from the human user.</summary>
    private static async Task<Results<Ok<AgentMessageDto>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>>> SendSessionMessageAsync(
        string sessionId,
        [FromBody] SendSessionMessageRequest body,
        IMessageService messages,
        INativeApiRealtimeGateway realtime,
        CancellationToken cancellationToken)
    {
        if (body.ProjectId == Guid.Empty || body.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(body.BodyMarkdown))
        {
            return TypedResults.BadRequest(Problem("Invalid request", "ProjectId, RequestId, and BodyMarkdown are required."));
        }

        try
        {
            var delivered = await messages.SendAsync(new SendAgentMessageCommand(
                new ProjectId(body.ProjectId),
                new WorkRequestId(body.RequestId),
                string.IsNullOrWhiteSpace(body.ThreadId) ? body.RequestId.ToString() : body.ThreadId,
                SenderSessionId: null,
                [sessionId],
                string.IsNullOrWhiteSpace(body.Subject) ? $"[message] {body.RequestId}" : body.Subject,
                body.BodyMarkdown,
                ParseImportance(body.Importance),
                body.AckRequired), cancellationToken);

            await realtime.RouteMailAsync(delivered, cancellationToken);
            return TypedResults.Ok(delivered);
        }
        catch (MailSessionNotFoundException ex)
        {
            return TypedResults.NotFound(Problem("Session not found", ex.Message));
        }
        catch (MailValidationException ex)
        {
            return TypedResults.BadRequest(Problem("Invalid request", ex.Message));
        }
    }

    /// <summary>
    /// POST {prefix}/sessions/{sessionId}/cancel — dispatches a real cancellation command to the
    /// node currently running the session (SPEC §23.2). The session's projection is updated
    /// only by the node's actual <c>session.cancelled</c> event, never synthesized here.
    /// </summary>
    private static async Task<Results<Accepted<SessionCancellationResponse>, NotFound<ProblemDetails>>> CancelSessionAsync(
        string sessionId,
        CancelSessionRequest? body,
        IAgentSessionStore sessions,
        INativeApiRealtimeGateway realtime,
        string locationPrefix,
        CancellationToken cancellationToken)
    {
        var existing = await sessions.GetAsync(sessionId, cancellationToken);
        if (existing is null)
        {
            return TypedResults.NotFound(Problem("Session not found", $"No session '{sessionId}' exists."));
        }

        var reason = body?.Reason;
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "cancelled-by-user";
        }

        await realtime.CancelSessionAsync(sessionId, reason, cancellationToken);
        return TypedResults.Accepted($"{locationPrefix}/sessions/{sessionId}", new SessionCancellationResponse(sessionId, reason));
    }

    /// <summary>POST {prefix}/messages/{messageId}/acknowledge</summary>
    private static async Task<Results<Ok<AgentMessageDto>, NotFound<ProblemDetails>, Conflict<ProblemDetails>, BadRequest<ProblemDetails>>> AcknowledgeAsync(
        string messageId,
        [FromBody] AcknowledgeMessageRequest body,
        IMessageService messages,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.SessionId))
        {
            return TypedResults.BadRequest(Problem("Invalid request", "SessionId is required."));
        }

        try
        {
            var delivered = await messages.AcknowledgeAsync(messageId, body.SessionId, cancellationToken);
            return TypedResults.Ok(delivered);
        }
        catch (MailMessageNotFoundException ex)
        {
            return TypedResults.NotFound(Problem("Message not found", ex.Message));
        }
        catch (MailNotAddresseeException ex)
        {
            return TypedResults.NotFound(Problem("Message not found", ex.Message));
        }
        catch (MailAcknowledgementRequiresReadException ex)
        {
            return TypedResults.Conflict(Problem("Acknowledgement requires read", ex.Message));
        }
    }

    /// <summary>"high" (case-insensitive) is high importance; anything else, including null, is normal.</summary>
    private static MessageImportance ParseImportance(string? importance) =>
        string.Equals(importance, "high", StringComparison.OrdinalIgnoreCase)
            ? MessageImportance.High
            : MessageImportance.Normal;

    /// <summary>
    /// Mail problem bodies always carry <c>Status = 400</c> regardless of the HTTP status, matching
    /// the envelope the legacy <c>/api</c> routes have always produced.
    /// </summary>
    private static ProblemDetails Problem(string title, string detail) =>
        ApiProblems.Problem(StatusCodes.Status400BadRequest, title, detail);
}

/// <summary>Response envelope for <c>GET {prefix}/requests/{requestId}/messages</c>.</summary>
public sealed record MessageListResponse(IReadOnlyList<AgentMessageDto> Messages);

/// <summary>Request body for <c>POST {prefix}/requests/{requestId}/messages</c>.</summary>
internal sealed record SendAgentMessageRequest(
    Guid ProjectId,
    string? SenderSessionId,
    IReadOnlyList<string> Recipients,
    string Subject,
    string BodyMarkdown,
    string? Importance,
    bool AckRequired,
    string? ThreadId)
{
    internal bool TryValidate(out string error, out MessageImportance importance)
    {
        error = "";
        importance = MessageImportance.Normal;
        if (ProjectId == Guid.Empty)
        {
            error = "ProjectId is required.";
            return false;
        }

        if (Recipients is not { Count: > 0 })
        {
            error = "At least one recipient is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Subject) || string.IsNullOrWhiteSpace(BodyMarkdown))
        {
            error = "Subject and BodyMarkdown are required.";
            return false;
        }

        if (string.Equals(Importance, "high", StringComparison.OrdinalIgnoreCase))
        {
            importance = MessageImportance.High;
        }
        else if (!string.IsNullOrWhiteSpace(Importance)
            && !string.Equals(Importance, "normal", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unknown importance '{Importance}'.";
            return false;
        }

        return true;
    }
}

/// <summary>Request body for <c>POST {prefix}/requests/{requestId}/reply</c>.</summary>
internal sealed record ReplyAgentMessageRequest(
    Guid ProjectId,
    string SenderSessionId,
    string BodyMarkdown,
    string? Importance,
    bool AckRequired,
    string? ThreadId);

/// <summary>Request body for <c>POST {prefix}/requests/{requestId}/guidance</c>.</summary>
internal sealed record SendGuidanceRequest(
    Guid ProjectId,
    string Target,
    string? Subject,
    string BodyMarkdown,
    string? ThreadId);

/// <summary>Response for <c>POST {prefix}/requests/{requestId}/guidance</c>.</summary>
public sealed record GuidanceDeliveryResponse(string MessageId, string ThreadId, IReadOnlyList<string> Recipients);

/// <summary>Request body for <c>POST {prefix}/sessions/{sessionId}/message</c>.</summary>
internal sealed record SendSessionMessageRequest(
    Guid ProjectId,
    Guid RequestId,
    string Subject,
    string BodyMarkdown,
    string? Importance,
    bool AckRequired,
    string? ThreadId);

/// <summary>Request body for <c>POST {prefix}/sessions/{sessionId}/cancel</c>.</summary>
public sealed record CancelSessionRequest(string? Reason);

/// <summary>Response for <c>POST {prefix}/sessions/{sessionId}/cancel</c>: the command was dispatched.</summary>
public sealed record SessionCancellationResponse(string SessionId, string Reason);

/// <summary>Request body for <c>POST {prefix}/messages/{messageId}/acknowledge</c>.</summary>
internal sealed record AcknowledgeMessageRequest(string SessionId);
