﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Web;
using Kudu.Contracts.Jobs;
using Kudu.Contracts.Settings;
using Kudu.Core.Tracing;

namespace Kudu.Core.Jobs
{
    public abstract class JobsManagerBase
    {
        protected static readonly IScriptHost[] ScriptHosts = new IScriptHost[]
        {
            new WindowsScriptHost(),
            new BashScriptHost(),
            new PythonScriptHost(),
            new PhpScriptHost(),
            new NodeScriptHost()
        };
    }

    public abstract class JobsManagerBase<TJob> : JobsManagerBase, IJobsManager<TJob> where TJob : JobBase, new()
    {
        private const string DefaultScriptFileName = "run";

        private readonly string _jobsTypePath;

        private string _appBaseUrlPrefix;
        private string _urlPrefix;
        private string _vfsUrlPrefix;

        protected IEnvironment Environment { get; private set; }

        protected IFileSystem FileSystem { get; private set; }

        protected IDeploymentSettingsManager Settings { get; private set; }

        protected ITraceFactory TraceFactory { get; private set; }

        protected string JobsBinariesPath { get; private set; }

        protected string JobsDataPath { get; private set; }

        protected IAnalytics Analytics { get; private set; }

        protected JobsManagerBase(ITraceFactory traceFactory, IEnvironment environment, IFileSystem fileSystem, IDeploymentSettingsManager settings, IAnalytics analytics, string jobsTypePath)
        {
            TraceFactory = traceFactory;
            Environment = environment;
            FileSystem = fileSystem;
            Settings = settings;
            Analytics = analytics;

            _jobsTypePath = jobsTypePath;

            JobsBinariesPath = Path.Combine(Environment.JobsBinariesPath, jobsTypePath);
            JobsDataPath = Path.Combine(Environment.JobsDataPath, jobsTypePath);
        }

        public abstract IEnumerable<TJob> ListJobs();

        public abstract TJob GetJob(string jobName);

        protected TJob GetJobInternal(string jobName)
        {
            string jobPath = Path.Combine(JobsBinariesPath, jobName);
            DirectoryInfoBase jobDirectory = FileSystem.DirectoryInfo.FromDirectoryName(jobPath);
            return BuildJob(jobDirectory);
        }

        protected IEnumerable<TJob> ListJobsInternal()
        {
            var jobs = new List<TJob>();

            if (!FileSystem.Directory.Exists(JobsBinariesPath))
            {
                return Enumerable.Empty<TJob>();
            }

            DirectoryInfoBase jobsDirectory = FileSystem.DirectoryInfo.FromDirectoryName(JobsBinariesPath);
            DirectoryInfoBase[] jobDirectories = jobsDirectory.GetDirectories("*", SearchOption.TopDirectoryOnly);
            foreach (DirectoryInfoBase jobDirectory in jobDirectories)
            {
                TJob job = BuildJob(jobDirectory);
                if (job != null)
                {
                    jobs.Add(job);
                }
            }

            return jobs;
        }

        protected TJob BuildJob(DirectoryInfoBase jobDirectory)
        {
            if (!jobDirectory.Exists)
            {
                return null;
            }

            string jobName = jobDirectory.Name;
            FileInfoBase[] files = jobDirectory.GetFiles("*.*", SearchOption.TopDirectoryOnly);
            IScriptHost scriptHost;
            string scriptFilePath = FindCommandToRun(files, out scriptHost);

            if (scriptFilePath == null)
            {
                return null;
            }

            string runCommand = scriptFilePath.Substring(jobDirectory.FullName.Length + 1);

            var job = new TJob()
            {
                Name = jobName,
                Url = BuildJobsUrl(jobName),
                ExtraInfoUrl = BuildExtraInfoUrl(jobName),
                ScriptFilePath = scriptFilePath,
                RunCommand = runCommand,
                JobType = _jobsTypePath,
                ScriptHost = scriptHost
            };

            UpdateJob(job);

            return job;
        }

        protected abstract void UpdateJob(TJob job);

        protected TJobStatus GetStatus<TJobStatus>(string statusFilePath) where TJobStatus : class, IJobStatus, new()
        {
            return JobLogger.ReadJobStatusFromFile<TJobStatus>(TraceFactory, FileSystem, statusFilePath) ?? new TJobStatus();
        }

        protected Uri BuildJobsUrl(string relativeUrl)
        {
            if (_urlPrefix == null)
            {
                if (AppBaseUrlPrefix == null)
                {
                    return null;
                }

                _urlPrefix = "{0}/jobs/{1}/".FormatInvariant(AppBaseUrlPrefix, _jobsTypePath);
            }

            return new Uri(_urlPrefix + relativeUrl);
        }

        protected Uri BuildVfsUrl(string relativeUrl)
        {
            if (_vfsUrlPrefix == null)
            {
                if (AppBaseUrlPrefix == null)
                {
                    return null;
                }

                _vfsUrlPrefix = "{0}/vfs/data/jobs/{1}/".FormatInvariant(AppBaseUrlPrefix, _jobsTypePath);
            }

            return new Uri(_vfsUrlPrefix + relativeUrl);
        }

        protected Uri BuildExtraInfoUrl(string jobName)
        {
            // TODO: If a specific file exists read url from there
            return BuildDefaultExtraInfoUrl(jobName);
        }

        protected abstract Uri BuildDefaultExtraInfoUrl(string jobName);

        protected string AppBaseUrlPrefix
        {
            get
            {
                if (_appBaseUrlPrefix == null)
                {
                    if (HttpContext.Current == null)
                    {
                        return null;
                    }

                    _appBaseUrlPrefix = HttpContext.Current.Request.Url.GetLeftPart(UriPartial.Authority);
                }

                return _appBaseUrlPrefix;
            }
        }

        private static string FindCommandToRun(FileInfoBase[] files, out IScriptHost scriptHostFound)
        {
            string secondaryScriptFound = null;

            scriptHostFound = null;

            foreach (IScriptHost scriptHost in ScriptHosts)
            {
                if (String.IsNullOrEmpty(scriptHost.HostPath))
                {
                    continue;
                }

                foreach (string supportedExtension in scriptHost.SupportedExtensions)
                {
                    var supportedFiles = files.Where(f => String.Equals(f.Extension, supportedExtension, StringComparison.OrdinalIgnoreCase));
                    if (supportedFiles.Any())
                    {
                        var scriptFound =
                            supportedFiles.FirstOrDefault(f => String.Equals(f.Name, DefaultScriptFileName + supportedExtension, StringComparison.OrdinalIgnoreCase));

                        if (scriptFound != null)
                        {
                            scriptHostFound = scriptHost;
                            return scriptFound.FullName;
                        }

                        if (secondaryScriptFound == null)
                        {
                            scriptHostFound = scriptHost;
                            secondaryScriptFound = supportedFiles.First().FullName;
                        }
                    }
                }
            }

            if (secondaryScriptFound != null)
            {
                return secondaryScriptFound;
            }

            return null;
        }
    }
}