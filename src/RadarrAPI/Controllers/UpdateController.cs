﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RadarrAPI.Database;
using RadarrAPI.Update.Data;
using Branch = RadarrAPI.Update.Branch;
using OperatingSystem = RadarrAPI.Update.OperatingSystem;
using Runtime = RadarrAPI.Update.Runtime;
using Architecture = System.Runtime.InteropServices.Architecture;
using System.Linq.Expressions;
using RadarrAPI.Database.Models;

namespace RadarrAPI.Controllers
{
    [Route("v1/[controller]")]
    public class UpdateController : Controller
    {
        private readonly DatabaseContext _database;

        public UpdateController(DatabaseContext database)
        {
            _database = database;
        }

        private IQueryable<UpdateFileEntity> GetUpdateFiles(Branch branch, OperatingSystem os, Runtime runtime, Architecture arch)
        {
            // Mono and Dotnet are equivalent for our purposes
            if (runtime == Runtime.Mono)
            {
                runtime = Runtime.DotNet;
            }

            // If runtime is DotNet then default arch to x64
            if (runtime == Runtime.DotNet)
            {
                arch = Architecture.X64;
            }

            Expression<Func<UpdateFileEntity, bool>> predicate;

            // Return whatever runtime/arch for macos and windows
            // Choose correct runtime/arch for linux
            if (os == OperatingSystem.Linux)
            {
                predicate = (x) => x.Update.Branch == branch &&
                    x.OperatingSystem == os &&
                    x.Architecture == arch &&
                    x.Runtime == runtime;
            }
            else
            {
                predicate = (x) => x.Update.Branch == branch &&
                    x.OperatingSystem == os;
            }

            return _database.UpdateFileEntities
                .Include(x => x.Update)
                .Where(predicate)
                .OrderByDescending(x => x.Update.ReleaseDate);
        }

        [Route("{branch}/changes")]
        [HttpGet]
        public ActionResult GetChanges(
            [FromRoute(Name = "branch")] Branch updateBranch,
            [FromQuery(Name = "os")] OperatingSystem operatingSystem,
            [FromQuery(Name = "runtime")] Runtime runtime = Runtime.DotNet,
            [FromQuery(Name = "arch")] Architecture arch = Architecture.X64
            )
        {
            var updateFiles = GetUpdateFiles(updateBranch, operatingSystem, runtime, arch).Take(5);

            var response = new List<UpdatePackage>();

            foreach (var updateFile in updateFiles)
            {
                var update = updateFile.Update;
                UpdateChanges updateChanges = null;

                if (update.New.Count != 0 || update.Fixed.Count != 0)
                {
                    updateChanges = new UpdateChanges
                    {
                        New = update.New,
                        Fixed = update.Fixed
                    };
                }

                response.Add(new UpdatePackage
                {
                    Version = update.Version,
                    ReleaseDate = update.ReleaseDate,
                    Filename = updateFile.Filename,
                    Url = updateFile.Url,
                    Changes = updateChanges,
                    Hash = updateFile.Hash,
                    Branch = update.Branch.ToString().ToLower()
                });
            }

            return Ok(response);
        }

        [Route("{branch}")]
        [HttpGet]
        public ActionResult GetUpdates([FromRoute(Name = "branch")] Branch updateBranch,
                                 [FromQuery(Name = "version")] string urlVersion,
                                 [FromQuery(Name = "os")] OperatingSystem operatingSystem,
                                 [FromQuery(Name = "runtime")] Runtime runtime,
                                 [FromQuery(Name = "arch")] Architecture arch)
        {
            // Check given version
            if (!Version.TryParse(urlVersion, out var version))
            {
                return BadRequest(new
                {
                    ErrorMessage = "Invalid version number specified."
                });
            }

            var updateFile = GetUpdateFiles(updateBranch, operatingSystem, runtime, arch).FirstOrDefault();

            if (updateFile == null)
            {
                return NotFound(new
                {
                    ErrorMessage = "Latest update not found."
                });
            }

            var update = updateFile.Update;

            // Compare given version and update version
            var updateVersion = new Version(update.Version);
            if (updateVersion.CompareTo(version) <= 0)
            {
                return Ok(new UpdatePackageContainer
                {
                    Available = false
                });
            }

            // Get the update changes
            UpdateChanges updateChanges = null;

            if (update.New.Count != 0 || update.Fixed.Count != 0)
            {
                updateChanges = new UpdateChanges
                {
                    New = update.New,
                    Fixed = update.Fixed
                };
            }

            return Ok(new UpdatePackageContainer
            {
                Available = true,
                UpdatePackage = new UpdatePackage
                {
                    Version = update.Version,
                    ReleaseDate = update.ReleaseDate,
                    Filename = updateFile.Filename,
                    Url = updateFile.Url,
                    Changes = updateChanges,
                    Hash = updateFile.Hash,
                    Branch = update.Branch.ToString().ToLower(),
                    Runtime = updateFile.Runtime.ToString().ToLower()
                }
            });
        }

        [Route("{branch}/updatefile")]
        [HttpGet]
        public object GetUpdateFile([FromRoute(Name = "branch")] Branch updateBranch,
                                    [FromQuery(Name = "version")] string urlVersion,
                                    [FromQuery(Name = "os")] OperatingSystem operatingSystem,
                                    [FromQuery(Name = "runtime")] Runtime runtime,
                                    [FromQuery(Name = "arch")] Architecture arch)
        {
            // Check given version
            if (!Version.TryParse(urlVersion, out Version version))
            {
                return new
                {
                    ErrorMessage = "Invalid version number specified."
                };
            }

            var updateFile = GetUpdateFiles(updateBranch, operatingSystem, runtime, arch).FirstOrDefault();

            if (updateFile == null)
            {
                return new
                    {
                        ErrorMessage = $"Version {version} not found."
                    };
            }

            return RedirectPermanent(updateFile.Url);
        }
    }
}
