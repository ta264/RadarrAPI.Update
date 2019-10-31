﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RadarrAPI.Options;
using RadarrAPI.Services.BackgroundTasks;
using RadarrAPI.Services.ReleaseCheck;
using RadarrAPI.Services.ReleaseCheck.AppVeyor;
using RadarrAPI.Services.ReleaseCheck.Azure;
using RadarrAPI.Services.ReleaseCheck.Github;

namespace RadarrAPI.Controllers
{
    [Route("v1/[controller]")]
    public class WebhookController
    {
        private readonly IBackgroundTaskQueue _queue;

        private readonly RadarrOptions _config;

        public WebhookController(IBackgroundTaskQueue queue, IOptions<RadarrOptions> optionsConfig)
        {
            _queue = queue;
            _config = optionsConfig.Value;
        }

        [Route("refresh")]
        [HttpGet, HttpPost]
        public string Refresh([FromQuery] string source, [FromQuery(Name = "api_key")] string apiKey)
        {
            if (!_config.ApiKey.Equals(apiKey))
            {
                return "No, thank you.";
            }

            var type = source.ToLower() switch
            {
                "appveyor" => typeof(AppVeyorReleaseSource),
                "azure" => typeof(AzureReleaseSource),
                "github" => typeof(GithubReleaseSource),
                _ => null
            };

            _queue.QueueBackgroundWorkItem(async (serviceProvider, token) =>
            {
                var releaseService = serviceProvider.GetRequiredService<ReleaseService>();
                await releaseService.UpdateReleasesAsync(type);
            });

            return "Thank you.";
        }
    }
}
