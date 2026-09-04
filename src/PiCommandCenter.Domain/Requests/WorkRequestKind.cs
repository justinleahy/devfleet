namespace PiCommandCenter.Domain.Requests;

/// <summary>
/// Kind of work a request represents.
/// </summary>
public enum WorkRequestKind
{
    Development = 0,
    Analysis = 1,
    Review = 2,
}
