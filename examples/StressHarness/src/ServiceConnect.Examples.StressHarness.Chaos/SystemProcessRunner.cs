using System.Diagnostics;
using System.Globalization;

namespace ServiceConnect.Examples.StressHarness.Chaos;

/// <summary>
/// Default <see cref="IProcessRunner"/> implementation. Spawns the
/// requested binary via <see cref="Process.Start(ProcessStartInfo)"/>,
/// propagates cancellation by killing the process tree, and returns the
/// child's exit code. stdout/stderr are redirected (so the child does
/// not block on a full console pipe) but discarded — failure surfaces
/// via the non-zero exit code returned to the caller.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"failed to start process {fileName}"));

        // Cancellation translates to a hard kill of the entire process tree —
        // docker compose spawns child processes whose lifetime exceeds the
        // CLI invocation, and a polite SIGTERM to the top-level binary alone
        // would leak those children.
        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // process already exited between HasExited and Kill — benign race
            }
        });

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
