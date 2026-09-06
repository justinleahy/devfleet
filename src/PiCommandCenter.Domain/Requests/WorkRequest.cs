namespace PiCommandCenter.Domain.Requests;

/// <summary>
/// A queued unit of work. Constructed only through <see cref="WorkRequest.Enqueue"/> or
/// rehydration via <see cref="WorkRequest.Rehydrate"/> so invalid state is unrepresentable.
/// </summary>
public sealed class WorkRequest
{
    private WorkRequest(
        WorkRequestId id,
        ProjectId projectId,
        WorkRequestKind kind,
        RequestPriority priority,
        RiskLevel riskLevel,
        string title,
        string prompt,
        WorkRequestStatus status,
        WorkRequestStatus? blockedPhase,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version,
        WorkRequestId? originalRequestId)
    {
        Id = id;
        ProjectId = projectId;
        Kind = kind;
        Priority = priority;
        RiskLevel = riskLevel;
        Title = title;
        Prompt = prompt;
        Status = status;
        BlockedPhase = blockedPhase;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
        OriginalRequestId = originalRequestId;
    }

    public WorkRequestId Id { get; }

    public ProjectId ProjectId { get; }

    public WorkRequestKind Kind { get; private set; }

    /// <summary>Queue ordering: higher value dequeues first, then CreatedAt ascending.</summary>
    public RequestPriority Priority { get; private set; }

    public RiskLevel RiskLevel { get; private set; }

    /// <summary>Normalized non-empty short title.</summary>
    public string Title { get; private set; }

    /// <summary>Normalized non-empty instruction prompt.</summary>
    public string Prompt { get; private set; }

    public WorkRequestStatus Status { get; private set; }

    /// <summary>
    /// The in-flight phase the request was in when it became <see cref="WorkRequestStatus.Blocked"/>;
    /// preserved separately so unblocking can resume the phase. Null unless Status is Blocked.
    /// </summary>
    public WorkRequestStatus? BlockedPhase { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Immutable link to the request this retry was drafted from. Null for ordinary enqueue.
    /// Does not confer assignment, session, reservation, or execution authority.
    /// </summary>
    public WorkRequestId? OriginalRequestId { get; }

    /// <summary>
    /// Creates a new work request in the <see cref="WorkRequestStatus.Queued"/> state — the only
    /// legal way to enter the queue. Throws <see cref="ArgumentException"/> on invalid input.
    /// </summary>
    public static WorkRequest Enqueue(
        ProjectId projectId,
        WorkRequestKind kind,
        RequestPriority priority,
        RiskLevel riskLevel,
        string title,
        string prompt,
        DateTimeOffset createdAt,
        WorkRequestId? originalRequestId = null)
    {
        var (cleanTitle, cleanPrompt) = Normalize(title, prompt);

        return new WorkRequest(
            WorkRequestId.New(),
            projectId,
            kind,
            priority,
            riskLevel,
            cleanTitle,
            cleanPrompt,
            WorkRequestStatus.Queued,
            blockedPhase: null,
            createdAt,
            createdAt,
            version: 1,
            originalRequestId);
    }

    /// <summary>
    /// Rehydrates a persisted work request without mutating timestamps or version.
    /// Blocked state must carry the phase it was blocked from.
    /// </summary>
    public static WorkRequest Rehydrate(
        WorkRequestId id,
        ProjectId projectId,
        WorkRequestKind kind,
        RequestPriority priority,
        RiskLevel riskLevel,
        string title,
        string prompt,
        WorkRequestStatus status,
        WorkRequestStatus? blockedPhase,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version,
        WorkRequestId? originalRequestId = null)
    {
        var (cleanTitle, cleanPrompt) = Normalize(title, prompt);
        if (status == WorkRequestStatus.Blocked && blockedPhase is null)
        {
            throw new ArgumentException("A blocked request must preserve the phase it was blocked from.", nameof(blockedPhase));
        }

        if (status != WorkRequestStatus.Blocked && blockedPhase is not null)
        {
            throw new ArgumentException("Only a blocked request may carry a blocked phase.", nameof(blockedPhase));
        }

        return new WorkRequest(
            id,
            projectId,
            kind,
            priority,
            riskLevel,
            cleanTitle,
            cleanPrompt,
            status,
            blockedPhase,
            createdAt,
            updatedAt,
            version,
            originalRequestId);
    }

    /// <summary>Transitions a queued request into <see cref="WorkRequestStatus.Starting"/>.</summary>
    public void Start(DateTimeOffset at)
    {
        EnsureCurrentStatus(WorkRequestStatus.Queued, nameof(Start));
        Transition(WorkRequestStatus.Starting, at);
    }

    /// <summary>Enters the planning phase from Starting.</summary>
    public void BeginPlanning(DateTimeOffset at)
    {
        EnsureCurrentStatus(WorkRequestStatus.Starting, nameof(BeginPlanning));
        Transition(WorkRequestStatus.Planning, at);
    }

    /// <summary>Enters the executing phase from Planning.</summary>
    public void BeginExecuting(DateTimeOffset at)
    {
        EnsureCurrentStatus(WorkRequestStatus.Planning, nameof(BeginExecuting));
        Transition(WorkRequestStatus.Executing, at);
    }

    /// <summary>Enters the reviewing phase from Executing.</summary>
    public void BeginReviewing(DateTimeOffset at)
    {
        EnsureCurrentStatus(WorkRequestStatus.Executing, nameof(BeginReviewing));
        Transition(WorkRequestStatus.Reviewing, at);
    }

    /// <summary>Enters the verifying phase from Reviewing.</summary>
    public void BeginVerifying(DateTimeOffset at)
    {
        EnsureCurrentStatus(WorkRequestStatus.Reviewing, nameof(BeginVerifying));
        Transition(WorkRequestStatus.Verifying, at);
    }

    /// <summary>Completes a verified request.</summary>
    public void Complete(DateTimeOffset at)
    {
        EnsureCurrentStatus(WorkRequestStatus.Verifying, nameof(Complete));
        Transition(WorkRequestStatus.Completed, at);
    }

    /// <summary>Fails a request from any in-flight phase.</summary>
    public void Fail(DateTimeOffset at)
    {
        EnsureInFlight(nameof(Fail));
        Transition(WorkRequestStatus.Failed, at);
    }

    /// <summary>
    /// Blocks a request from any in-flight phase, preserving the current phase in
    /// <see cref="BlockedPhase"/> so it can be resumed by <see cref="Unblock"/>.
    /// </summary>
    public void Block(DateTimeOffset at)
    {
        EnsureInFlight(nameof(Block));
        var phase = Status;
        Transition(WorkRequestStatus.Blocked, at);
        BlockedPhase = phase;
    }

    /// <summary>Resumes a blocked request back into the phase it was blocked from.</summary>
    public void Unblock(DateTimeOffset at)
    {
        if (Status != WorkRequestStatus.Blocked || BlockedPhase is null)
        {
            throw new InvalidOperationException("Only a blocked request with a preserved phase can be unblocked.");
        }

        var resumePhase = BlockedPhase.Value;
        UpdatedAt = at;
        Status = resumePhase;
        BlockedPhase = null;
        Version++;
    }

    /// <summary>Cancels a queued request before any execution assignment exists.</summary>
    public void CancelQueued(DateTimeOffset at)
    {
        EnsureCurrentStatus(WorkRequestStatus.Queued, nameof(CancelQueued));
        Transition(WorkRequestStatus.Cancelled, at);
    }

    /// <summary>Closes normal execution admission while assigned work is cancelled.</summary>
    public void BeginCancelling(DateTimeOffset at)
    {
        EnsureInFlight(nameof(BeginCancelling));
        Transition(WorkRequestStatus.Cancelling, at);
        BlockedPhase = null;
    }

    /// <summary>Records cancellation after the assigned execution has proved quiescence.</summary>
    public void ConfirmCancellation(DateTimeOffset at)
    {
        EnsureCurrentStatus(WorkRequestStatus.Cancelling, nameof(ConfirmCancellation));
        Transition(WorkRequestStatus.Cancelled, at);
    }

    /// <summary>
    /// Idempotent catch-up toward <paramref name="target"/> along the linear pipeline.
    /// Already-at-or-past statuses are no-ops. Terminal states never regress.
    /// Blocked requests stay blocked unless the target is a terminal outcome.
    /// </summary>
    public bool TryCatchUpTo(WorkRequestStatus target, DateTimeOffset at)
    {
        if (Status is WorkRequestStatus.Cancelling
            or WorkRequestStatus.Completed
            or WorkRequestStatus.Failed
            or WorkRequestStatus.Cancelled)
        {
            return Status == target;
        }

        if (target is WorkRequestStatus.Failed)
        {
            Fail(at);
            return true;
        }

        if (target is WorkRequestStatus.Blocked)
        {
            if (Status == WorkRequestStatus.Blocked)
            {
                return true;
            }

            Block(at);
            return true;
        }

        if (Status == WorkRequestStatus.Blocked)
        {
            return false;
        }

        var changed = false;
        while (PipelineRank(Status) < PipelineRank(target))
        {
            switch (Status)
            {
                case WorkRequestStatus.Queued:
                    Start(at);
                    break;
                case WorkRequestStatus.Starting:
                    BeginPlanning(at);
                    break;
                case WorkRequestStatus.Planning:
                    BeginExecuting(at);
                    break;
                case WorkRequestStatus.Executing:
                    BeginReviewing(at);
                    break;
                case WorkRequestStatus.Reviewing:
                    BeginVerifying(at);
                    break;
                case WorkRequestStatus.Verifying when target == WorkRequestStatus.Completed:
                    Complete(at);
                    break;
                default:
                    return changed;
            }

            changed = true;
        }

        return changed || Status == target;
    }

    private static int PipelineRank(WorkRequestStatus status) => status switch
    {
        WorkRequestStatus.Queued => 0,
        WorkRequestStatus.Starting => 1,
        WorkRequestStatus.Planning => 2,
        WorkRequestStatus.Executing => 3,
        WorkRequestStatus.Reviewing => 4,
        WorkRequestStatus.Verifying => 5,
        WorkRequestStatus.Completed => 6,
        _ => -1,
    };

    private void Transition(WorkRequestStatus next, DateTimeOffset at)
    {
        Status = next;
        UpdatedAt = at;
        Version++;
    }

    private void EnsureInFlight(string operation)
    {
        if (Status is WorkRequestStatus.Queued
            or WorkRequestStatus.Starting
            or WorkRequestStatus.Planning
            or WorkRequestStatus.Executing
            or WorkRequestStatus.Reviewing
            or WorkRequestStatus.Verifying
            or WorkRequestStatus.Blocked)
        {
            return;
        }

        throw new InvalidOperationException($"Request in terminal status '{Status}' cannot be transitioned by '{operation}'.");
    }

    private void EnsureCurrentStatus(WorkRequestStatus expected, string operation)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"'{operation}' requires status '{expected}' but request is '{Status}'.");
        }
    }

    private static (string Title, string Prompt) Normalize(string title, string prompt)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title must not be empty.", nameof(title));
        }

        var cleanTitle = title.Trim();
        if (cleanTitle.Length == 0)
        {
            throw new ArgumentException("Title must not be empty.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt must not be empty.", nameof(prompt));
        }

        var cleanPrompt = prompt.Trim();
        if (cleanPrompt.Length == 0)
        {
            throw new ArgumentException("Prompt must not be empty.", nameof(prompt));
        }

        return (cleanTitle, cleanPrompt);
    }
}
