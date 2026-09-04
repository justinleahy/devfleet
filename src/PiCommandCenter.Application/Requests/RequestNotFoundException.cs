using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>Thrown when an operation references a work request that does not exist.</summary>
public sealed class RequestNotFoundException(WorkRequestId id)
    : Exception($"Work request '{id.Value}' was not found.")
{
    public WorkRequestId Id { get; } = id;
}
