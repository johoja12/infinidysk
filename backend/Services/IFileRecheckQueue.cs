namespace NzbWebDAV.Services;

/// <summary>Moves one file to the front of the health-check queue, including one still waiting unscheduled.</summary>
public interface IFileRecheckQueue
{
    Task<HealthCheckService.FileRecheckOutcome> ExpediteFileRecheckAsync(Guid davItemId, CancellationToken cancellationToken);
}
