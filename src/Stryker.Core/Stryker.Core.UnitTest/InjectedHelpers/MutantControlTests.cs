using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Stryker.Core.UnitTest.InjectedHelpers;

/// <summary>
/// Tests for MutantControl's thread-safety and signal-flush dirty-flag behavior.
/// MutantControl is a static class, so tests use reflection to manipulate private state
/// and clean up after themselves.
/// </summary>
[TestClass]
public class MutantControlTests : TestBase
{
    private static readonly Type MutantControlType = typeof(Stryker.MutantControl);

    private static void SetField(string name, object value) =>
        MutantControlType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, value);

    private static T GetField<T>(string name) =>
        (T)MutantControlType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    [TestMethod]
    public void FlushCoverageToFile_ShouldSkip_WhenNotDirty()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stryker-test-dirty-skip-{Guid.NewGuid()}.txt");

        try
        {
            SetField("_cachedCoverageFilePath", tempFile);
            SetField("_coverageFilePathCached", true);
            SetField("_coverageDirty", false);

            Stryker.MutantControl.FlushCoverageToFile();

            File.Exists(tempFile).ShouldBeFalse("FlushCoverageToFile should not write when _coverageDirty is false");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            SetField("_coverageDirty", false);
        }
    }

    [TestMethod]
    public void FlushCoverageToFile_ShouldFlush_WhenDirty()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stryker-test-dirty-flush-{Guid.NewGuid()}.txt");

        try
        {
            Stryker.MutantControl.ResetCoverage();
            SetField("_cachedCoverageFilePath", tempFile);
            SetField("_coverageFilePathCached", true);
            SetField("_coverageDirty", true);

            Stryker.MutantControl.FlushCoverageToFile();

            File.Exists(tempFile).ShouldBeTrue("FlushCoverageToFile should write when _coverageDirty is true");
            // Dirty flag should be cleared after flush
            GetField<bool>("_coverageDirty").ShouldBeFalse("_coverageDirty should be false after successful flush");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            SetField("_coverageDirty", false);
        }
    }

    [TestMethod]
    public void ConcurrentRegisterCoverage_AndFlushCoverageToFile_ShouldNotCorruptData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"stryker-test-concurrent-{Guid.NewGuid()}.txt");
        const int threadCount = 8;
        const int iterationsPerThread = 50;

        try
        {
            Stryker.MutantControl.ResetCoverage();
            SetField("_cachedCoverageFilePath", tempFile);
            SetField("_coverageFilePathCached", true);
            SetField("_coverageDirty", false);

            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var barrier = new Barrier(threadCount);

            // RegisterCoverage is private — invoke via IsActive with CaptureCoverage=true
            Stryker.MutantControl.CaptureCoverage = true;

            var tasks = new Task[threadCount];
            for (var i = 0; i < threadCount; i++)
            {
                var mutantId = i;
                tasks[i] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    for (var iter = 0; iter < iterationsPerThread; iter++)
                    {
                        try
                        {
                            // Alternate between registering coverage and flushing
                            if (iter % 5 == 0)
                            {
                                SetField("_coverageDirty", true);
                                Stryker.MutantControl.FlushCoverageToFile();
                            }
                            else
                            {
                                Stryker.MutantControl.IsActive(mutantId);
                            }
                        }
                        catch (Exception ex)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);

            exceptions.ShouldBeEmpty("Concurrent RegisterCoverage and FlushCoverageToFile should not throw");
        }
        finally
        {
            Stryker.MutantControl.CaptureCoverage = false;
            Stryker.MutantControl.ResetCoverage();
            SetField("_coverageDirty", false);
            SetField("_cachedCoverageFilePath", string.Empty);
            SetField("_coverageFilePathCached", false);
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
