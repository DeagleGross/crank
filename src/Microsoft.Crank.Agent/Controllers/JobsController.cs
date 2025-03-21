﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Crank.Models;
using Repository;

namespace Microsoft.Crank.Agent.Controllers
{
    [Route("[controller]")]
    public class JobsController : Controller
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        private readonly IJobRepository _jobs;

        public JobsController(IJobRepository jobs)
        {
            _jobs = jobs;
        }

        [HttpGet("all")]
        public IEnumerable<JobResult> GetAll()
        {
            return _jobs.GetAll().Select(x => new JobResult(x, this.Url));
        }

        [HttpGet("")]
        public IEnumerable<JobResult> GetQueue()
        {
            return _jobs.GetAll().Where(IsActive).Select(x => new JobResult(x, this.Url));

            bool IsActive(Job job)
            {
                return job.State == JobState.New
                    || job.State == JobState.Waiting
                    || job.State == JobState.Initializing
                    || job.State == JobState.Starting
                    || job.State == JobState.Running
                    ;
            }
        }

        [HttpGet("{id}")]
        public IActionResult GetById(int id)
        {
            var job = _jobs.Find(id);
            if (job == null)
            {
                return NotFound();
            }
            else
            {
                return new ObjectResult(job);
            }
        }

        [HttpGet("info")]
        public IActionResult Info()
        {
            return Json(new
            {
                hw = Startup.Hardware,
                env = Startup.HardwareVersion,
                os = Startup.OperatingSystem,
                arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                proc = Environment.ProcessorCount
            });
        }

        [HttpPost]
        public IActionResult Create([FromBody] Job job)
        {
            if (job == null || job.Id != 0)
            {
                return BadRequest();
            }
            else if (job.DriverVersion < 1)
            {
                return BadRequest("The driver is not compatible with this server, please update it.");
            }
            else if (job.State != JobState.New)
            {
                return BadRequest("The job state should be ServerState.New. You are probably using a wrong version of the driver.");
            }

            job.Hardware = Startup.Hardware;
            job.HardwareVersion = Startup.HardwareVersion;
            job.OperatingSystem = Startup.OperatingSystem;
            // Use server-side date and time to prevent issues from time drifting
            job.LastDriverCommunicationUtc = DateTime.UtcNow;
            job = _jobs.Add(job);
            job.Url = Url.ActionLink("GetById", "Jobs", new { job.Id });

            Response.Headers["Location"] = $"/jobs/{job.Id}";
            return new StatusCodeResult((int)HttpStatusCode.Accepted);
        }

        [HttpDelete("{id}")]
        public IActionResult Delete(int id)
        {
            try
            {
                Log.Info($"Driver deleting job '{id}'");
                var job = _jobs.Find(id);

                if (job == null)
                {
                    return NoContent();
                }

                job.State = JobState.Deleting;
                _jobs.Update(job);

                Response.Headers["Location"] = $"/jobs/{job.Id}";
                return new StatusCodeResult((int)HttpStatusCode.Accepted);
            }
            catch (Exception e)
            {
                Log.Error(e, $"Error while deleting job '{id}' ");
                return NotFound();
            }
        }

        [HttpPost("{id}/stop")]
        public IActionResult Stop(int id, bool collectDump = false)
        {
            Log.Info($"Driver stopping job '{id}'");

            try
            {
                var job = _jobs.Find(id);

                if (job == null)
                {
                    // The job doesn't exist anymore
                    return NotFound();
                }

                if (job.State == JobState.Stopped || job.State == JobState.Failed
                    || job.State == JobState.Deleting || job.State == JobState.Deleted)
                {
                    // The job might have already been stopped, or deleted.
                    // Can happen if the server stops the job, then the driver does it.
                    // If the benchmark failed, it will be marked as Stopping automatically.
                    return new StatusCodeResult((int)HttpStatusCode.Accepted);
                }

                if (collectDump)
                {
                    job.DumpProcess = true;
                }

                job.State = JobState.Stopping;
                _jobs.Update(job);

                Response.Headers["Location"] = $"/jobs/{job.Id}";
                return new StatusCodeResult((int)HttpStatusCode.Accepted);
            }
            catch (Exception e)
            {
                Log.Error($"Error while stopping job '{id}' " + e.Message);
                return NotFound();
            }
        }

        [HttpPost("{id}/resetstats")]
        public IActionResult ResetStats(int id)
        {
            try
            {
                var job = _jobs.Find(id);
                job.Measurements.Clear();
                _jobs.Update(job);
                return Ok();
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpPost("{id}/measurements/flush")]
        public IActionResult FlushMeasurement(int id)
        {
            // Remove all the measurements before the first result marker

            try
            {
                var job = _jobs.Find(id);

                // No delimiter?
                if (!job.Measurements.Any(x => x.IsDelimiter))
                {
                    return Ok();
                }

                Measurement measurement;
                int count = 0;
                do
                {
                    job.Measurements.TryDequeue(out measurement);
                    count++;
                } while (!measurement.IsDelimiter);

                Log.Info($"Flushed {count} measurements");

                _jobs.Update(job);
                return Ok();
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpPost("{id}/trace")]
        public IActionResult TracePost(int id)
        {
            // This will trigger
            // - dump file collection
            // - native trace collection
            // - managed trace collection

            try
            {
                var job = _jobs.Find(id);
                job.State = JobState.TraceCollecting;
                _jobs.Update(job);

                Response.Headers["Location"] = $"/jobs/{job.Id}";
                return new StatusCodeResult((int)HttpStatusCode.Accepted);
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpPost("{id}/start")]
        public IActionResult Start(int id)
        {
            var job = _jobs.Find(id);

            
            if (job.State != JobState.Initializing)
            {
                Log.Info($"Start rejected, job is {job.State}");
                return StatusCode(500, $"The job can't be started as its state is {job.State}");
            }

            job.State = JobState.Waiting;
            _jobs.Update(job);

            return Ok();
        }

        [HttpPost("{id}/attachment")]
        [RequestSizeLimit(10_000_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 10_000_000_000)]
        public async Task<IActionResult> UploadAttachment(int id)
        {
            // Because uploaded files can be big, the client is supposed to keep
            // pinging the service to keep the job alive during the Initialize state.
            
            var destinationFilename = Request.Headers["destinationFilename"].ToString();

            Log.Info($"Uploading attachment: {destinationFilename}");

            var job = _jobs.Find(id);

            if (job == null)
            {
                return NotFound("Job doesn't exist anymore");
            }

            if (job.State != JobState.Initializing)
            {
                Log.Info($"Attachment rejected, job is {job.State}");
                return StatusCode(500, $"The job can't accept attachment as its state is {job.State}");
            }

            var tempFilename = Path.GetTempFileName();

            try
            {
                job.LastDriverCommunicationUtc = DateTime.UtcNow;
                await SaveBodyAsync(job, tempFilename);
            }
            catch
            {
                return StatusCode(500, "Error while saving file");
            }

            job.Attachments.Add(new Attachment
            {
                TempFilename = tempFilename,
                Filename = destinationFilename,
            });

            job.LastDriverCommunicationUtc = DateTime.UtcNow;

            return Ok();
        }

        [HttpPost("{id}/attachment/zip")]
        [RequestSizeLimit(10_000_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 10_000_000_000)]
        public async Task<IActionResult> UploadAttachmentZip(int id)
        {
            // Because uploaded files can be big, the client is supposed to keep
            // pinging the service to keep the job alive during the Initialize state.

            var destinationFilename = Request.Headers["destinationFilename"].ToString();

            Log.Info($"Uploading archive: {destinationFilename}");

            var job = _jobs.Find(id);

            if (job == null)
            {
                return NotFound("Job doesn't exist anymore");
            }

            if (job.State != JobState.Initializing)
            {
                Log.Info($"Attachment rejected, job is {job.State}");
                return StatusCode(500, $"The job can't accept attachment as its state is {job.State}");
            }

            var destinationTempFilename = Path.GetFullPath(Path.GetRandomFileName(), Path.GetTempPath());

            var tempFilename = Path.GetTempFileName() + ".zip";

            try
            {
                job.LastDriverCommunicationUtc = DateTime.UtcNow;
                await SaveBodyAsync(job, tempFilename);

                // Extract the zip file in a temporary folder
                ZipFile.ExtractToDirectory(tempFilename, destinationTempFilename);
            }
            catch
            {
                return StatusCode(500, "Error while saving file");
            }
            finally
            {
                System.IO.File.Delete(tempFilename);
            }

            job.LastDriverCommunicationUtc = DateTime.UtcNow;

            foreach (var file in Directory.GetFiles(destinationTempFilename, "*.*", SearchOption.AllDirectories))
            {
                var attachment = new Attachment
                {
                    TempFilename = file,
                    Filename = Path.Combine(Path.GetDirectoryName(destinationFilename), file.Substring(destinationTempFilename.Length).TrimStart('/', '\\'))
                };
                
                job.Attachments.Add(attachment);

                Log.Info($"Creating attachment: {attachment.Filename}");
            }

            job.LastDriverCommunicationUtc = DateTime.UtcNow;

            return Ok();
        }

        [HttpPost("{id}/source")]
        [RequestSizeLimit(10_000_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 10_000_000_000)]
        public async Task<IActionResult> UploadSource(int id, string sourceName = Source.DefaultSource)
        {
            var destinationFilename = Request.Headers["destinationFilename"].ToString();

            Log.Info($"Uploading source code");

            var job = _jobs.Find(id);

            if (!job.Sources.TryGetValue(sourceName, out var source))
                return NotFound("Source does not exist on job");

            var tempFilename = Path.GetTempFileName();

            try
            {
                job.LastDriverCommunicationUtc = DateTime.UtcNow;
                await SaveBodyAsync(job, tempFilename);
            }
            catch
            {
                return StatusCode(500, "Error while saving file");
            }

            source.SourceCode = new Attachment
            {
                TempFilename = tempFilename,
                Filename = destinationFilename,
            };

            job.LastDriverCommunicationUtc = DateTime.UtcNow;

            return Ok();
        }

        [HttpPost("{id}/build")]
        [RequestSizeLimit(10_000_000_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 10_000_000_000)]
        public async Task<IActionResult> UploadBuildFile(int id)
        {
            var destinationFilename = Request.Headers["destinationFilename"].ToString();

            Log.Info($"Uploading build file {destinationFilename}");

            var job = _jobs.Find(id);
            var tempFilename = Path.GetTempFileName();

            try
            {
                job.LastDriverCommunicationUtc = DateTime.UtcNow;
                await SaveBodyAsync(job, tempFilename);
            }
            catch
            {
                return StatusCode(500, "Error while saving file");
            }

            job.BuildAttachments.Add(new Attachment
            {
                TempFilename = tempFilename,
                Filename = destinationFilename,
            });

            job.LastDriverCommunicationUtc = DateTime.UtcNow;

            return Ok();
        }

        private async Task SaveBodyAsync(Job job, string filename)
        {
            using var outputFileStream = System.IO.File.Create(filename);

            Task task = null;

            if (Request.Headers.TryGetValue("Content-Encoding", out var encoding) && encoding.Contains("gzip"))
            {
                Log.Info($"Received gzipped file content");
                using var decompressor = new GZipStream(Request.Body, CompressionMode.Decompress);
                task = decompressor.CopyToAsync(outputFileStream, Request.HttpContext.RequestAborted);
            }
            else
            {
                Log.Info($"Received uncompressed file content");
                task = Request.Body.CopyToAsync(outputFileStream, Request.HttpContext.RequestAborted);
            }

            // Keeps controller's connection alive as PINGs might not be accepted
            // by the agent while big files are uploaded.

            while (!Request.HttpContext.RequestAborted.IsCancellationRequested && !task.IsCompleted)
            {
                await Task.Delay(500);
                job.LastDriverCommunicationUtc = DateTime.UtcNow;
            }

            await task;
        }

        [HttpGet("{id}/trace")]
        public IActionResult Trace(int id)
        {
            Log.Info($"Downloading trace for job {id}");

            try
            {
                var job = _jobs.Find(id);
                Log.Info($"Sending {job.PerfViewTraceFile}");
                
                if (!System.IO.File.Exists(job.PerfViewTraceFile))
                {
                    Log.Info("Trace file doesn't exist");
                    return NotFound();
                }

                return new GZipFileResult(job.PerfViewTraceFile);
            }
            catch(Exception e)
            {
                Log.Error(e);
                return NotFound();
            }
        }

        [HttpGet("{id}/dump")]
        public IActionResult Dump(int id)
        {
            Log.Info($"Downloading dump for job {id}");

            try
            {
                var job = _jobs.Find(id);
                Log.Info($"Sending {job.DumpFile}");

                if (!System.IO.File.Exists(job.DumpFile))
                {
                    Log.Info("Dump file doesn't exist");
                    return NotFound();
                }

                return new GZipFileResult(job.DumpFile);
            }
            catch (Exception e)
            {
                Log.Error(e);
                return NotFound();
            }
        }

        [HttpGet("{id}/buildlog")]
        public IActionResult BuildLog(int id)
        {
            try
            {
                var job = _jobs.Find(id);
                return Content(job.BuildLog.ToString());
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpGet("{id}/buildlog/{start}")]
        public IActionResult BuildLog(int id, int start)
        {
            try
            {
                var job = _jobs.Find(id);

                return Json(job.BuildLog.Get(start));
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpGet("{id}/output")]
        public IActionResult Output(int id)
        {
            try
            {
                var job = _jobs.Find(id);
                return Content(job.Output.ToString());
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpGet("{id}/output/{start}")]
        public IActionResult Output(int id, int start)
        {
            try
            {
                var job = _jobs.Find(id);

                return Json(job.Output.Get(start));
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpGet("{id}/eventpipe")]
        public IActionResult EventPipe(int id)
        {
            try
            {
                var job = _jobs.Find(id);

                foreach (var file in Directory.GetFiles(job.BasePath, "*.netperf"))
                {
                    return new GZipFileResult(file);
                }

                return NotFound();
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpGet("{id}/fetch")]
        public async Task<IActionResult> Fetch(int id)
        {
            try
            {
                var job = _jobs.Find(id);

                Log.Info($"Driver fetching published application '{id}'");

                if (String.IsNullOrEmpty(job.DockerFile))
                {
                    var zipPath = Path.Combine(Directory.GetParent(job.BasePath).FullName, "published.zip");
                    ZipFile.CreateFromDirectory(job.BasePath, zipPath);

                    return File(System.IO.File.OpenRead(zipPath), "application/object");
                }
                else
                {
                    var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
                    Directory.CreateDirectory(tempDirectory);

                    try
                    {
                        // docker cp mycontainer:/src/. target

                        if (String.IsNullOrEmpty(job.DockerFetchPath))
                        {
                            return BadRequest("The Docker fetch path was not provided in the job");
                        }

                        var sourceFolder = Path.Combine(job.DockerFetchPath, ".");

                        // Delete container if the same name already exists
                        await ProcessUtil.RunAsync("docker", $"cp {job.GetNormalizedImageName()}:{sourceFolder} {tempDirectory}", throwOnError: false);

                        var zipPath = Path.Combine(Directory.GetParent(job.BasePath).FullName, "fetch.zip");
                        ZipFile.CreateFromDirectory(tempDirectory, zipPath);

                        return File(System.IO.File.OpenRead(zipPath), "application/object");
                    }
                    finally
                    {
                        Response.RegisterForDispose(new TempFolder(tempDirectory));
                    }
                }
            }
            catch
            {
                return NotFound();
            }
        }

        [HttpGet("{id}/download")]
        public async Task<IActionResult> Download(int id, string path)
        {
            try
            {
                var job = _jobs.Find(id);

                if (job == null)
                {
                    return NotFound();
                }

                // Downloads can't get out of this path
                var rootPath = Directory.GetParent(job.BasePath).FullName;

                // Assume ~/ means relative to the project, otherwise it's relative to the application folder (job.BasePath)
                var isRelativeToProject = String.IsNullOrEmpty(job.DockerFile) && (path.StartsWith("~/") || path.StartsWith("~\\"));
                
                if (isRelativeToProject)
                {
                    // One level up from the output folder
                    path = string.Concat("..", path.AsSpan(1));
                }

                // Resolve dot notation in path
                var fullPath = Path.GetFullPath(path, job.BasePath);

                Log.Info($"Download requested: '{path}'");

                if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error($"Client is not allowed to download '{fullPath}'");
                    return BadRequest("Attempts to access a path outside of the job.");
                }

                if (String.IsNullOrEmpty(job.DockerFile))
                {
                    if (!System.IO.File.Exists(fullPath))
                    {
                        return NotFound();
                    }

                    Log.Info($"Uploading {path} ({new FileInfo(fullPath).Length / 1024 + 1} KB)");

                    return new GZipFileResult(fullPath);
                }
                else
                {
                    var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
                    Directory.CreateDirectory(tempDirectory);

                    try
                    {
                        // docker cp mycontainer:/foo.txt foo.txt 

                        var destinationFilename = Path.Combine(tempDirectory, Path.GetFileName(path));

                        // Delete container if the same name already exists
                        var result = await ProcessUtil.RunAsync("docker", [ "cp", $"{job.GetNormalizedImageName()}:{path}", destinationFilename ], throwOnError: false, log: true);

                        return new GZipFileResult(destinationFilename);
                    }
                    finally
                    {
                        Response.RegisterForDispose(new TempFolder(tempDirectory));
                    }
                }
            }
            catch (Exception e)
            {
                return StatusCode(500, e.Message);
            }
        }

        [HttpGet("{id}/list")]
        public IActionResult List(int id, string path)
        {
            try
            {
                var job = _jobs.Find(id);

                if (job == null)
                {
                    return NotFound();
                }

                // Downloads can't get out of this path
                var rootPath = Directory.GetParent(job.BasePath).FullName;

                // Assume ~/ means relative to the project, otherwise it's relative to the application folder (job.BasePath)
                var isRelativeToProject = String.IsNullOrEmpty(job.DockerFile) && (path.StartsWith("~/") || path.StartsWith("~\\"));

                if (isRelativeToProject)
                {
                    // One level up from the output folder
                    path = string.Concat("..", path.AsSpan(1));
                }

                // Resolve dot notation in path
                var fullPath = Path.GetFullPath(path, job.BasePath);

                if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error($"Client is not allowed to list '{fullPath}'");
                    return BadRequest("Attempts to access a path outside of the job.");
                }

                if (!Directory.Exists(Path.GetDirectoryName(fullPath)))
                {
                    return Json(Array.Empty<string>());
                }

                // Returned paths are relative to the job.BasePath folder (e.g., /published in case of dotnet projects)
                if (fullPath.Contains("*"))
                {
                    return Json(
                        Directory.GetFiles(Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath))
                        .Select(x => x.Substring(job.BasePath.Length).TrimStart('/', '\\'))
                        .ToArray()
                        );
                }
                else
                {
                    return Json(
                        Directory.GetFiles(Path.GetDirectoryName(fullPath))
                        .Select(x => x.Substring(job.BasePath.Length).TrimStart('/', '\\'))
                        .ToArray()
                        );
                }
            }
            catch (Exception e)
            {
                return StatusCode(500, e.Message);
            }
        }

        [HttpGet("{id}/invoke")]
        public async Task<IActionResult> Invoke(int id, string path)
        {
            try
            {
                var job = _jobs.Find(id);
                var response = await _httpClient.GetStringAsync(new Uri(new Uri(job.Url), path));
                return Content(response);
            }
            catch (Exception e)
            {
                return StatusCode(500, e.ToString());
            }
        }

        [HttpGet("services/{service}")]
        public IActionResult LatestService(string service)
        {
            lock (_jobs)
            {
                return DetailResult(() => GetLatestJob(service));
            }
        }

        [HttpGet("services/{service}/measurements")]
        public IActionResult LatestServiceMeasurements(string service, string path)
        {
            lock (_jobs)
            {
                var filter = this.Request.Query["name"];

                IEnumerable<Measurement> measurements = null;

                if (filter.Any())
                {
                    measurements = GetLatestJob(service)
                        ?.Measurements
                        ?.Where(x => x.Name.Equals(filter.First(), StringComparison.OrdinalIgnoreCase))
                        ;
                }
                else
                {
                    measurements = GetLatestJob(service)
                    ?.Measurements
                    ;

                }

                return ObjectOrNotFoundResult(measurements);
            }
        }

        [HttpGet("services/{service}/measurements/last")]
        public IActionResult LatestServiceLatestMeasurement(string service, string path)
        {
            var filter = this.Request.Query["name"];

            Measurement measurement = null;

            if (filter.Any())
            {
                measurement = GetLatestJob(service)
                    ?.Measurements
                    ?.LastOrDefault(x => x.Name.Equals(filter.First(), StringComparison.OrdinalIgnoreCase))
                    ;
            }
            else
            {
                measurement = GetLatestJob(service)
                    ?.Measurements
                    ?.LastOrDefault()
                    ;
            }

            return ObjectOrNotFoundResult(measurement);
        }

        [HttpPost("{id}/statistics")]
        public IActionResult PostStatistics(int id, [FromBody] JobStatistics statistics)
        {
            Log.Info($"Receiving statistics for job {id}");
            Log.Info($"statistics: {JsonSerializer.Serialize(statistics)}");

            try
            {
                var job = _jobs.Find(id);

                if (job == null)
                {
                    return NotFound("Job not found");
                }

                Log.Info($"Found {statistics.Metadata.Count} metadata and {statistics.Measurements.Count} measurements");

                foreach (var metadata in statistics.Metadata)
                {
                    job.Metadata.Enqueue(metadata);
                }

                foreach (var measurement in statistics.Measurements)
                {
                    job.Measurements.Enqueue(measurement);
                }

                return Ok();
            }
            catch (Exception e)
            {
                Log.Error(e);
                return StatusCode(500, e);
            }
        }

        [HttpPost("{id}/metadata")]
        public IActionResult PostMetadata(int id, string name, Operation aggregate, Operation reduce, string format, string longDescription, string shortDescription)
        {
            Log.Info($"Receiving metadata for job {id}");
            Log.Info($"name: {name}, aggregate: {aggregate}, reduce: {reduce}, format: {format}, longDescription: {longDescription}, shortDescription: {shortDescription} ");

            try
            {
                var job = _jobs.Find(id);

                if (job == null)
                {
                    return NotFound("Job not found");
                }

                var metadata = new MeasurementMetadata
                {
                    Name = name,
                    Aggregate = aggregate,
                    Reduce = reduce,
                    Format = format,
                    LongDescription = longDescription,
                    ShortDescription = shortDescription
                };

                job.Metadata.Enqueue(metadata);

                return Ok();
            }
            catch (Exception e)
            {
                Log.Error(e);
                return StatusCode(500, e);
            }
        }

        [HttpPost("{id}/measurement")]
        public IActionResult PostMeasurement(int id, string name, string timestamp, string value)
        {
            Log.Info($"Receiving measurement for job {id}");
            Log.Info($"name: {name}, timestamp: {timestamp}, value: {value}");

            try
            {
                var job = _jobs.Find(id);

                if (job == null)
                {
                    return NotFound("Job not found");
                }

                var measurement = new Measurement
                {
                    Name = name,
                    Timestamp = DateTime.Parse(timestamp).ToUniversalTime(),
                    Value = decimal.TryParse(value, out var result) ? result : value
                };

                job.Measurements.Enqueue(measurement);

                return Ok();
            }
            catch (Exception e)
            {
                Log.Error(e);
                return StatusCode(500, e);
            }
        }

        private Job GetLatestJob(string name)
        {
            return _jobs
                .GetAll()
                .OrderByDescending(x => x.Id)
                .FirstOrDefault(x => x.Service.Equals(name, StringComparison.OrdinalIgnoreCase))
                ;
        }

        private class TempFolder : IDisposable
        {
            private readonly string _folder;

            public TempFolder(string folder)
            {
                _folder = folder;
            }
            public void Dispose()
            {
                try
                {
                    Directory.Delete(_folder, true);
                }
                catch
                {
                    Log.Error("Could not delete temporary folder: " + _folder);
                }
            }
        }

        private IActionResult DetailResult(Func<Job> filter)
        {
            var job = filter();

            if (job == null)
            {
                return NotFound();
            }

            return new ObjectResult(job);
        }

        private IActionResult ObjectOrNotFoundResult(object obj)
        {
            if (obj == null)
            {
                return NotFound();
            }

            return new ObjectResult(obj);
        }
    }
}
