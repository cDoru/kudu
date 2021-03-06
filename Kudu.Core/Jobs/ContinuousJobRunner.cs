﻿using System;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Threading;
using Kudu.Contracts.Jobs;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core.Infrastructure;
using Kudu.Core.Tracing;

namespace Kudu.Core.Jobs
{
    public class ContinuousJobRunner : BaseJobRunner
    {
        private int _started = 0;
        private Thread _continuousJobThread;
        private readonly ContinuousJobLogger _continuousJobLogger;
        private readonly string _disableFilePath;

        public ContinuousJobRunner(string jobName, IEnvironment environment, IFileSystem fileSystem, IDeploymentSettingsManager settings, ITraceFactory traceFactory, IAnalytics analytics)
            : base(jobName, Constants.ContinuousPath, environment, fileSystem, settings, traceFactory, analytics)
        {
            _continuousJobLogger = new ContinuousJobLogger(jobName, Environment, FileSystem, TraceFactory);
            _continuousJobLogger.ReportStatus(ContinuousJobStatus.Initializing);

            _disableFilePath = Path.Combine(JobBinariesPath, "disable.job");
        }

        protected override string JobEnvironmentKeyPrefix
        {
            get { return "WEBSITE_ALWAYS_ON_JOB_RUNNING_"; }
        }

        protected override TimeSpan IdleTimeout
        {
            get { return TimeSpan.MaxValue; }
        }

        private void StartJob(ContinuousJob continuousJob)
        {
            // Do not go further if already started or job is disabled
            if (Interlocked.Exchange(ref _started, 1) == 1 || IsDisabled)
            {
                return;
            }

            _continuousJobLogger.ReportStatus(ContinuousJobStatus.Starting);

            _continuousJobThread = new Thread(() =>
            {
                try
                {
                    while (_started == 1 && !IsDisabled)
                    {
                        InitializeJobInstance(continuousJob, _continuousJobLogger);
                        RunJobInstance(continuousJob, _continuousJobLogger);

                        if (_started == 1 && !IsDisabled)
                        {
                            TimeSpan jobsInterval = Settings.GetJobsInterval();
                            _continuousJobLogger.LogInformation("Process went down, waiting for {0} seconds".FormatInvariant(jobsInterval.TotalSeconds));
                            _continuousJobLogger.ReportStatus(ContinuousJobStatus.PendingRestart);
                            WaitForTimeOrStop(jobsInterval);
                        }
                    }
                }
                catch (Exception ex)
                {
                    TraceFactory.GetTracer().TraceError(ex);
                }
            });

            _continuousJobThread.Start();
        }

        public void StopJob()
        {
            Interlocked.Exchange(ref _started, 0);
            SafeKillAllRunningJobInstances(_continuousJobLogger);

            if (_continuousJobThread != null)
            {
                if (!_continuousJobThread.Join(TimeSpan.FromMinutes(1)))
                {
                    _continuousJobThread.Abort();
                }

                _continuousJobThread = null;
                _continuousJobLogger.ReportStatus(ContinuousJobStatus.Stopped);
            }
        }

        public void RefreshJob(ContinuousJob continuousJob)
        {
            StopJob();
            StartJob(continuousJob);
        }

        public void DisableJob()
        {
            OperationManager.Attempt(() => FileSystem.File.WriteAllBytes(_disableFilePath, new byte[0]));
            StopJob();
        }

        public void EnableJob(ContinuousJob continuousJob)
        {
            OperationManager.Attempt(() => FileSystem.File.Delete(_disableFilePath));
            StartJob(continuousJob);
        }

        protected override void UpdateStatus(IJobLogger logger, string status)
        {
            logger.ReportStatus(new ContinuousJobStatus() { Status = status });
        }

        private void WaitForTimeOrStop(TimeSpan timeSpan)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeSpan && _started == 1)
            {
                Thread.Sleep(200);
            }
        }

        private bool IsDisabled
        {
            get { return FileSystem.File.Exists(_disableFilePath); }
        }
    }
}