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
public class SingleMicrosoftTestPlatformRunnerCoverageTests
{
    private Dictionary<string, List<TestNode>> _testsByAssembly = null!;
    private Dictionary<string, MtpTestDescription> _testDescriptions = null!;
    private TestSet _testSet = null!;
    private object _discoveryLock = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testsByAssembly = new Dictionary<string, List<TestNode>>();
        _testDescriptions = new Dictionary<string, MtpTestDescription>();
        _testSet = new TestSet();
        _discoveryLock = new object();
    }

    [TestMethod]
    public async Task SetCoverageMode_ShouldEnableCoverageMode()
    {
        var runnerId = 600;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");
        
        try
        {
            // Create an existing coverage file that should be deleted
            await File.WriteAllTextAsync(coverageFilePath, "1,2,3");
            File.Exists(coverageFilePath).ShouldBeTrue("Setup: coverage file should exist before test");

            using var runner = new SingleMicrosoftTestPlatformRunner(
                runnerId,
                _testsByAssembly,
                _testDescriptions,
                _testSet,
                _discoveryLock,
                NullLogger.Instance);

            // Create a test assembly to trigger server creation
            var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;
            await runner.DiscoverTestsAsync(testAssembly);

            // Enable coverage mode
            runner.SetCoverageMode(true);

            // The old coverage file should be deleted
            File.Exists(coverageFilePath).ShouldBeFalse("Coverage file should be deleted when enabling coverage mode");

            // Servers should be disposed and will be recreated on next use with coverage env var
            // Verify we can still discover tests (which recreates servers)
            var result = await runner.DiscoverTestsAsync(testAssembly);
            result.ShouldBeTrue("Server should be recreated successfully after enabling coverage mode");

            // Enabling again should still delete any stale coverage file (defensive cleanup)
            await File.WriteAllTextAsync(coverageFilePath, "test");
            runner.SetCoverageMode(true);
            File.Exists(coverageFilePath).ShouldBeFalse("Should delete stale coverage file even when mode is already enabled");
        }
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public async Task SetCoverageMode_ShouldDisableCoverageMode()
    {
        var runnerId = 601;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");
        
        try
        {
            using var runner = new SingleMicrosoftTestPlatformRunner(
                runnerId,
                _testsByAssembly,
                _testDescriptions,
                _testSet,
                _discoveryLock,
                NullLogger.Instance);

            var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;

            // Enable coverage mode first
            runner.SetCoverageMode(true);
            await runner.DiscoverTestsAsync(testAssembly);
            
            // Create a coverage file
            await File.WriteAllTextAsync(coverageFilePath, "1,2,3");
            File.Exists(coverageFilePath).ShouldBeTrue("Setup: coverage file should exist");

            // Disable coverage mode
            runner.SetCoverageMode(false);

            // The coverage file should be deleted when changing modes (clean start)
            File.Exists(coverageFilePath).ShouldBeFalse("Coverage file should be deleted when disabling coverage mode");

            // Servers should be disposed and will be recreated without coverage env var
            var result = await runner.DiscoverTestsAsync(testAssembly);
            result.ShouldBeTrue("Server should be recreated successfully after disabling coverage mode");

            // Disabling again should still delete any stale coverage file (defensive cleanup)
            await File.WriteAllTextAsync(coverageFilePath, "test");
            runner.SetCoverageMode(false);
            File.Exists(coverageFilePath).ShouldBeFalse("Should delete stale coverage file even when mode is already disabled");
        }
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public async Task SetCoverageMode_ShouldNoOp_WhenModeIsAlreadySet()
    {
        var runnerId = 602;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");
        
        try
        {
            using var runner = new SingleMicrosoftTestPlatformRunner(
                runnerId,
                _testsByAssembly,
                _testDescriptions,
                _testSet,
                _discoveryLock,
                NullLogger.Instance);

            var testAssembly = typeof(SingleMicrosoftTestPlatformRunnerCoverageTests).Assembly.Location;
            await runner.DiscoverTestsAsync(testAssembly);

            // Enable coverage mode
            runner.SetCoverageMode(true);
            File.Exists(coverageFilePath).ShouldBeFalse("Coverage file should be deleted on first enable");

            // Create a coverage file to verify defensive cleanup still happens
            await File.WriteAllTextAsync(coverageFilePath, "test-data");

            // Try to enable again - servers should NOT be disposed, but stale coverage file should be deleted
            runner.SetCoverageMode(true);
            File.Exists(coverageFilePath).ShouldBeFalse("Stale coverage file should be deleted even when mode already enabled");

            // Verify servers are still functional (not disposed)
            var result = await runner.DiscoverTestsAsync(testAssembly);
            result.ShouldBeTrue("Servers should still be functional after no-op");
            
            // Disable coverage mode
            runner.SetCoverageMode(false);
            
            // Try to disable again - should do nothing (no server disposal)
            runner.SetCoverageMode(false);
            
            // Verify servers are still functional
            result = await runner.DiscoverTestsAsync(testAssembly);
            result.ShouldBeTrue("Servers should still be functional after no-op disable");
        }
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public async Task SetCoverageMode_ShouldRestartServers_WhenTogglingBetweenModes()
    {
        var runnerId = 603;

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
            500,
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
        var runnerId = 501;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");

        try
        {
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
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnEmpty_WhenFileContainsWhitespace()
    {
        var runnerId = 502;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");

        try
        {
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
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public void ReadCoverageData_ShouldParseCoveredMutants()
    {
        var runnerId = 503;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");

        try
        {
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
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public void ReadCoverageData_ShouldParseCoveredAndStaticMutants()
    {
        var runnerId = 504;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");

        try
        {
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
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public void ReadCoverageData_ShouldHandleSingleMutant()
    {
        var runnerId = 505;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");

        try
        {
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
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnEmptyCovered_WhenOnlyStaticMutantsPresent()
    {
        var runnerId = 506;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");

        try
        {
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
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public void ReadCoverageData_ShouldHandleTrailingSemicolon()
    {
        var runnerId = 507;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");

        try
        {
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
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
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
        var runnerId = 610;
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
        var runnerId = 620;
        var coverageFilePath = Path.Combine(Path.GetTempPath(), $"stryker-coverage-{runnerId}.txt");

        try
        {
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
        finally
        {
            if (File.Exists(coverageFilePath))
            {
                File.Delete(coverageFilePath);
            }
        }
    }

    [TestMethod]
    public void ReadCoverageData_ShouldReturnEmpty_WhenNoCoverageFile()
    {
        var runnerId = 621;
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

    // --- Live (epoch-based) per-test coverage tests ---
    //
    // 'perTest' mode keeps the test server alive: the runner bumps an epoch counter file
    // between tests, and the injected MutantControl flushes each test's coverage to
    // '<coverageFile>.<epoch>'. These tests simulate the MutantControl side.

    [TestMethod, Timeout(5000)]
    public async Task RunTestsForLiveCoverage_ShouldAttributeCoveragePerTest()
    {
        var assembly = "/fake/assembly.dll";
        var tests = new List<(TestNode Test, string TestId)>
        {
            (new TestNode("t1", "Test1", "test", "discovered"), "t1"),
            (new TestNode("t2", "Test2", "test", "discovered"), "t2"),
            (new TestNode("t3", "Test3", "test", "discovered"), "t3"),
        };

        // t2 covers nothing: it never triggers MutantControl, so no epoch file is written for it
        using var runner = new LiveCoverageSimulatingRunner(620, _testsByAssembly, _testDescriptions, _testSet, _discoveryLock,
            new Dictionary<string, int[]>
            {
                ["t1"] = [1, 2],
                ["t3"] = [3],
            });

        var results = await runner.RunTestsForLiveCoverageAsync(assembly, tests, CoverageConfidence.Normal);

        runner.ObservedEpochs.ShouldBe([1, 2, 3]);
        runner.ServerStopped.ShouldBeTrue("The server must be stopped to flush the final test's coverage");

        results.Count.ShouldBe(3);
        var r1 = results.Single(r => r.TestId == "t1");
        r1.MutationsCovered.OrderBy(x => x).ShouldBe([1, 2]);
        r1.Confidence.ShouldBe(CoverageConfidence.Normal);

        var r2 = results.Single(r => r.TestId == "t2");
        r2.MutationsCovered.ShouldBeEmpty();
        r2.Confidence.ShouldBe(CoverageConfidence.Normal);

        var r3 = results.Single(r => r.TestId == "t3");
        r3.MutationsCovered.ShouldBe([3]);
    }

    [TestMethod, Timeout(5000)]
    public async Task RunTestsForLiveCoverage_ShouldCleanUpEpochFiles()
    {
        var assembly = "/fake/assembly.dll";
        var tests = new List<(TestNode Test, string TestId)>
        {
            (new TestNode("t1", "Test1", "test", "discovered"), "t1"),
        };

        using var runner = new LiveCoverageSimulatingRunner(621, _testsByAssembly, _testDescriptions, _testSet, _discoveryLock,
            new Dictionary<string, int[]> { ["t1"] = [1] });

        await runner.RunTestsForLiveCoverageAsync(assembly, tests, CoverageConfidence.Normal);

        File.Exists(runner.EpochCoverageFilePath(1)).ShouldBeFalse("Per-epoch coverage files must be cleaned up");
        File.Exists(Path.Combine(Path.GetTempPath(), "stryker-epoch-621.txt")).ShouldBeFalse("Epoch counter file must be cleaned up");
    }

    [TestMethod, Timeout(5000)]
    public async Task RunTestsForLiveCoverage_ShouldReturnDubiousResults_WhenRunFails()
    {
        var assembly = "/fake/assembly.dll";
        var tests = new List<(TestNode Test, string TestId)>
        {
            (new TestNode("t1", "Test1", "test", "discovered"), "t1"),
            (new TestNode("t2", "Test2", "test", "discovered"), "t2"),
        };

        using var runner = new LiveCoverageSimulatingRunner(622, _testsByAssembly, _testDescriptions, _testSet, _discoveryLock,
            new Dictionary<string, int[]>(), failOnRun: true);

        var results = await runner.RunTestsForLiveCoverageAsync(assembly, tests, CoverageConfidence.Normal);

        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Confidence == CoverageConfidence.Dubious);
        results.ShouldAllBe(r => !r.MutationsCovered.Any());
    }

    [TestMethod, Timeout(5000)]
    public void SetCoverageMode_ShouldTrackLiveMode()
    {
        using var runner = new SingleMicrosoftTestPlatformRunner(
            623, _testsByAssembly, _testDescriptions, _testSet, _discoveryLock, NullLogger.Instance);

        var coverageField = typeof(SingleMicrosoftTestPlatformRunner).GetField("_coverageMode",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var liveField = typeof(SingleMicrosoftTestPlatformRunner).GetField("_liveCoverageMode",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        runner.SetCoverageMode(true, live: true);
        coverageField.GetValue(runner).ShouldBe(true);
        liveField.GetValue(runner).ShouldBe(true);

        runner.SetCoverageMode(false);
        coverageField.GetValue(runner).ShouldBe(false);
        liveField.GetValue(runner).ShouldBe(false);

        runner.SetCoverageMode(true);
        coverageField.GetValue(runner).ShouldBe(true);
        liveField.GetValue(runner).ShouldBe(false, "Plain coverage mode must not enable live (per-test) mode");
    }

    /// <summary>
    /// Simulates the injected MutantControl's epoch behavior: coverage accumulates while a test
    /// runs and is flushed to '&lt;coverageFile&gt;.&lt;epoch&gt;' when a NEW epoch is observed
    /// (i.e. during the next covering test) or on process exit (server stop).
    /// </summary>
    private class LiveCoverageSimulatingRunner : SingleMicrosoftTestPlatformRunner
    {
        private readonly Dictionary<string, int[]> _coverageByTestUid;
        private readonly bool _failOnRun;
        private readonly string _epochFilePath;
        private int _trackedEpoch;
        private int[] _accumulator = [];

        public List<int> ObservedEpochs { get; } = [];
        public bool ServerStopped { get; private set; }

        public LiveCoverageSimulatingRunner(
            int id,
            Dictionary<string, List<TestNode>> testsByAssembly,
            Dictionary<string, MtpTestDescription> testDescriptions,
            TestSet testSet,
            object discoveryLock,
            Dictionary<string, int[]> coverageByTestUid,
            bool failOnRun = false)
            : base(id, testsByAssembly, testDescriptions, testSet, discoveryLock, NullLogger.Instance)
        {
            _coverageByTestUid = coverageByTestUid;
            _failOnRun = failOnRun;
            _epochFilePath = Path.Combine(Path.GetTempPath(), $"stryker-epoch-{id}.txt");
        }

        internal override Task ExecuteSingleTestAsync(string assembly, TestNode test)
        {
            if (_failOnRun)
            {
                throw new InvalidOperationException("simulated server failure");
            }

            var epoch = int.Parse(File.ReadAllText(_epochFilePath).Trim());
            ObservedEpochs.Add(epoch);

            if (!_coverageByTestUid.TryGetValue(test.Uid, out var covered))
            {
                // test executes no mutated code: MutantControl never runs, nothing is flushed
                return Task.CompletedTask;
            }

            if (_trackedEpoch > 0 && _trackedEpoch != epoch)
            {
                Flush();
            }

            _trackedEpoch = epoch;
            _accumulator = covered;
            return Task.CompletedTask;
        }

        internal override Task StopAndRemoveServerAsync(string assembly)
        {
            ServerStopped = true;
            if (_trackedEpoch > 0)
            {
                Flush();
            }

            return Task.CompletedTask;
        }

        private void Flush()
        {
            File.WriteAllText(EpochCoverageFilePath(_trackedEpoch), string.Join(",", _accumulator) + ";");
            _accumulator = [];
        }
    }
}
