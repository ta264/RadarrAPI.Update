﻿using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Octokit;
using RadarrAPI.Database;
using RadarrAPI.Database.Models;
using RadarrAPI.Options;
using RadarrAPI.Util;
using Branch = RadarrAPI.Update.Branch;
using OperatingSystem = RadarrAPI.Update.OperatingSystem;

namespace RadarrAPI.Services.ReleaseCheck.Github
{
    public class GithubReleaseSource : ReleaseSourceBase
    {
        private readonly DatabaseContext _database;
        
        private readonly RadarrOptions _config;

        private readonly GitHubClient _gitHubClient;
        
        private readonly HttpClient _httpClient;

        public GithubReleaseSource(DatabaseContext database, IOptions<RadarrOptions> config)
        {
            _database = database;
            _config = config.Value;
            _gitHubClient = new GitHubClient(new ProductHeaderValue("RadarrAPI"));
            _httpClient = new HttpClient();
        }

        protected override async Task<bool> DoFetchReleasesAsync()
        {
            if (ReleaseBranch == Branch.Unknown)
            {
                throw new ArgumentException("ReleaseBranch must not be unknown when fetching releases.");
            }

            var hasNewRelease = false;

            var releases = (await _gitHubClient.Repository.Release.GetAll("Radarr", "Radarr")).ToArray();
            var validReleases = releases
                .Take(3)
                .Where(r =>
                    r.TagName.StartsWith("v") && VersionUtil.IsValid(r.TagName.Substring(1)) &&
                    r.Prerelease == (ReleaseBranch == Branch.Develop)
                ).Reverse();

            foreach (var release in validReleases)
            {
                // Check if release has been published.
                if (!release.PublishedAt.HasValue) continue;

                var version = release.TagName.Substring(1);

                // Get an updateEntity
                var updateEntity = _database.UpdateEntities
                    .Include(x => x.UpdateFiles)
                    .FirstOrDefault(x => x.Version.Equals(version) && x.Branch.Equals(ReleaseBranch));

                if (updateEntity == null)
                {
                    // Create update object
                    updateEntity = new UpdateEntity
                    {
                        Version = version,
                        ReleaseDate = release.PublishedAt.Value.UtcDateTime,
                        Branch = ReleaseBranch
                    };

                    // Start tracking this object
                    await _database.AddAsync(updateEntity);

                    // Set new release to true.
                    hasNewRelease = true;
                }

                // Parse changes
                var releaseBody = release.Body;

                var features = RegexUtil.ReleaseFeaturesGroup.Match(releaseBody);
                if (features.Success)
                {
                    updateEntity.New.Clear();

                    foreach (Match match in RegexUtil.ReleaseChange.Matches(features.Groups["features"].Value))
                    {
                        if (match.Success)
                        {
                            updateEntity.New.Add(match.Groups["text"].Value);
                        }
                    }
                }

                var fixes = RegexUtil.ReleaseFixesGroup.Match(releaseBody);
                if (fixes.Success)
                {
                    updateEntity.Fixed.Clear();

                    foreach (Match match in RegexUtil.ReleaseChange.Matches(fixes.Groups["fixes"].Value))
                    {
                        if (match.Success)
                        {
                            updateEntity.Fixed.Add(match.Groups["text"].Value);
                        }
                    }
                }

                // Process release files.
                foreach (var releaseAsset in release.Assets)
                {
                    var operatingSystem = Parser.ParseOS(releaseAsset.Name);
                    if (!operatingSystem.HasValue)
                    {
                        continue;
                    }

                    var runtime = Parser.ParseRuntime(releaseAsset.Name);
                    var arch = Parser.ParseArchitecture(releaseAsset.Name);

                    // Check if exists in database.
                    var updateFileEntity = _database.UpdateFileEntities
                        .FirstOrDefault(x =>
                            x.UpdateEntityId == updateEntity.UpdateEntityId &&
                            x.OperatingSystem == operatingSystem.Value &&
                            x.Runtime == runtime &&
                            x.Architecture == arch);

                    if (updateFileEntity != null) continue;

                    // Calculate the hash of the zip file.
                    var releaseZip = Path.Combine(_config.DataDirectory, ReleaseBranch.ToString(), releaseAsset.Name);
                    string releaseHash;

                    if (!File.Exists(releaseZip))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(releaseZip));
                        
                        using (var fileStream = File.OpenWrite(releaseZip))
                        using (var artifactStream = await _httpClient.GetStreamAsync(releaseAsset.BrowserDownloadUrl))
                        {
                            await artifactStream.CopyToAsync(fileStream);
                        }
                    }

                    using (var stream = File.OpenRead(releaseZip))
                    {
                        using (var sha = SHA256.Create())
                        {
                            releaseHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLower();
                        }
                    }

                    File.Delete(releaseZip);

                    // Add to database.
                    updateEntity.UpdateFiles.Add(new UpdateFileEntity
                    {
                        OperatingSystem = operatingSystem.Value,
                        Architecture = arch,
                        Runtime = runtime,
                        Filename = releaseAsset.Name,
                        Url = releaseAsset.BrowserDownloadUrl,
                        Hash = releaseHash
                    });
                }

                // Save all changes to the database.
                await _database.SaveChangesAsync();
            }

            return hasNewRelease;
        }
    }
}
