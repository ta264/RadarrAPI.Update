using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RadarrAPI.Options;
using RadarrAPI.Services.ReleaseCheck.AppVeyor;
using RadarrAPI.Services.ReleaseCheck.Github;
using RadarrAPI.Services.ReleaseCheck.Azure;
using RadarrAPI.Update;
using Sentry;

namespace RadarrAPI.Services.ReleaseCheck
{
    public class ReleaseService
    {
        private static readonly ConcurrentDictionary<Type, SemaphoreSlim> ReleaseLocks;

        private readonly IServiceScopeFactory _serviceScopeFactory;

        private readonly IHub _sentry;
        private readonly ILogger<ReleaseService> _logger;

        private readonly RadarrOptions _config;

        static ReleaseService()
        {
            ReleaseLocks = new ConcurrentDictionary<Type, SemaphoreSlim>();
            ReleaseLocks.TryAdd(typeof(GithubReleaseSource), new SemaphoreSlim(1, 1));
            ReleaseLocks.TryAdd(typeof(AzureReleaseSource), new SemaphoreSlim(1, 1));
            ReleaseLocks.TryAdd(typeof(AppVeyorReleaseSource), new SemaphoreSlim(1, 1));
        }

        public ReleaseService(
            IServiceScopeFactory serviceScopeFactory, 
            IHub sentry, 
            ILogger<ReleaseService> logger,
            IOptions<RadarrOptions> configOptions)
        {
            _serviceScopeFactory = serviceScopeFactory;
            _sentry = sentry;
            _logger = logger;

            _config = configOptions.Value;
        }

        public async Task UpdateReleasesAsync(Type releaseSource)
        {
            if (!ReleaseLocks.TryGetValue(releaseSource, out var releaseLock))
            {
                throw new NotImplementedException($"{releaseSource} does not have a release lock.");
            }

            var obtainedLock = false;

            try
            {
                obtainedLock = await releaseLock.WaitAsync(TimeSpan.FromMinutes(5));

                if (obtainedLock)
                {
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        var releaseSourceInstance = (ReleaseSourceBase) scope.ServiceProvider.GetRequiredService(releaseSource);

                        await releaseSourceInstance.StartFetchReleasesAsync();
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
    }
}
