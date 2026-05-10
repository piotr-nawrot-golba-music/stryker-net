using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;
using Stryker.Utilities.Logging;
using static Stryker.Abstractions.Testing.ITestRunner;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Manages a pool of MicrosoftTestPlatformRunner instances to enable parallel mutation testing
/// with isolated environment variables per runner.
/// </summary>
public sealed class MicrosoftTestPlatformRunnerPool : ITestRunner, IAsyncCoverageCapture, IAsyncDisposable
{
    private readonly Channel<SingleMicrosoftTestPlatformRunner> _runnerChannel;
    private readonly SingleMicrosoftTestPlatformRunner[] _runners;
    private readonly ILogger _logger;
    private readonly int _countOfRunners;
    private readonly TestSet _testSet = new();
    private readonly Dictionary<string, List<TestNode>> _testsByAssembly = new();
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions = new();
    private readonly object _discoveryLock = new();
    private readonly ISingleRunnerFactory _runnerFactory;
    private readonly IStrykerOptions _options;

    public IEnumerable<SingleMicrosoftTestPlatformRunner> Runners => _runners;

    public MicrosoftTestPlatformRunnerPool(IStrykerOptions options, ILogger? logger = null, ISingleRunnerFactory? runnerFactory = null)
    {
        _logger = logger ?? ApplicationLogging.LoggerFactory.CreateLogger<MicrosoftTestPlatformRunnerPool>();
        _options = options;
        _countOfRunners = Math.Max(1, options.Concurrency);
        _runnerFactory = runnerFactory ?? new DefaultRunnerFactory();
        _runners = new SingleMicrosoftTestPlatformRunner[_countOfRunners];
        // Bounded channel with one slot per runner acts as both the queue and the semaphore:
        // ReadAsync atomically dequeues (no retry loop needed) and TryWrite returns the runner.
        _runnerChannel = Channel.CreateBounded<SingleMicrosoftTestPlatformRunner>(
            new BoundedChannelOptions(_countOfRunners)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        _logger.LogWarning("The Microsoft Test Platform testrunner is currently in preview. Results should be verified since this feature is still being tested.");

        Initialize();
    }

    public void ResetTestProcesses() => ResetTestProcessesAsync().GetAwaiter().GetResult();

    private async Task ResetTestProcessesAsync()
    {
        _logger.LogDebug("Resetting all test server processes in the pool");
        await Task.WhenAll(_runners.Select(runner => runner.ResetServerAsync())).ConfigureAwait(false);
        _logger.LogDebug("All test server processes have been reset");
    }

    private void Initialize()
    {
        // Create and initialize all runners in parallel to speed up startup time
        Parallel.For(0, _countOfRunners, (int i, ParallelLoopState _) =>
        {
            var runner = _runnerFactory.CreateRunner(
                i,
                _testsByAssembly,
                _testDescriptions,
                _testSet,
                _discoveryLock,
                _logger,
                _options);
            _runners[i] = runner;
            _runnerChannel.Writer.TryWrite(runner);
        });
    }

    public async Task<bool> DiscoverTestsAsync(string assembly)
    {
        if (string.IsNullOrEmpty(assembly) || !File.Exists(assembly))
        {
            return false;
        }

        return await RunThisAsync(runner => runner.DiscoverTestsAsync(assembly)).ConfigureAwait(false);
    }

    public ITestSet GetTests(IProjectAndTests project) => _testSet;

    public async Task<ITestRunResult> InitialTestAsync(IProjectAndTests project)
    {
        var assemblies = project.GetTestAssemblies();
        ArgumentNullException.ThrowIfNull(assemblies);
        if (assemblies.Count == 0)
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        var results = await RunThisAsync(runner => runner.InitialTestAsync(project)).ConfigureAwait(false);

        // reset all test processes after the initial test run
        await ResetTestProcessesAsync().ConfigureAwait(false);

        return results;
    }

    /// <inheritdoc cref="ITestRunner.CaptureCoverage"/>
    /// <remarks>Delegates to <see cref="CaptureCoverageAsync"/> to avoid duplicating logic.</remarks>
    public IEnumerable<ICoverageRunResult> CaptureCoverage(IProjectAndTests project) =>
        CaptureCoverageAsync(project).GetAwaiter().GetResult();

    public async Task<IEnumerable<ICoverageRunResult>> CaptureCoverageAsync(IProjectAndTests project)
    {
        if (_options.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest))
        {
            var confidence = _options.OptimizationMode.HasFlag(OptimizationModes.CaptureCoveragePerTest)
                ? CoverageConfidence.Exact
                : CoverageConfidence.Normal;
            return await CaptureCoverageTestByTestAsync(project, confidence).ConfigureAwait(false);
        }

        return await CaptureCoverageInOneGoAsync(project).ConfigureAwait(false);
    }

    private async Task<IEnumerable<ICoverageRunResult>> CaptureCoverageInOneGoAsync(IProjectAndTests project)
    {
        _logger.LogInformation("Starting aggregate coverage capture for MTP runner");

        SetCoverageModeForAllRunners(true);

        try
        {
            var testResult = await RunThisAsync(runner => runner.InitialTestAsync(project)).ConfigureAwait(false);

            if (testResult.FailingTests.IsEveryTest)
            {
                _logger.LogWarning("Coverage test run failed: {Message}", testResult.ResultMessage);
            }

            await ResetTestProcessesAsync().ConfigureAwait(false);

            var allCoveredMutants = new HashSet<int>();
            var allStaticMutants = new HashSet<int>();

            foreach (var runner in _runners)
            {
                var (coveredMutants, staticMutants) = runner.ReadCoverageData();
                allCoveredMutants.UnionWith(coveredMutants);
                allStaticMutants.UnionWith(staticMutants);
            }

            _logger.LogInformation("Aggregate coverage capture complete: {CoveredCount} mutations covered, {StaticCount} static mutations",
                allCoveredMutants.Count, allStaticMutants.Count);

            return _testDescriptions.Values.Select(testDescription =>
                CoverageRunResult.Create(
                    testDescription.Id,
                    CoverageConfidence.Normal,
                    allCoveredMutants,
                    allStaticMutants,
                    []));
        }
        finally
        {
            SetCoverageModeForAllRunners(false);
        }
    }

    private async Task<IEnumerable<ICoverageRunResult>> CaptureCoverageTestByTestAsync(
        IProjectAndTests project,
        CoverageConfidence confidence)
    {
        _logger.LogInformation("Starting per-test coverage capture for MTP runner");

        SetCoverageModeForAllRunners(true);

        try
        {
            var testAssemblies = project.GetTestAssemblies();
            ArgumentNullException.ThrowIfNull(testAssemblies);
            var assemblySet = new HashSet<string>(testAssemblies);

            var allTests = new List<(string Assembly, TestNode Test, string TestId)>();
            lock (_discoveryLock)
            {
                foreach (var (assembly, tests) in _testsByAssembly)
                {
                    if (!assemblySet.Contains(assembly))
                    {
                        continue;
                    }

                    foreach (var test in tests)
                    {
                        if (_testDescriptions.TryGetValue(test.Uid, out var desc))
                        {
                            allTests.Add((assembly, test, desc.Id));
                        }
                    }
                }
            }

            _logger.LogInformation("Capturing per-test coverage for {TestCount} tests across {AssemblyCount} assemblies",
                allTests.Count, assemblySet.Count);

            var results = new ConcurrentBag<ICoverageRunResult>();

            await Parallel.ForEachAsync(allTests,
                new ParallelOptions { MaxDegreeOfParallelism = _countOfRunners },
                async (testInfo, _) =>
                {
                    var result = await RunThisAsync(async runner =>
                        await runner.RunSingleTestForCoverageAsync(
                            testInfo.Assembly, testInfo.Test, testInfo.TestId, confidence)
                            .ConfigureAwait(false))
                        .ConfigureAwait(false);

                    results.Add(result);
                }).ConfigureAwait(false);

            _logger.LogInformation(
                "Per-test coverage capture complete: {TestCount} tests captured",
                results.Count);

            return results;
        }
        finally
        {
            SetCoverageModeForAllRunners(false);
        }
    }

    public async Task<ITestRunResult> TestMultipleMutantsAsync(
        IProjectAndTests project,
        ITimeoutValueCalculator? timeoutCalc,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update)
    {
        var assemblies = project.GetTestAssemblies();
        ArgumentNullException.ThrowIfNull(assemblies);
        if (assemblies.Count == 0)
        {
            return new TestRunResult(false, "No test assemblies found");
        }

        return await RunThisAsync(runner => runner.TestMultipleMutantsAsync(project, timeoutCalc, mutants, update)).ConfigureAwait(false);
    }

    private async Task<T> RunThisAsync<T>(Func<SingleMicrosoftTestPlatformRunner, Task<T>> task)
    {
        const int maxWaitTimeSeconds = 300;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(maxWaitTimeSeconds));

        SingleMicrosoftTestPlatformRunner runner;
        try
        {
            // ReadAsync atomically dequeues one runner; no retry loop needed.
            runner = await _runnerChannel.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Timed out waiting for an available test runner after {maxWaitTimeSeconds} seconds. Total runners: {_countOfRunners}");
        }
        catch (ChannelClosedException ex)
        {
            throw new ObjectDisposedException(nameof(MicrosoftTestPlatformRunnerPool), ex);
        }

        try
        {
            return await task(runner).ConfigureAwait(false);
        }
        finally
        {
            // TryWrite always succeeds here: we own exactly one slot in the bounded channel.
            // If it returns false the channel was completed (disposal), which is fine —
            // the runner will be disposed via _runners[].
            _runnerChannel.Writer.TryWrite(runner);
        }
    }

    private void SetCoverageModeForAllRunners(bool enabled)
    {
        foreach (var runner in _runners)
            runner.SetCoverageMode(enabled);
    }

    public void Dispose()
    {
        _runnerChannel.Writer.TryComplete();
        foreach (var runner in _runners)
            runner.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _runnerChannel.Writer.TryComplete();
        await Task.WhenAll(_runners.Select(r => r.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}

