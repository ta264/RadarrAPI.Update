﻿using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NLog;
using RadarrAPI.Services.ReleaseCheck.AppVeyor;
using RadarrAPI.Services.ReleaseCheck.Github;
using RadarrAPI.Update;
using Sentry;

namespace RadarrAPI.Services.ReleaseCheck
{
    public class ReleaseService
    {
        private static readonly ConcurrentDictionary<Branch, SemaphoreSlim> ReleaseLocks;

        private readonly IServiceScopeFactory _serviceScopeFactory;

        private readonly IHub _sentry;
        private readonly ILogger<ReleaseService> _logger;

        private readonly ConcurrentDictionary<Branch, Type> _releaseBranches;

        private readonly Config _config;

        static ReleaseService()
        {
            ReleaseLocks = new ConcurrentDictionary<Branch, SemaphoreSlim>();
            ReleaseLocks.TryAdd(Branch.Develop, new SemaphoreSlim(1, 1));
            ReleaseLocks.TryAdd(Branch.Nightly, new SemaphoreSlim(1, 1));
        }

        public ReleaseService(
            IServiceScopeFactory serviceScopeFactory, 
            IHub sentry, 
            ILogger<ReleaseService> logger,
            IOptions<Config> configOptions)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _sentry = sentry;
            _logger = logger;

            _releaseBranches = new ConcurrentDictionary<Branch, Type>();
            _releaseBranches.TryAdd(Branch.Develop, typeof(GithubReleaseSource));
            _releaseBranches.TryAdd(Branch.Nightly, typeof(AppVeyorReleaseSource));

            _config = configOptions.Value;
        }

        public async Task UpdateReleasesAsync(Branch branch)
        {
            if (!_releaseBranches.TryGetValue(branch, out var releaseSourceBaseType))
            {
                throw new NotImplementedException($"{branch} does not have a release source.");
            }

            if (!ReleaseLocks.TryGetValue(branch, out var releaseLock))
            {
                throw new NotImplementedException($"{branch} does not have a release lock.");
            }

            var obtainedLock = false;

            try
            {
                obtainedLock = await releaseLock.WaitAsync(TimeSpan.FromMinutes(5));

                if (obtainedLock)
                {
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var releaseSourceInstance = (ReleaseSourceBase) scope.ServiceProvider.GetRequiredService(releaseSourceBaseType);

                        releaseSourceInstance.ReleaseBranch = branch;

                        var hasNewRelease = await releaseSourceInstance.StartFetchReleasesAsync();
                        if (hasNewRelease)
                        {
                            await CallTriggers(branch);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "ReleaseService threw an exception.");
                _sentry.CaptureException(e);
            }
            finally
            {
                if (obtainedLock)
                {
                    releaseLock.Release();
                }
            }
        }

        private async Task CallTriggers(Branch branch)
        {
            var triggers = _config.Triggers[branch];
            if (triggers.Count == 0)
            {
                return;
            }

            foreach (var trigger in triggers)
            {
                try
                {
                    var request = WebRequest.CreateHttp(trigger);
                    request.Method = "GET";
                    request.UserAgent = "RadarrAPI.Update/Trigger";
                    request.KeepAlive = false;
                    request.Timeout = 2500;
                    request.ReadWriteTimeout = 2500;
                    request.ContinueTimeout = 2500;

                    var response = await request.GetResponseAsync();
                    response.Dispose();
                }
                catch (Exception)
                {
                    // don't care.
                }
            }
        }
    }
}
