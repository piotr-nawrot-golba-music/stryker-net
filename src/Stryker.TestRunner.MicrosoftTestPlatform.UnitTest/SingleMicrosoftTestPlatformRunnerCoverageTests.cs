using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.Testing;
using Stryker.TestRunner.MicrosoftTestPlatform.Models;
using Stryker.TestRunner.Results;
using Stryker.TestRunner.Tests;

namespace Stryker.TestRunner.MicrosoftTestPlatform.UnitTest;

[TestClass]
public class SingleMicrosoftTestPlatformRunnerCoverageTests : TestBase
{
    private Dictionary<string, List<TestNode>> _testsByAssembly = null!;
    private Dictionary<string, MtpTestDescription> _testDescriptions = null!;
    private TestSet _testSet = null!;
    private object _discoveryLock = null!;
    private readonly List<string> _tempFiles = [];

    // Scatter runner IDs per process so parallel test processes don't share temp file names
    private static int _nextRunnerId = (Environment.ProcessId & 0xFFFF) << 10;
    private int NextRunnerId() => Interlocked.Increment(ref _nextRunnerId);

    private string CoverageFilePath(int runnerId)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");
        _tempFiles.Add(path);
        return path;
    }

    [TestInitialize]
    public void Initialize()
    {
        _testsByAssembly = new Dictionary<string, List<TestNode>>();
        _testDescriptions = new Dictionary<string, MtpTestDescription>();
        _testSet = new TestSet();
        _discoveryLock = new object();
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var file in _tempFiles.Where(File.Exists))
        {
            File.Delete(file);
        }
        _tempFiles.Clear();
    }

    [TestMethod]
    public async Task SetCoverageMode_ShouldEnableCoverageMode()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);

        await File.WriteAllTextAsync(coverageFilePath, "1,2,3");
        File.Exists(coverageFilePath).ShouldBeTrue("Setup: coverage file should exist before test");

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;
        await runner.DiscoverTestsAsync(testAssembly);

        runner.SetCoverageMode(true);

        File.Exists(coverageFilePath).ShouldBeFalse("Coverage file should be deleted when enabling coverage mode");

        var result = await runner.DiscoverTestsAsync(testAssembly);
        result.ShouldBeTrue("Server should be recreated successfully after enabling coverage mode");

        await File.WriteAllTextAsync(coverageFilePath, "test");
        runner.SetCoverageMode(true);
        File.Exists(coverageFilePath).ShouldBeFalse("Should delete stale coverage file even when mode is already enabled");
    }

    [TestMethod]
    public async Task SetCoverageMode_ShouldDisableCoverageMode()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;

        runner.SetCoverageMode(true);
        await runner.DiscoverTestsAsync(testAssembly);

        await File.WriteAllTextAsync(coverageFilePath, "1,2,3");
        File.Exists(coverageFilePath).ShouldBeTrue("Setup: coverage file should exist");

        runner.SetCoverageMode(false);

        File.Exists(coverageFilePath).ShouldBeFalse("Coverage file should be deleted when disabling coverage mode");

        var result = await runner.DiscoverTestsAsync(testAssembly);
        result.ShouldBeTrue("Server should be recreated successfully after disabling coverage mode");

        await File.WriteAllTextAsync(coverageFilePath, "test");
        runner.SetCoverageMode(false);
        File.Exists(coverageFilePath).ShouldBeFalse("Should delete stale coverage file even when mode is already disabled");
    }

    [TestMethod]
    public async Task SetCoverageMode_ShouldNoOp_WhenModeIsAlreadySet()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;
        await runner.DiscoverTestsAsync(testAssembly);

        runner.SetCoverageMode(true);
        File.Exists(coverageFilePath).ShouldBeFalse("Coverage file should be deleted on first enable");

        await File.WriteAllTextAsync(coverageFilePath, "test-data");

        runner.SetCoverageMode(true);
        File.Exists(coverageFilePath).ShouldBeFalse("Stale coverage file should be deleted even when mode already enabled");

        var result = await runner.DiscoverTestsAsync(testAssembly);
        result.ShouldBeTrue("Servers should still be functional after no-op");

        runner.SetCoverageMode(false);
        runner.SetCoverageMode(false);

        result = await runner.DiscoverTestsAsync(testAssembly);
        result.ShouldBeTrue("Servers should still be functional after no-op disable");
    }

    [TestMethod]
    public async Task SetCoverageMode_ShouldRestartServers_WhenTogglingBetweenModes()
    {
        var runnerId = NextRunnerId();

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;

        // Initial discovery without coverage
        var result1 = await runner.DiscoverTestsAsync(testAssembly);
        result1.ShouldBeTrue("Initial discovery should succeed");

        // Enable coverage - should restart servers
        runner.SetCoverageMode(true);
        var result2 = await runner.DiscoverTestsAsync(testAssembly);
        result2.ShouldBeTrue("Discovery after enabling coverage should succeed (server restarted)");

        // Disable coverage - should restart servers again
        runner.SetCoverageMode(false);
        var result3 = await runner.DiscoverTestsAsync(testAssembly);
        result3.ShouldBeTrue("Discovery after disabling coverage should succeed (server restarted)");
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnEmpty_WhenFileDoesNotExist()
    {
        using var runner = new SingleMicrosoftTestPlatformRunner(
            NextRunnerId(),
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.ShouldBeEmpty();
        result.StaticMutants.ShouldBeEmpty();
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnEmpty_WhenFileIsEmpty()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);
        File.WriteAllText(coverageFilePath, string.Empty);

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.ShouldBeEmpty();
        result.StaticMutants.ShouldBeEmpty();
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnEmpty_WhenFileContainsWhitespace()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);
        File.WriteAllText(coverageFilePath, "   \n\t  ");

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.ShouldBeEmpty();
        result.StaticMutants.ShouldBeEmpty();
    }

    [TestMethod]
    public void ReadCoverageData_ShouldParseCoveredMutants()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);
        File.WriteAllText(coverageFilePath, "1,2,3");

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.Count.ShouldBe(3);
        result.CoveredMutants.ShouldContain(1);
        result.CoveredMutants.ShouldContain(2);
        result.CoveredMutants.ShouldContain(3);
        result.StaticMutants.ShouldBeEmpty();
    }

    [TestMethod]
    public void ReadCoverageData_ShouldParseCoveredAndStaticMutants()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);
        File.WriteAllText(coverageFilePath, "1,2,3;10,20");

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.Count.ShouldBe(3);
        result.CoveredMutants.ShouldContain(1);
        result.CoveredMutants.ShouldContain(2);
        result.CoveredMutants.ShouldContain(3);

        result.StaticMutants.Count.ShouldBe(2);
        result.StaticMutants.ShouldContain(10);
        result.StaticMutants.ShouldContain(20);
    }

    [TestMethod]
    public void ReadCoverageData_ShouldHandleSingleMutant()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);
        File.WriteAllText(coverageFilePath, "42");

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.Count.ShouldBe(1);
        result.CoveredMutants.ShouldContain(42);
        result.StaticMutants.ShouldBeEmpty();
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnEmptyCovered_WhenOnlyStaticMutantsPresent()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);
        File.WriteAllText(coverageFilePath, ";5,6,7");

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.ShouldBeEmpty();
        result.StaticMutants.Count.ShouldBe(3);
        result.StaticMutants.ShouldContain(5);
        result.StaticMutants.ShouldContain(6);
        result.StaticMutants.ShouldContain(7);
    }

    [TestMethod]
    public void ReadCoverageData_ShouldHandleTrailingSemicolon()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);
        File.WriteAllText(coverageFilePath, "1,2,3;");

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.Count.ShouldBe(3);
        result.StaticMutants.ShouldBeEmpty();
    }

    [TestMethod]
    public async Task ResetServerAsync_ShouldDisposeAndClearAllServers()
    {
        using var runner = new SingleMicrosoftTestPlatformRunner(
            0,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        // Populate _assemblyServers by discovering tests against the real test assembly
        var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;
        await runner.DiscoverTestsAsync(testAssembly);

        var serversField = typeof(SingleMicrosoftTestPlatformRunner)
            .GetField("_assemblyServers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var serversBefore = (Dictionary<string, AssemblyTestServer>)serversField.GetValue(runner)!;
        serversBefore.ShouldNotBeEmpty("servers should be populated after discovery");

        await runner.ResetServerAsync();

        var serversAfter = (Dictionary<string, AssemblyTestServer>)serversField.GetValue(runner)!;
        serversAfter.ShouldBeEmpty("all servers should be disposed and removed after reset");
    }

    [TestMethod]
    public async Task StopAndRemoveServerAsync_ShouldRemoveServerFromDictionary()
    {
        var runnerId = NextRunnerId();
        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;
        await runner.DiscoverTestsAsync(testAssembly);

        var serversField = typeof(SingleMicrosoftTestPlatformRunner)
            .GetField("_assemblyServers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var serversBefore = (Dictionary<string, AssemblyTestServer>)serversField.GetValue(runner)!;
        serversBefore.ShouldNotBeEmpty("servers should exist after discovery");

        var method = typeof(SingleMicrosoftTestPlatformRunner)
            .GetMethod("StopAndRemoveServerAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(runner, new object[] { testAssembly })!;

        var serversAfter = (Dictionary<string, AssemblyTestServer>)serversField.GetValue(runner)!;
        serversAfter.ContainsKey(testAssembly).ShouldBeFalse("server should be removed after stop");
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnCoveredAndStaticMutants_FromFile()
    {
        var runnerId = NextRunnerId();
        var coverageFilePath = CoverageFilePath(runnerId);

        using var runner = new SingleMicrosoftTestPlatformRunner(
            runnerId,
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        File.WriteAllText(coverageFilePath, "1,2,3;10");

        var result = runner.ReadCoverageData();

        result.CoveredMutants.Count.ShouldBe(3);
        result.CoveredMutants.ShouldContain(1);
        result.CoveredMutants.ShouldContain(2);
        result.CoveredMutants.ShouldContain(3);
        result.StaticMutants.Count.ShouldBe(1);
        result.StaticMutants.ShouldContain(10);
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnEmpty_WhenNoCoverageFile()
    {
        using var runner = new SingleMicrosoftTestPlatformRunner(
            NextRunnerId(),
            _testsByAssembly,
            _testDescriptions,
            _testSet,
            _discoveryLock,
            NullLogger.Instance);

        var result = runner.ReadCoverageData();

        result.CoveredMutants.ShouldBeEmpty();
        result.StaticMutants.ShouldBeEmpty();
    }

    [TestMethod]
    public void CaptureCoverageTestByTest_ShouldReturnDubious_WhenHandlerThrows()
    {
        var options = new Mock<IStrykerOptions>();
        options.Setup(x => x.Concurrency).Returns(1);
        options.Setup(x => x.OptimizationMode).Returns(OptimizationModes.CoverageBasedTest);

        var testNode = new TestNode("test-1", "ThrowingTest", "test", "discovered");
        var testsByAssembly = new Dictionary<string, List<TestNode>>
        {
            ["assembly.dll"] = [testNode]
        };
        var testDescriptions = new Dictionary<string, MtpTestDescription>
        {
            ["test-1"] = new(testNode)
        };

        var runnerFactory = new Mock<ISingleRunnerFactory>();
        runnerFactory.Setup(x => x.CreateRunner(
                It.IsAny<int>(),
                It.IsAny<Dictionary<string, List<TestNode>>>(),
                It.IsAny<Dictionary<string, MtpTestDescription>>(),
                It.IsAny<TestSet>(),
                It.IsAny<object>(),
                It.IsAny<ILogger>(),
                It.IsAny<IStrykerOptions>()))
            .Returns<int, Dictionary<string, List<TestNode>>, Dictionary<string, MtpTestDescription>, TestSet, object, ILogger, IStrykerOptions>(
                (id, tba, td, ts, dl, logger, opts) =>
                {
                    if (tba.Count == 0)
                    {
                        foreach (var kvp in testsByAssembly)
                            tba[kvp.Key] = kvp.Value;
                        foreach (var kvp in testDescriptions)
                            td[kvp.Key] = kvp.Value;
                    }
                    return new TestableRunner(id, tba, td, ts, dl,
                        () => { },
                        coverageHandler: (_, _, _, _) =>
                            throw new InvalidOperationException("Server startup failed"));
                });

        var project = new Mock<IProjectAndTests>();
        project.Setup(x => x.GetTestAssemblies()).Returns(new[] { "assembly.dll" });

        using var pool = new MicrosoftTestPlatformRunnerPool(options.Object, NullLogger.Instance, runnerFactory.Object);

        var coverage = pool.CaptureCoverage(project.Object).ToList();

        coverage.Count.ShouldBe(1);
        coverage[0].Confidence.ShouldBe(CoverageConfidence.Dubious);
        coverage[0].MutationsCovered.ShouldBeEmpty();
    }

    [TestMethod]
    public void CaptureCoverageTestByTest_ShouldReturnDubious_WhenCoverageIsEmpty()
    {
        var options = new Mock<IStrykerOptions>();
        options.Setup(x => x.Concurrency).Returns(1);
        options.Setup(x => x.OptimizationMode).Returns(OptimizationModes.CoverageBasedTest);

        var testNode = new TestNode("test-1", "NoCoverageTest", "test", "discovered");
        var testsByAssembly = new Dictionary<string, List<TestNode>>
        {
            ["assembly.dll"] = [testNode]
        };
        var testDescriptions = new Dictionary<string, MtpTestDescription>
        {
            ["test-1"] = new(testNode)
        };

        var runnerFactory = new Mock<ISingleRunnerFactory>();
        runnerFactory.Setup(x => x.CreateRunner(
                It.IsAny<int>(),
                It.IsAny<Dictionary<string, List<TestNode>>>(),
                It.IsAny<Dictionary<string, MtpTestDescription>>(),
                It.IsAny<TestSet>(),
                It.IsAny<object>(),
                It.IsAny<ILogger>(),
                It.IsAny<IStrykerOptions>()))
            .Returns<int, Dictionary<string, List<TestNode>>, Dictionary<string, MtpTestDescription>, TestSet, object, ILogger, IStrykerOptions>(
                (id, tba, td, ts, dl, logger, opts) =>
                {
                    if (tba.Count == 0)
                    {
                        foreach (var kvp in testsByAssembly)
                            tba[kvp.Key] = kvp.Value;
                        foreach (var kvp in testDescriptions)
                            td[kvp.Key] = kvp.Value;
                    }
                    return new TestableRunner(id, tba, td, ts, dl,
                        () => { },
                        coverageHandler: (_, _, testId, _) =>
                            Task.FromResult<ICoverageRunResult>(
                                CoverageRunResult.Create(testId, CoverageConfidence.Dubious,
                                    Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>())));
                });

        var project = new Mock<IProjectAndTests>();
        project.Setup(x => x.GetTestAssemblies()).Returns(new[] { "assembly.dll" });

        using var pool = new MicrosoftTestPlatformRunnerPool(options.Object, NullLogger.Instance, runnerFactory.Object);

        var coverage = pool.CaptureCoverage(project.Object).ToList();

        coverage.Count.ShouldBe(1);
        coverage[0].Confidence.ShouldBe(CoverageConfidence.Dubious);
        coverage[0].MutationsCovered.ShouldBeEmpty();
    }
}
