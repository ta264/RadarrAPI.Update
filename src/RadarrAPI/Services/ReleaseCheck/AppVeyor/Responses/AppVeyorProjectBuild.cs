﻿using System;
using Newtonsoft.Json;

namespace RadarrAPI.Services.ReleaseCheck.AppVeyor.Responses
{
    public class AppVeyorProjectBuild
    {
        [JsonProperty("branch")]
        public string Branch { get; set; }

        [JsonProperty("buildId")]
        public int BuildId { get; set; }

        [JsonProperty("jobs")]
        public AppVeyorJob[] Jobs { get; set; }

        [JsonProperty("version", Required = Required.Always)]
        public string Version { get; set; }

        [JsonProperty("message", Required = Required.Always)]
        public string Message { get; set; }

        [JsonProperty("messageExtended")]
        public string MessageExtended { get; set; }

        [JsonProperty("isTag", Required = Required.Always)]
        public bool IsTag { get; set; }

        [JsonProperty("pullRequestId")]
        public int? PullRequestId { get; set; }

        [JsonProperty("status", Required = Required.Always)]
        public string Status { get; set; }
        
        public DateTimeOffset? Started { get; set; }

    }
}
