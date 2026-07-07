using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Stryker.Abstractions;
using Stryker.Abstractions.Exceptions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;
using static Stryker.Abstractions.Testing.ITestRunner;

namespace Stryker.TestRunner.MicrosoftTestPlatform;

/// <summary>
/// Individual test runner instance that handles test execution with mutation-specific
/// environment variables. Used by MicrosoftTestPlatformRunnerPool.
/// Maintains persistent test server connections per assembly to reduce process startup overhead.
/// Uses file-based mutant control to allow changing the active mutant without restarting processes.
/// </summary>
public class SingleMicrosoftTestPlatformRunner : IDisposable
{
    private readonly int _id;
    private readonly Dictionary<string, List<TestNode>> _testsByAssembly;
    private readonly Dictionary<string, MtpTestDescription> _testDescriptions;
    private readonly TestSet _testSet;
    private readonly object _discoveryLock;
    private readonly ILogger _logger;
    private readonly string _mutantFilePath;
    private readonly string _coverageFilePath;
    private readonly IStrykerOptions? _options;

    private readonly Dictionary<string, AssemblyTestServer> _assemblyServers = new();
    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private bool _disposed;
    private bool _coverageMode;
    // Per-test ('live') coverage: tests run one by one against a live server while an epoch
    // counter file tells the injected MutantControl where one test's coverage ends and the next begins
    private bool _liveCoverageMode;
    private readonly string _epochFilePath;

    private readonly string _runnerId;

    public SingleMicrosoftTestPlatformRunner(
        int id,
        Dictionary<string, List<TestNode>> testsByAssembly,
        Dictionary<string, MtpTestDescription> testDescriptions,
        TestSet testSet,
        object discoveryLock,
        ILogger logger,
        IStrykerOptions? options = null)
    {
        _id = id;
        _testsByAssembly = testsByAssembly;
        _testDescriptions = testDescriptions;
        _testSet = testSet;
        _discoveryLock = discoveryLock;
        _logger = logger;
        _options = options;

        // Create unique file paths for this runner to communicate with the test process
        _mutantFilePath = Path.Combine(Path.GetTempPath(), $"stryker-mutant-{_id}.txt");
        _coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{_id}.txt");
        _epochFilePath = Path.Combine(Path.GetTempPath(), $"stryker-epoch-{_id}.txt");
        _runnerId = $"MtpRunner-{_id}";

        // Initialize with no active mutation
        WriteMutantIdToFile(-1);
    }

    public Task<bool> DiscoverTestsAsync(string assembly)
    {
        return DiscoverTestsInternalAsync(assembly);
    }

    public Task<ITestRunResult> InitialTestAsync(IProjectAndTests project)
    {
        var assemblies = project.GetTestAssemblies();
        return RunAllTestsAsync(assemblies, mutantId: -1, mutants: null, update: null);
    }

    public Task<ITestRunResult> TestMultipleMutantsAsync(
        IProjectAndTests project,
        ITimeoutValueCalculator? timeoutCalc,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update)
    {
        var assemblies = project.GetTestAssemblies();

        if (mutants.Count > 0 && _options?.OptimizationMode.HasFlag(OptimizationModes.CoverageBasedTest) == true)
        {
            return RunMutantGroupAsync(assemblies, mutants, update, timeoutCalc);
        }

        if (mutants.Count > 1)
        {
            throw new GeneralStrykerException(
                "Internal error: trying to test multiple mutants simultaneously without 'perTest' coverage analysis.");
        }

        // When testing a single mutant, activate it; otherwise use -1 (no mutation)
        var mutantId = mutants.Count == 1 ? mutants[0].Id : -1;

        _logger.LogDebug("{RunnerId}: Testing mutant {MutantId} against all tests", _runnerId, mutantId);

        return RunAllTestsAsync(assemblies, mutantId, mutants, update, timeoutCalc);
    }

    public async Task ResetServerAsync()
    {
        _logger.LogDebug("{RunnerId}: Resetting test servers to reload assemblies", _runnerId);
        
        await _serverLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var server in _assemblyServers.Values)
            {
                server.Dispose();
            }
            _assemblyServers.Clear();
        }
        finally
        {
            _serverLock.Release();
        }
        
        _logger.LogDebug("{RunnerId}: Test servers reset complete", _runnerId);
    }

    /// <summary>
    /// Stops and removes the server for a specific assembly. This triggers ProcessExit
    /// in the test process, causing MutantControl.FlushCoverageToFile() to be called.
    /// The server is removed from the cache so a fresh one is created on next use.
    /// </summary>
    internal virtual async Task StopAndRemoveServerAsync(string assembly)
    {
        AssemblyTestServer? server;
        await _serverLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _assemblyServers.TryGetValue(assembly, out server);
            _assemblyServers.Remove(assembly);
        }
        finally
        {
            _serverLock.Release();
        }

        if (server is not null)
        {
            await server.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a single test in isolation to capture its per-test coverage data.
    /// The flow is: start server → run one test → stop server (triggers coverage flush) → read coverage file.
    /// This is used by the pool's CaptureCoverageTestByTest method.
    /// </summary>
    internal virtual async Task<ICoverageRunResult> RunSingleTestForCoverageAsync(
        string assembly, TestNode test, string testId, CoverageConfidence confidence)
    {
        try
        {
            DeleteCoverageFile();

            var server = await GetOrCreateServerAsync(assembly).ConfigureAwait(false);
            await server.RunTestsAsync(new[] { test }).ConfigureAwait(false);
            await StopAndRemoveServerAsync(assembly).ConfigureAwait(false);

            var (coveredMutants, staticMutants) = ReadCoverageData();

            DeleteCoverageFile();

            // Empty coverage likely means the process was force-killed before FlushCoverageToFile ran
            if (coveredMutants.Count == 0 && staticMutants.Count == 0)
            {
                _logger.LogWarning(
                    "{RunnerId}: No coverage data captured for test {TestId} — coverage file was empty or missing. Marking as Dubious.",
                    _runnerId, testId);

                return CoverageRunResult.Create(
                    testId,
                    CoverageConfidence.Dubious,
                    coveredMutants,
                    staticMutants,
                    Array.Empty<int>());
            }

            _logger.LogDebug(
                "{RunnerId}: Test {TestId} covers {CoveredCount} mutants ({StaticCount} static)",
                _runnerId, testId, coveredMutants.Count, staticMutants.Count);

            return CoverageRunResult.Create(
                testId,
                confidence,
                coveredMutants,
                staticMutants,
                Array.Empty<int>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to capture coverage for test {TestId}", _runnerId, testId);
            try { await StopAndRemoveServerAsync(assembly).ConfigureAwait(false); }
            catch { /* best-effort cleanup to prevent server leak */ }
            DeleteCoverageFile();
            return CoverageRunResult.Create(
                testId,
                CoverageConfidence.Dubious,
                Array.Empty<int>(),
                Array.Empty<int>(),
                Array.Empty<int>());
        }
    }

    /// <summary>
    /// Captures per-test coverage while keeping the test server alive ('perTest' mode).
    /// Tests run one at a time; before each test the epoch counter file is bumped, which makes the
    /// injected MutantControl flush the previous test's coverage to '&lt;coverageFile&gt;.&lt;epoch&gt;'.
    /// Stopping the server at the end flushes the final test's coverage on process exit.
    /// Unlike perTestInIsolation this avoids a process restart per test, at the cost of
    /// Normal (instead of Exact) confidence: code running on background threads may be
    /// attributed to the wrong test, and static initializers only run once per process.
    /// </summary>
    internal virtual async Task<IReadOnlyList<ICoverageRunResult>> RunTestsForLiveCoverageAsync(
        string assembly,
        IReadOnlyList<(TestNode Test, string TestId)> tests,
        CoverageConfidence confidence)
    {
        var results = new List<ICoverageRunResult>(tests.Count);

        try
        {
            var epoch = 0;
            foreach (var (test, _) in tests)
            {
                epoch++;
                WriteEpochToFile(epoch);
                await ExecuteSingleTestAsync(assembly, test).ConfigureAwait(false);
            }

            // stopping the server triggers the final coverage flush on process exit
            await StopAndRemoveServerAsync(assembly).ConfigureAwait(false);

            for (var e = 1; e <= tests.Count; e++)
            {
                var testId = tests[e - 1].TestId;
                // A missing epoch file is expected: it means the test never executed mutated code
                var (coveredMutants, staticMutants) = ReadCoverageData(EpochCoverageFilePath(e));

                _logger.LogDebug(
                    "{RunnerId}: Test {TestId} covers {CoveredCount} mutants ({StaticCount} static)",
                    _runnerId, testId, coveredMutants.Count, staticMutants.Count);

                results.Add(CoverageRunResult.Create(
                    testId,
                    confidence,
                    coveredMutants,
                    staticMutants,
                    Array.Empty<int>()));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to capture per-test coverage for {Assembly}", _runnerId, assembly);
            try { await StopAndRemoveServerAsync(assembly).ConfigureAwait(false); }
            catch { /* best-effort cleanup to prevent server leak */ }

            // coverage is unreliable for the tests of this run; mark them all as Dubious
            return tests
                .Select(t => CoverageRunResult.Create(
                    t.TestId, CoverageConfidence.Dubious, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()))
                .ToList();
        }
        finally
        {
            DeleteEpochArtifacts();
        }
    }

    /// <summary>
    /// Runs a single test against the (live) server of the given assembly. Seam for unit tests.
    /// </summary>
    internal virtual async Task ExecuteSingleTestAsync(string assembly, TestNode test)
    {
        var server = await GetOrCreateServerAsync(assembly).ConfigureAwait(false);
        await server.RunTestsAsync(new[] { test }).ConfigureAwait(false);
    }

    private void WriteEpochToFile(int epoch)
    {
        try
        {
            File.WriteAllText(_epochFilePath, epoch.ToString());
            // MutantControl detects epoch changes via the file's last-write time; consecutive
            // writes can fall within the timestamp resolution, so stamp a strictly increasing time
            File.SetLastWriteTimeUtc(_epochFilePath, DateTime.UnixEpoch.AddSeconds(epoch));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to write epoch to file {FilePath}", _runnerId, _epochFilePath);
        }
    }

    internal string EpochCoverageFilePath(int epoch) => $"{_coverageFilePath}.{epoch}";

    /// <summary>
    /// Deletes the epoch counter file and all per-epoch coverage files of this runner.
    /// </summary>
    private void DeleteEpochArtifacts()
    {
        try
        {
            if (File.Exists(_epochFilePath))
            {
                File.Delete(_epochFilePath);
            }

            foreach (var file in Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(_coverageFilePath)}.*"))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to delete epoch coverage files", _runnerId);
        }
    }

    private void WriteMutantIdToFile(int mutantId)
    {
        try
        {
            File.WriteAllText(_mutantFilePath, mutantId.ToString());
            _logger.LogDebug("{RunnerId}: Wrote mutant ID {MutantId} to file {FilePath}",
                _runnerId, mutantId, _mutantFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to write mutant ID to file {FilePath}",
                _runnerId, _mutantFilePath);
        }
    }

    private Dictionary<string, string?> BuildEnvironmentVariables()
    {
        var envVars = new Dictionary<string, string?>
        {
            ["STRYKER_MUTANT_FILE"] = _mutantFilePath
        };

        ExternalEnvironmentVariables.Add(envVars);

        // Add coverage filename when in coverage mode (MutantControl will combine with temp path)
        if (_coverageMode)
        {
            envVars["STRYKER_COVERAGE_FILE"] = Path.GetFileName(_coverageFilePath);

            if (_liveCoverageMode)
            {
                envVars["STRYKER_COVERAGE_EPOCH_FILE"] = Path.GetFileName(_epochFilePath);
            }
        }

        return envVars;
    }

    /// <summary>
    /// Enables or disables coverage capture mode. When enabled, the test process will track
    /// which mutations are covered and write the data to a file on process exit.
    /// </summary>
    public void SetCoverageMode(bool enabled) => SetCoverageMode(enabled, live: false);

    /// <summary>
    /// Enables or disables coverage capture mode. When <paramref name="live"/> is set, per-test
    /// (epoch-based) coverage is captured: the runner bumps an epoch counter between tests and the
    /// test process flushes each test's coverage to its own file without being restarted.
    /// </summary>
    internal void SetCoverageMode(bool enabled, bool live)
    {
        var liveMode = enabled && live;
        _serverLock.Wait();
        try
        {
            if (_coverageMode != enabled || _liveCoverageMode != liveMode)
            {
                _coverageMode = enabled;
                _liveCoverageMode = liveMode;
                _logger.LogDebug("{RunnerId}: Coverage mode {Status}", _runnerId,
                    enabled ? (liveMode ? "enabled (per test)" : "enabled") : "disabled");

                foreach (var server in _assemblyServers.Values)
                {
                    server.Dispose();
                }
                _assemblyServers.Clear();
            }
        }
        finally
        {
            _serverLock.Release();
        }

        // Always clean up any existing coverage files to prevent stale data,
        // even when the mode hasn't changed (e.g. retry/re-run paths)
        DeleteCoverageFile();
        DeleteEpochArtifacts();
    }

    /// <summary>
    /// Reads coverage data from the coverage file written by the test process.
    /// Returns the covered mutants and static mutants as separate lists.
    /// </summary>
    public (IReadOnlyList<int> CoveredMutants, IReadOnlyList<int> StaticMutants) ReadCoverageData() =>
        ReadCoverageData(_coverageFilePath);

    internal (IReadOnlyList<int> CoveredMutants, IReadOnlyList<int> StaticMutants) ReadCoverageData(string coverageFilePath)
    {
        if (!File.Exists(coverageFilePath))
        {
            _logger.LogDebug("{RunnerId}: Coverage file not found at {Path}", _runnerId, coverageFilePath);
            return (Array.Empty<int>(), Array.Empty<int>());
        }

        try
        {
            var content = File.ReadAllText(coverageFilePath).Trim();
            _logger.LogDebug("{RunnerId}: Read coverage data: {Content}", _runnerId, content);

            if (string.IsNullOrEmpty(content))
            {
                return (Array.Empty<int>(), Array.Empty<int>());
            }

            var parts = content.Split(';');
            var coveredMutants = ParseMutantIds(parts.Length > 0 ? parts[0] : string.Empty);
            var staticMutants = ParseMutantIds(parts.Length > 1 ? parts[1] : string.Empty);

            return (coveredMutants, staticMutants);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to read coverage file at {Path}", _runnerId, coverageFilePath);
            return (Array.Empty<int>(), Array.Empty<int>());
        }
    }

    private static IReadOnlyList<int> ParseMutantIds(string idString)
    {
        if (string.IsNullOrWhiteSpace(idString))
        {
            return Array.Empty<int>();
        }

        var parts = idString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var id))
            {
                result.Add(id);
            }
        }
        return result;
    }

    private void DeleteCoverageFile()
    {
        try
        {
            if (File.Exists(_coverageFilePath))
            {
                File.Delete(_coverageFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RunnerId}: Failed to delete coverage file at {Path}", _runnerId, _coverageFilePath);
        }
    }

    private async Task<AssemblyTestServer> GetOrCreateServerAsync(string assembly)
    {
        await _serverLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_assemblyServers.TryGetValue(assembly, out var existing) && existing.IsInitialized)
            {
                return existing;
            }

            var environmentVariables = BuildEnvironmentVariables();
            var server = new AssemblyTestServer(assembly, environmentVariables, _logger, _runnerId, _options);

            var started = await server.StartAsync().ConfigureAwait(false);
            if (!started)
            {
                throw new InvalidOperationException($"Failed to start test server for {assembly}");
            }

            _assemblyServers[assembly] = server;
            return server;
        }
        finally
        {
            _serverLock.Release();
        }
    }

    private async Task<bool> DiscoverTestsInternalAsync(string assembly)
    {
        try
        {
            var server = await GetOrCreateServerAsync(assembly).ConfigureAwait(false);
            var tests = await server.DiscoverTestsAsync().ConfigureAwait(false);

            lock (_discoveryLock)
            {
                _testsByAssembly[assembly] = tests;

                foreach (var test in tests.Where(t => !_testDescriptions.ContainsKey(t.Uid)))
                {
                    var mtpTestDescription = new MtpTestDescription(test);
                    _testDescriptions[test.Uid] = mtpTestDescription;
                    _testSet.RegisterTest(mtpTestDescription.Description);
                }
            }

            _logger.LogDebug("{RunnerId}: Discovered {TestCount} tests in {Assembly}", _runnerId, tests.Count, assembly);
            return tests.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{RunnerId}: Failed to discover tests in {Assembly}", _runnerId, assembly);
            return false;
        }
    }

    internal List<TestNode>? GetDiscoveredTests(string assembly)
    {
        lock (_discoveryLock)
        {
            return _testsByAssembly.TryGetValue(assembly, out var tests) ? tests : null;
        }
    }

    internal TimeSpan? CalculateAssemblyTimeout(List<TestNode> discoveredTests, ITimeoutValueCalculator timeoutCalc, string assembly)
    {
        int estimatedTimeMs;
        lock (_discoveryLock)
        {
            estimatedTimeMs = (int)discoveredTests
                .Sum(t => _testDescriptions.TryGetValue(t.Uid, out var desc)
                    ? desc.InitialRunTime.TotalMilliseconds
                    : 0);
        }

        var timeoutMs = timeoutCalc.CalculateTimeoutValue(estimatedTimeMs);
        _logger.LogDebug("{RunnerId}: Using {TimeoutMs} ms as test run timeout for {Assembly}",
            _runnerId, timeoutMs, Path.GetFileName(assembly));

        return TimeSpan.FromMilliseconds(timeoutMs);
    }

    internal async Task HandleAssemblyTimeoutAsync(string assembly, List<TestNode> discoveredTests, List<string> allTimedOutTests)
    {
        _logger.LogDebug("{RunnerId}: Test run timed out for {Assembly}", _runnerId, Path.GetFileName(assembly));

        allTimedOutTests.AddRange(discoveredTests.Select(t => t.Uid));
        
        AssemblyTestServer? server;
        await _serverLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _assemblyServers.TryGetValue(assembly, out server);
        }
        finally
        {
            _serverLock.Release();
        }
        
        if (server is not null)
        {
            _logger.LogDebug("{RunnerId}: Restarting test server for {Assembly} after timeout", _runnerId, Path.GetFileName(assembly));
            try
            {
                await server.RestartAsync(force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{RunnerId}: Failed to restart test server for {Assembly} after timeout. Creating a new server on next use.", _runnerId, Path.GetFileName(assembly));
                server.Dispose();
                await _serverLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    _assemblyServers.Remove(assembly);
                }
                finally
                {
                    _serverLock.Release();
                }
            }
        }
    }

    private sealed class TestRunAccumulator
    {
        private readonly List<string> _executedTests = [];
        private readonly List<string> _failedTests = [];
        private readonly List<string> _messages = [];
        private readonly List<string> _errorMessages = [];
        private int _totalDiscoveredTests;
        private int _totalExecutedTests;

        public List<string> TimedOutTests { get; } = [];
        public bool HasTimeout { get; set; }
        // When test runs were filtered by coverage, the executed set must never be
        // compressed to 'every test': mutants whose tests did not run would otherwise
        // be considered assessed (and marked Survived) by Mutant.AnalyzeTestRun.
        public bool SuppressEveryTestCompression { get; init; }
        public TimeSpan TotalDuration { get; private set; }

        public void Aggregate(TestRunResult result, List<TestNode>? discoveredTests)
        {
            if (result.ExecutedTests.IsEveryTest)
            {
                _totalExecutedTests += discoveredTests?.Count ?? 0;
                if (SuppressEveryTestCompression && discoveredTests is not null)
                {
                    // compression is disabled, so the executed tests must be listed explicitly
                    _executedTests.AddRange(discoveredTests.Select(t => t.Uid));
                }
            }
            else
            {
                var before = _executedTests.Count;
                _executedTests.AddRange(result.ExecutedTests.GetIdentifiers());
                _totalExecutedTests += _executedTests.Count - before;
            }

            _failedTests.AddRange(result.FailingTests.GetIdentifiers());
            TotalDuration += result.Duration;
            _messages.AddRange(result.Messages ?? []);

            if (!string.IsNullOrWhiteSpace(result.ResultMessage))
            {
                _errorMessages.Add(result.ResultMessage);
            }
        }

        public void AddDiscoveredCount(int count) => _totalDiscoveredTests += count;

        public ITestIdentifiers BuildExecutedTests() =>
            !SuppressEveryTestCompression && _totalDiscoveredTests > 0 && _totalExecutedTests >= _totalDiscoveredTests
                ? TestIdentifierList.EveryTest()
                : new TestIdentifierList(_executedTests);

        public ITestIdentifiers BuildFailedTests() => new TestIdentifierList(_failedTests);

        public ITestIdentifiers BuildTimedOutTests() => new TestIdentifierList(TimedOutTests);

        public string BuildErrorMessage() => string.Join(Environment.NewLine, _errorMessages);

        public IEnumerable<string> Messages => _messages;
    }

    internal async Task<ITestRunResult> RunAllTestsAsync(
        IReadOnlyList<string> assemblies,
        int mutantId,
        IReadOnlyList<IMutant>? mutants,
        TestUpdateHandler? update,
        ITimeoutValueCalculator? timeoutCalc = null)
    {
        try
        {
            var accumulator = new TestRunAccumulator();

            await RunTestsForActiveMutantAsync(assemblies, mutantId, testUidFilter: null, accumulator, timeoutCalc).ConfigureAwait(false);

            return FinalizeRun(accumulator, mutants, update);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{RunnerId}: Failed to run tests for mutant ID {MutantId}", _runnerId, mutantId);
            return new TestRunResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Tests a group of mutants using coverage analysis: each mutant is activated in turn
    /// (via the mutant file, without restarting the test process) and only its covering tests
    /// (<see cref="IMutant.AssessingTests"/>) are run. Mutant groups are built by Stryker core
    /// with disjoint test sets, so the aggregated results attribute failures unambiguously.
    /// </summary>
    private async Task<ITestRunResult> RunMutantGroupAsync(
        IReadOnlyList<string> assemblies,
        IReadOnlyList<IMutant> mutants,
        TestUpdateHandler? update,
        ITimeoutValueCalculator? timeoutCalc)
    {
        try
        {
            // (mutant id, covering test uids; null means every test)
            var runs = new List<(IMutant Mutant, IReadOnlySet<string>? TestUidFilter)>(mutants.Count);
            foreach (var mutant in mutants)
            {
                if (mutant.AssessingTests.IsEveryTest)
                {
                    runs.Add((mutant, null));
                    continue;
                }

                var coveringTests = mutant.AssessingTests.GetIdentifiers().ToHashSet();
                if (coveringTests.Count == 0)
                {
                    _logger.LogDebug("{RunnerId}: Mutant {MutantId} is not covered by any test, skipping",
                        _runnerId, mutant.Id);
                    continue;
                }

                runs.Add((mutant, coveringTests));
            }

            if (runs.Count == 0)
            {
                IEnumerable<MtpTestDescription> descriptions;
                lock (_discoveryLock)
                {
                    descriptions = _testDescriptions.Values.ToList();
                }

                return new TestRunResult(descriptions, TestIdentifierList.NoTest(), TestIdentifierList.NoTest(),
                    TestIdentifierList.NoTest(), "Mutants are not covered by any test!", [], TimeSpan.Zero);
            }

            var accumulator = new TestRunAccumulator
            {
                // only safe to report 'every test ran' when no coverage filtering took place
                SuppressEveryTestCompression = runs.Any(r => r.TestUidFilter is not null)
            };

            foreach (var (mutant, testUidFilter) in runs)
            {
                _logger.LogDebug("{RunnerId}: Testing mutant {MutantId} against {TestCount}",
                    _runnerId, mutant.Id, testUidFilter is null ? "all tests" : $"{testUidFilter.Count} covering test(s)");

                await RunTestsForActiveMutantAsync(assemblies, mutant.Id, testUidFilter, accumulator, timeoutCalc).ConfigureAwait(false);
            }

            return FinalizeRun(accumulator, mutants, update);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{RunnerId}: Failed to test mutant group [{Mutants}]",
                _runnerId, string.Join(",", mutants.Select(m => m.Id)));
            return new TestRunResult(false, ex.Message);
        }
    }

    private async Task RunTestsForActiveMutantAsync(
        IReadOnlyList<string> assemblies,
        int mutantId,
        IReadOnlySet<string>? testUidFilter,
        TestRunAccumulator accumulator,
        ITimeoutValueCalculator? timeoutCalc)
    {
        WriteMutantIdToFile(mutantId);

        foreach (var assembly in assemblies)
        {
            var (result, timedOut, testsRun) = await RunAssemblyTestsAsync(assembly, timeoutCalc, testUidFilter).ConfigureAwait(false);

            if (testsRun is not null)
            {
                accumulator.AddDiscoveredCount(testsRun.Count);

                if (timedOut)
                {
                    accumulator.HasTimeout = true;
                    await HandleAssemblyTimeoutAsync(assembly, testsRun, accumulator.TimedOutTests).ConfigureAwait(false);
                }
            }

            if (result is not null)
            {
                accumulator.Aggregate(result, testsRun);
            }
        }
    }

    private ITestRunResult FinalizeRun(
        TestRunAccumulator accumulator,
        IReadOnlyList<IMutant>? mutants,
        TestUpdateHandler? update)
    {
        var executedTests = accumulator.BuildExecutedTests();
        var failedTestIds = accumulator.BuildFailedTests();
        var timedOutTestIds = accumulator.BuildTimedOutTests();

        IEnumerable<MtpTestDescription> testDescriptionValues;
        lock (_discoveryLock)
        {
            testDescriptionValues = _testDescriptions.Values.ToList();
        }

        if (update is not null && mutants is not null)
        {
            update.Invoke(mutants, failedTestIds, executedTests, timedOutTestIds);
        }

        if (accumulator.HasTimeout)
        {
            return TestRunResult.TimedOut(
                testDescriptionValues,
                executedTests,
                failedTestIds,
                timedOutTestIds,
                accumulator.BuildErrorMessage(),
                accumulator.Messages,
                accumulator.TotalDuration);
        }

        return new TestRunResult(
            testDescriptionValues,
            executedTests,
            failedTestIds,
            timedOutTestIds,
            accumulator.BuildErrorMessage(),
            accumulator.Messages,
            accumulator.TotalDuration);
    }

    internal virtual async Task<(TestRunResult? Result, bool TimedOut, List<TestNode>? TestsRun)> RunAssemblyTestsAsync(
        string assembly,
        ITimeoutValueCalculator? timeoutCalc,
        IReadOnlySet<string>? testUidFilter = null)
    {
        if (!File.Exists(assembly))
        {
            return (null, false, null);
        }

        var discoveredTests = GetDiscoveredTests(assembly);
        var testsToRun = testUidFilter is null
            ? discoveredTests
            : discoveredTests?.Where(t => testUidFilter.Contains(t.Uid)).ToList();

        TimeSpan? timeout = null;
        if (timeoutCalc is not null && testsToRun is not null)
        {
            timeout = CalculateAssemblyTimeout(testsToRun, timeoutCalc, assembly);
        }

        var (testResults, timedOut) = await RunAssemblyTestsInternalAsync(
            assembly,
            testUidFilter is null ? null : t => testUidFilter.Contains(t.Uid),
            timeout).ConfigureAwait(false);

        return (testResults as TestRunResult, timedOut, testsToRun);
    }

    internal async Task<(ITestRunResult Result, bool TimedOut)> RunAssemblyTestsInternalAsync(
        string assembly,
        Func<TestNode, bool>? testUidFilter,
        TimeSpan? timeout = null)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            var server = await GetOrCreateServerAsync(assembly).ConfigureAwait(false);

            List<TestNode>? tests = null;
            lock (_discoveryLock)
            {
                if (_testsByAssembly.TryGetValue(assembly, out var assemblyTests))
                {
                    tests = assemblyTests;
                }
            }

            var testsToRun = tests?.Where(t => testUidFilter is null || testUidFilter(t)).ToArray();

            var (testResults, timedOut) = await server.RunTestsAsync(testsToRun, timeout).ConfigureAwait(false);

            var duration = DateTime.UtcNow - startTime;
            var result = BuildTestRunResult(testResults, tests?.Count ?? 0, duration);

            return (result, timedOut);
        }
        catch (Exception ex)
        {
            return (new TestRunResult(false, ex.Message), false);
        }
    }

    /// <summary>
    /// Maps a list of <see cref="TestNodeUpdate"/>s returned by the MTP server
    /// to a <see cref="TestRunResult"/>. Exposed for unit testing.
    /// </summary>
    /// <remarks>
    /// Classification of execution states goes through <see cref="TestNodeStates"/>
    /// so that failure attribution (the bug this adapter originally had) stays in
    /// one place:
    /// <list type="bullet">
    ///   <item><description><c>failed</c>/<c>error</c>/<c>cancelled</c> → failing tests (mutant killed)</description></item>
    ///   <item><description><c>timed-out</c> → timed-out tests (mutant timeout)</description></item>
    ///   <item><description><c>passed</c>/<c>skipped</c> → executed but neither failing nor timed-out</description></item>
    ///   <item><description><c>in-progress</c>/<c>discovered</c> → excluded from executed tests</description></item>
    /// </list>
    /// </remarks>
    internal TestRunResult BuildTestRunResult(
        IReadOnlyCollection<TestNodeUpdate> testResults,
        int totalDiscoveredTests,
        TimeSpan duration)
    {
        var finishedTests = testResults
            .Where(x => TestNodeStates.IsFinished(x.Node.ExecutionState))
            .ToList();

        var failedTests = finishedTests
            .Where(x => TestNodeStates.IsFailure(x.Node.ExecutionState))
            .Select(x => x.Node.Uid)
            .ToList();

        var timedOutTests = finishedTests
            .Where(x => TestNodeStates.IsTimeout(x.Node.ExecutionState))
            .Select(x => x.Node.Uid)
            .ToList();

        lock (_discoveryLock)
        {
            // MTP doesn't report per-test timing, so approximate with the average
            var perTestDuration = finishedTests.Count > 0
                ? TimeSpan.FromTicks(duration.Ticks / finishedTests.Count)
                : TimeSpan.Zero;

            foreach (var testResult in finishedTests.Where(tr => _testDescriptions.ContainsKey(tr.Node.Uid)))
            {
                var testDescription = _testDescriptions[testResult.Node.Uid];
                testDescription.RegisterInitialTestResult(new MtpTestResult(perTestDuration));
            }
        }

        var errorMessagesStr = string.Join(Environment.NewLine,
            finishedTests
                .Where(x => TestNodeStates.IsFailure(x.Node.ExecutionState)
                         || TestNodeStates.IsTimeout(x.Node.ExecutionState))
                .Select(x => $"{x.Node.DisplayName}{Environment.NewLine}{Environment.NewLine}State: {x.Node.ExecutionState}"));

        var messages = finishedTests.Select(x =>
            $"{x.Node.DisplayName}{Environment.NewLine}{Environment.NewLine}State: {x.Node.ExecutionState}");

        var executedTestCount = finishedTests.Count;
        var executedTests = totalDiscoveredTests > 0 && executedTestCount >= totalDiscoveredTests
            ? TestIdentifierList.EveryTest()
            : new TestIdentifierList(finishedTests.Select(x => x.Node.Uid));

        var failedTestIds = new TestIdentifierList(failedTests);
        var timedOutTestIds = timedOutTests.Count == 0
            ? TestIdentifierList.NoTest()
            : new TestIdentifierList(timedOutTests);

        IEnumerable<MtpTestDescription> testDescriptionValues;
        lock (_discoveryLock)
        {
            testDescriptionValues = _testDescriptions.Values.ToList();
        }

        return new TestRunResult(
            testDescriptionValues,
            executedTests,
            failedTestIds,
            timedOutTestIds,
            errorMessagesStr,
            messages,
            duration);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _serverLock.Wait();
            try
            {
                foreach (var server in _assemblyServers.Values)
                {
                    server.Dispose();
                }
                _assemblyServers.Clear();
            }
            finally
            {
                _serverLock.Release();
            }

            // Clean up temp files
            try
            {
                if (File.Exists(_mutantFilePath))
                {
                    File.Delete(_mutantFilePath);
                }
                if (File.Exists(_coverageFilePath))
                {
                    File.Delete(_coverageFilePath);
                }
            }
            catch (Exception ex)
            {
                // Ignore cleanup errors
                _logger.LogWarning(ex, "{RunnerId}: Failed to clean up temp files", _runnerId);
            }
            DeleteEpochArtifacts();

            _serverLock.Dispose();
        }
        _disposed = true;
    }
}


