namespace Stryker
{
    /// <summary>
    /// A static class used for controlling mutant activation and coverage tracking at runtime.
    /// It supports both environment variable-based control (for VSTest runner) and file-based control (for MTP runner with process reuse).
    /// It should only use C# features up to v2 to ensure compatibility with the widest range of projects it is injected into.
    /// </summary>
    public static class MutantControl
    {
        private static System.Collections.Generic.List<int> _coveredMutants = new System.Collections.Generic.List<int>();
        private static System.Collections.Generic.List<int> _coveredStaticMutants = new System.Collections.Generic.List<int>();
        private static string envName = string.Empty;
        private static System.Object _coverageLock = new System.Object();
        private static long _lastMutantFileVersion = -1;
        // Initialized to avoid nullable warnings/errors
        private static string _cachedMutantFilePath = string.Empty;
        private static bool _mutantFilePathCached;

        // Coverage file path for MTP runner (file-based IPC)
        private static string _cachedCoverageFilePath = string.Empty;
        private static bool _coverageFilePathCached;
        private static bool _processExitRegistered;

        // Epoch-based per-test coverage for the MTP runner ('perTest' with process reuse):
        // the runner bumps an epoch counter in a control file between tests; when a coverage
        // registration observes a new epoch, the accumulated coverage is flushed to
        // '<coverageFile>.<oldEpoch>' and tracking restarts for the new epoch.
        private static string _cachedEpochFilePath = string.Empty;
        private static long _lastEpochFileVersion = -1;
        private static int _currentEpoch; // 0 = no epoch observed yet

        // this attribute will be set by the Stryker Data Collector before each test
        public static bool CaptureCoverage;
        public static int ActiveMutant = -2;
        public const int ActiveMutantNotInitValue = -2;

        static MutantControl()
        {
            // Check for MTP file-based coverage mode at class initialization
            // Environment variable contains only the filename, not the full path
            string coverageFileName = System.Environment.GetEnvironmentVariable("STRYKER_COVERAGE_FILE") ?? string.Empty;
            
            if (!string.IsNullOrEmpty(coverageFileName))
            {
                // Construct full path using temp directory
                _cachedCoverageFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), coverageFileName);
                _coverageFilePathCached = true;
                CaptureCoverage = true;

                // Per-test (epoch-based) coverage mode; the variable contains only the filename
                string epochFileName = System.Environment.GetEnvironmentVariable("STRYKER_COVERAGE_EPOCH_FILE") ?? string.Empty;
                if (!string.IsNullOrEmpty(epochFileName))
                {
                    _cachedEpochFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), epochFileName);
                }

                // Register for process exit to flush coverage data
                if (!_processExitRegistered)
                {
                    System.AppDomain.CurrentDomain.ProcessExit += delegate { FlushCoverageToFile(); };
                    _processExitRegistered = true;
                }
            }
        }

        public static void InitCoverage()
        {
            ResetCoverage();
        }

        public static void ResetCoverage()
        {
            _coveredMutants = new System.Collections.Generic.List<int>();
            _coveredStaticMutants = new System.Collections.Generic.List<int>();
        }

        public static void ResetActiveMutant()
        {
            ActiveMutant = ActiveMutantNotInitValue;
        }

        public static void SetActiveMutantViaEnvironmentVariable(int mutantId)
        {
            // Ensure we never assign null to a non-nullable string
            string environmentVariableName = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_ID_CONTROL_VAR") ?? string.Empty;
            if (environmentVariableName.Length > 0)
            {
                System.Environment.SetEnvironmentVariable(environmentVariableName, mutantId.ToString());
            }
            ActiveMutant = ActiveMutantNotInitValue;
        }

        private static bool TryReadMutantFromFile(out int mutantId)
        {
            mutantId = -1;

            // Cache the mutant file path to avoid repeated environment variable lookups
            if (!_mutantFilePathCached)
            {
                // coalesce null to empty string so _cachedMutantFilePath is never null
                _cachedMutantFilePath = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_FILE") ?? string.Empty;
                _mutantFilePathCached = true;
            }

            if (string.IsNullOrEmpty(_cachedMutantFilePath) || !System.IO.File.Exists(_cachedMutantFilePath))
            {
                return false;
            }

            try
            {
                System.IO.FileInfo fileInfo = new System.IO.FileInfo(_cachedMutantFilePath);
                long currentVersion = fileInfo.LastWriteTimeUtc.Ticks;

                // Only re-read if file has changed or we haven't read it yet
                if (currentVersion != _lastMutantFileVersion || ActiveMutant == ActiveMutantNotInitValue)
                {
                    string content = System.IO.File.ReadAllText(_cachedMutantFilePath).Trim();
                    if (int.TryParse(content, out mutantId))
                    {
                        _lastMutantFileVersion = currentVersion;
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore file read errors
            }
            return false;
        }

        public static System.Collections.Generic.IList<int>[] GetCoverageData()
        {
            System.Collections.Generic.IList<int>[] result = new System.Collections.Generic.IList<int>[] { _coveredMutants, _coveredStaticMutants };
            ResetCoverage();
            return result;
        }

        /// <summary>
        /// Writes accumulated coverage data to a file for MTP runner IPC.
        /// Called automatically on process exit to capture all coverage from tests run in this process.
        /// In epoch-based (per-test) mode the data is written to '<coverageFile>.<epoch>' so the
        /// runner can attribute it to the test that was running during that epoch.
        /// Format: "coveredMutants;staticMutants" (comma-separated IDs)
        /// </summary>
        public static void FlushCoverageToFile()
        {
            if (!_coverageFilePathCached)
            {
                // Environment variable contains only the filename
                string coverageFileName = System.Environment.GetEnvironmentVariable("STRYKER_COVERAGE_FILE") ?? string.Empty;
                if (!string.IsNullOrEmpty(coverageFileName))
                {
                    // Construct full path using temp directory
                    _cachedCoverageFilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), coverageFileName);
                }
                _coverageFilePathCached = true;
            }

            if (string.IsNullOrEmpty(_cachedCoverageFilePath))
            {
                return;
            }

            lock (_coverageLock)
            {
                if (_cachedEpochFilePath.Length > 0)
                {
                    // Per-test mode: the accumulator belongs to the currently tracked epoch.
                    // Epoch 0 means no coverage was ever registered, so there is nothing to write.
                    if (_currentEpoch > 0)
                    {
                        WriteCoverageFile(_cachedCoverageFilePath + "." + _currentEpoch.ToString());
                    }
                    return;
                }

                WriteCoverageFile(_cachedCoverageFilePath);
            }
        }

        private static void WriteCoverageFile(string filePath)
        {
            try
            {
                lock (_coverageLock)
                {
                    string covered = string.Join(",", _coveredMutants);
                    string staticMutants = string.Join(",", _coveredStaticMutants);
                    string content = covered + ";" + staticMutants;
                    System.IO.File.WriteAllText(filePath, content);
                    ResetCoverage();
                }
            }
            catch (System.Exception ex)
            {
                // Do not fail tests due to coverage write issues; log for diagnostics instead.
                System.Diagnostics.Debug.WriteLine(string.Format("[Stryker] Failed to flush coverage to file '{0}': {1}", filePath, ex));
            }
        }

        /// <summary>
        /// Detects a change of the coverage epoch (per-test mode). All coverage accumulated so far
        /// belongs to the previously tracked epoch and is flushed to that epoch's file before
        /// tracking moves to the new epoch. Must be called before registering new coverage.
        /// </summary>
        private static void CheckEpochChange()
        {
            if (_cachedEpochFilePath.Length == 0)
            {
                return;
            }

            try
            {
                if (!System.IO.File.Exists(_cachedEpochFilePath))
                {
                    return;
                }

                System.IO.FileInfo fileInfo = new System.IO.FileInfo(_cachedEpochFilePath);
                long currentVersion = fileInfo.LastWriteTimeUtc.Ticks;
                if (currentVersion == _lastEpochFileVersion)
                {
                    return;
                }

                string content = System.IO.File.ReadAllText(_cachedEpochFilePath).Trim();
                int epoch;
                if (!int.TryParse(content, out epoch))
                {
                    return;
                }

                _lastEpochFileVersion = currentVersion;
                if (epoch == _currentEpoch)
                {
                    return;
                }

                if (_currentEpoch > 0)
                {
                    WriteCoverageFile(_cachedCoverageFilePath + "." + _currentEpoch.ToString());
                }

                _currentEpoch = epoch;
            }
            catch
            {
                // Ignore file read errors; coverage stays attributed to the current epoch
            }
        }

        private static void CurrentDomain_ProcessExit(object sender, System.EventArgs e)
        {
            System.GC.KeepAlive(_coveredMutants);
            System.GC.KeepAlive(_coveredStaticMutants);
        }

        // check with: Stryker.MutantControl.IsActive(ID)
        public static bool IsActive(int id)
        {
            if (CaptureCoverage)
            {
                RegisterCoverage(id);
                return false;
            }

            // Check for file-based mutant control (used by MTP runner for process reuse)
            // Cache check: only call TryReadMutantFromFile if we might be using file-based control
            if (!_mutantFilePathCached || !string.IsNullOrEmpty(_cachedMutantFilePath))
            {
                int fileMutantId;
                if (TryReadMutantFromFile(out fileMutantId))
                {
                    ActiveMutant = fileMutantId;
                }

                // If we cached the file path and it's set, always use file-based control
                if (_mutantFilePathCached && !string.IsNullOrEmpty(_cachedMutantFilePath))
                {
                    return id == ActiveMutant;
                }
            }

            // lazy load the active mutant id from the environment variable (used by VSTest runner)
            if (ActiveMutant == ActiveMutantNotInitValue)
            {
                // coalesce null to empty string to avoid null-to-non-nullable conversion
                string environmentVariableName = System.Environment.GetEnvironmentVariable("STRYKER_MUTANT_ID_CONTROL_VAR") ?? string.Empty;
                if (environmentVariableName.Length > 0)
                {
                    string environmentVariable = System.Environment.GetEnvironmentVariable(environmentVariableName) ?? string.Empty;
                    if (string.IsNullOrEmpty(environmentVariable))
                    {
                        ActiveMutant = -1;
                    }
                    else
                    {
                        ActiveMutant = int.Parse(environmentVariable);
                    }
                }
                else
                {
                    ActiveMutant = -1;
                }
            }

            return id == ActiveMutant;
        }

        private static void RegisterCoverage(int id)
        {
            lock (_coverageLock)
            {
                // In per-test mode, a new epoch means the accumulated coverage belongs to the
                // previous test and must be flushed before this registration is recorded.
                CheckEpochChange();

                if (!_coveredMutants.Contains(id))
                {
                    _coveredMutants.Add(id);
                }
                if (MutantContext.InStatic() && !_coveredStaticMutants.Contains(id))
                {
                    _coveredStaticMutants.Add(id);
                }
            }
        }
    }
}
