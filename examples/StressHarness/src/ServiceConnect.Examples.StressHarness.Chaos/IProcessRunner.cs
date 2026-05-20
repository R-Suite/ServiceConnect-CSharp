namespace ServiceConnect.Examples.StressHarness.Chaos;

/// <summary>
/// Seam for shelling out to an external process. The default runtime
/// implementation (<see cref="SystemProcessRunner"/>) wraps
/// <see cref="System.Diagnostics.Process"/>; unit tests substitute a
/// recording fake so the chaos types can be exercised without a Docker
/// daemon present.
/// </summary>
public interface IProcessRunner
{
    Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
