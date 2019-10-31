﻿using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LidarrAPI.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.Build.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Octokit;
using RadarrAPI.Database;
using RadarrAPI.Database.Models;
using RadarrAPI.Options;
using RadarrAPI.Util;

namespace RadarrAPI.Services.ReleaseCheck.Azure
{
    public class AzureReleaseSource : ReleaseSourceBase
    {
        private const string AccountName = "Radarr";
        private const string ProjectSlug = "Radarr";
        private const string PackageArtifactName = "Packages";
        private readonly int[] BuildPipelines = new int[] { 1 };

        private static int? _lastBuildId;

        private readonly DatabaseContext _database;
        private readonly RadarrOptions _config;
        private readonly GitHubClient _githubClient;
        private readonly HttpClient _httpClient;
        private readonly VssConnection _connection;
        private readonly ILogger<AzureReleaseSource> _logger;

        private static readonly Regex ReleaseFeaturesGroup = new Regex(@"^New:\s*(?<text>.*?)\r*$", RegexOptions.Compiled);
        private static readonly Regex ReleaseFixesGroup = new Regex(@"^Fixed:\s*(?<text>.*?)\r*$", RegexOptions.Compiled);

        public AzureReleaseSource(DatabaseContext database,
                                  IHttpClientFactory httpClientFactory,
                                  IOptions<RadarrOptions> config,
                                  ILogger<AzureReleaseSource> logger)
        {
            _database = database;
            _config = config.Value;
            _httpClient = httpClientFactory.CreateClient();
            _connection = new VssConnection(new Uri($"https://dev.azure.com/{AccountName}"), new VssBasicCredential());
            _githubClient = new GitHubClient(new ProductHeaderValue("RadarrAPI"));
            _httpClient = new HttpClient();
            _logger = logger;
        }

        protected override async Task<bool> DoFetchReleasesAsync()
        {
            var hasNewRelease = false;

            var buildClient = _connection.GetClient<BuildHttpClient>();
            var nightlyHistory = await buildClient.GetBuildsAsync(project: ProjectSlug,
                                                                  definitions: BuildPipelines,
                                                                  branchName: "refs/heads/aphrodite",
                                                                  reasonFilter: BuildReason.IndividualCI | BuildReason.Manual,
                                                                  statusFilter: BuildStatus.Completed,
                                                                  resultFilter: BuildResult.Succeeded,
                                                                  queryOrder: BuildQueryOrder.StartTimeDescending,
                                                                  top: 5);

            var prHistory = await buildClient.GetBuildsAsync(project: ProjectSlug,
                                                             definitions: BuildPipelines,
                                                             reasonFilter: BuildReason.PullRequest | BuildReason.Manual,
                                                             statusFilter: BuildStatus.Completed,
                                                             resultFilter: BuildResult.Succeeded,
                                                             queryOrder: BuildQueryOrder.StartTimeDescending,
                                                             top: 5);

            var history = nightlyHistory.Concat(prHistory).DistinctBy(x => x.Id).OrderByDescending(x => x.Id);

            // Store here temporarily so we don't break on not processed builds.
            var lastBuild = _lastBuildId;

            // URL query has filtered to most recent 5 successful, completed builds
            foreach (var build in history)
            {
                if (lastBuild.HasValue && lastBuild.Value >= build.Id)
                {
                    break;
                }

                // Extract the build version
                _logger.LogInformation($"Found version: {build.BuildNumber}");

                // Get the branch - either PR source branch or the actual brach
                string branch = null;
                if (build.SourceBranch.StartsWith("refs/heads/"))
                {
                    branch = build.SourceBranch.Replace("refs/heads/", string.Empty);
                }
                else if (build.SourceBranch.StartsWith("refs/pull/"))
                {
                    var success = int.TryParse(build.SourceBranch.Split("/")[2], out var prNum);
                    if (!success)
                    {
                        continue;
                    }

                    var pr = await _githubClient.PullRequest.Get(AccountName, ProjectSlug, prNum);

                    if (pr.Head.Repository.Fork)
                    {
                        continue;
                    }

                    branch = pr.Head.Ref;
                }
                else
                {
                    continue;
                }

                // If the branch is call nightly (conflicts with daily develop builds)
                // or branch is called master (will get picked up when the github release goes up)
                // then skip
                if (branch == "nightly" || branch == "master")
                {
                    _logger.LogInformation($"Skipping azure build with branch {branch}");
                    continue;
                }

                // On azure, develop -> nightly
                if (branch == "develop")
                {
                    branch = "nightly";
                }

                _logger.LogInformation($"Found branch for version {build.BuildNumber}: {branch}");

                // Get build changes
                var changesTask = buildClient.GetBuildChangesAsync(ProjectSlug, build.Id);

                // Grab artifacts
                var artifacts = await buildClient.GetArtifactsAsync(ProjectSlug, build.Id);

                // there should be a single artifact called 'Packages' we parse for packages
                var artifact = artifacts.FirstOrDefault(x => x.Name == PackageArtifactName);
                if (artifact == null)
                {
                    continue;
                }

                var artifactClient = _connection.GetClient<ArtifactHttpClient>();
                var files = await artifactClient.GetArtifactFiles(ProjectSlug, build.Id, artifact);

                // Get an updateEntity
                var updateEntity = _database.UpdateEntities
                    .Include(x => x.UpdateFiles)
                    .FirstOrDefault(x => x.Version.Equals(build.BuildNumber) && x.Branch.Equals(branch));

                if (updateEntity == null)
                {
                    // Create update object
                    updateEntity = new UpdateEntity
                    {
                        Version = build.BuildNumber,
                        ReleaseDate = build.StartTime.Value,
                        Branch = branch
                    };

                    // Start tracking this object
                    await _database.AddAsync(updateEntity);

                    // Set new release to true.
                    hasNewRelease = true;
                }

                // Parse changes
                var changes = await changesTask;
                var features = changes.Select(x => ReleaseFeaturesGroup.Match(x.Message));
                if (features.Any(x => x.Success))
                {
                    updateEntity.New.Clear();

                    foreach (Match match in features.Where(x => x.Success))
                    {
                        updateEntity.New.Add(match.Groups["text"].Value);
                    }
                }

                var fixes = changes.Select(x => ReleaseFixesGroup.Match(x.Message));
                if (fixes.Any(x => x.Success))
                {
                    updateEntity.Fixed.Clear();

                    foreach (Match match in fixes.Where(x => x.Success))
                    {
                        updateEntity.Fixed.Add(match.Groups["text"].Value);
                    }
                }

                // Process artifacts
                foreach (var file in files)
                {
                    // Detect target operating system.
                    var operatingSystem = Parser.ParseOS(file.Path);
                    if (!operatingSystem.HasValue)
                    {
                        continue;
                    }

                    // Detect runtime / arch
                    var runtime = Parser.ParseRuntime(file.Path);
                    var arch = Parser.ParseArchitecture(file.Path);

                    // Check if exists in database.
                    var updateFileEntity = _database.UpdateFileEntities
                        .FirstOrDefault(x =>
                            x.UpdateEntityId == updateEntity.UpdateEntityId &&
                            x.OperatingSystem == operatingSystem.Value &&
                            x.Runtime == runtime &&
                            x.Architecture == arch);

                    if (updateFileEntity != null) continue;

                    // Calculate the hash of the zip file.
                    var releaseFileName = Path.GetFileName(file.Path);
                    var releaseZip = Path.Combine(_config.DataDirectory, branch, releaseFileName);
                    string releaseHash;

                    if (!File.Exists(releaseZip))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(releaseZip));

                        using (var fileStream = File.OpenWrite(releaseZip))
                        using (var artifactStream = await _httpClient.GetStreamAsync(file.Url))
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
                        Filename = releaseFileName,
                        Url = file.Url,
                        Hash = releaseHash
                    });
                }

                // Save all changes to the database.
                await _database.SaveChangesAsync();

                // Make sure we atleast skip this build next time.
                if (_lastBuildId == null ||
                    _lastBuildId.Value < build.Id)
                {
                    _lastBuildId = build.Id;
                }
            }

            return hasNewRelease;
        }
    }
}
