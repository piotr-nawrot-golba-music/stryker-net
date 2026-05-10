using System.Collections.Generic;
using System.Threading.Tasks;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;

namespace Stryker.Core.CoverageAnalysis;

public interface ICoverageAnalyser
{
    Task DetermineTestCoverageAsync(IStrykerOptions options, IProjectAndTests project, ITestRunner runner, IEnumerable<IMutant> mutants, ITestIdentifiers resultFailingTests);
}
