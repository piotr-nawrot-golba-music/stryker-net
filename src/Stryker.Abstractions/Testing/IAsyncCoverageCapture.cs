using System.Collections.Generic;
using System.Threading.Tasks;

namespace Stryker.Abstractions.Testing;

/// <summary>
/// Optional extension interface for test runners that support truly asynchronous
/// coverage capture. Runners that implement this interface (e.g. the MTP pool)
/// can be awaited end-to-end without blocking a thread-pool thread for the
/// duration of the coverage phase.
///
/// Runners that do not implement this interface (e.g. VsTestRunnerPool) fall back
/// to the synchronous <see cref="ITestRunner.CaptureCoverage"/> path.
/// </summary>
public interface IAsyncCoverageCapture
{
    Task<IEnumerable<ICoverageRunResult>> CaptureCoverageAsync(IProjectAndTests project);
}
