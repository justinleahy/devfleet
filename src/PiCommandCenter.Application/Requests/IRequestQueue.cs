using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Prioritized queue surface for work requests. Ordering is priority descending, then
/// CreatedAt ascending.
/// </summary>
public interface IRequestQueue
{
    /// <summary>Lists a project's requests ordered by priority descending, then CreatedAt ascending.</summary>
    /// <exception cref="Application.Projects.ProjectNotFoundException">No project with the given id exists.</exception>
    Task<IReadOnlyList<WorkRequestDto>> ListAsync(ProjectId projectId, CancellationToken cancellationToken = default);

    /// <summary>Enqueues a new request in the Queued state.</summary>
    /// <exception cref="Application.Projects.ProjectNotFoundException">No project with the given id exists.</exception>
    /// <exception cref="ArgumentException">The command violates a request invariant.</exception>
    Task<WorkRequestDto> EnqueueAsync(ProjectId projectId, QueueWorkRequestCommand command, CancellationToken cancellationToken = default);

    /// <summary>Gets a single request by id.</summary>
    /// <exception cref="RequestNotFoundException">No work request with the given id exists.</exception>
    Task<WorkRequestDto> GetAsync(WorkRequestId requestId, CancellationToken cancellationToken = default);
}
