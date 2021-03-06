﻿using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json.Linq;
using NuGet.Jobs.Common;
using NuGet.Services.Metadata.Catalog;
using NuGet.Services.Metadata.Catalog.Collecting;
using NuGet.Services.Metadata.Catalog.Persistence;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.Http;
using System.Threading.Tasks;

namespace CatalogCollector.PackageRegistrationBlobs
{
    public class Job : JobBase
    {
        private JobEventSource JobEventSourceLog = JobEventSource.Log;
        public CloudStorageAccount TargetStorageAccount { get; set; }
        public string TargetStoragePath { get; set; }
        public string TargetBaseAddress { get; set; }
        public string TargetLocalDirectory { get; set; }
        public string CatalogIndexUrl { get; set; }
        public string CatalogIndexPath { get; set; }
        public string CdnBaseAddress { get; set; }
        public string GalleryBaseAddress { get; set; }
        public bool DontStoreCursor { get; set; }

        public Job() : base(JobEventSource.Log) { }

        public override bool Init(IDictionary<string, string> jobArgsDictionary)
        {
            try
            {
                // Init member variables
                // This is mandatory. Don't try to get it. Let it throw if not found
                TargetBaseAddress =
                    JobConfigManager.GetArgument(jobArgsDictionary, JobArgumentNames.TargetBaseAddress);

                // This is mandatory. Don't try to get it. Let it throw if not found
                CdnBaseAddress =
                    JobConfigManager.GetArgument(jobArgsDictionary, JobArgumentNames.CdnBaseAddress);

                // This is mandatory. Don't try to get it. Let it throw if not found
                GalleryBaseAddress =
                    JobConfigManager.GetArgument(jobArgsDictionary, JobArgumentNames.GalleryBaseAddress);

                string targetStorageConnectionString = JobConfigManager.TryGetArgument(
                            jobArgsDictionary, JobArgumentNames.TargetStorageAccount, EnvironmentVariableKeys.StoragePrimary);
                TargetStorageAccount = String.IsNullOrEmpty(targetStorageConnectionString) ? null : CloudStorageAccount.Parse(targetStorageConnectionString);

                CatalogIndexUrl =
                    JobConfigManager.TryGetArgument(jobArgsDictionary, JobArgumentNames.CatalogIndexUrl);

                TargetStoragePath =
                    JobConfigManager.TryGetArgument(jobArgsDictionary, JobArgumentNames.TargetStoragePath);

                CatalogIndexPath =
                    JobConfigManager.TryGetArgument(jobArgsDictionary, JobArgumentNames.CatalogIndexPath);

                TargetLocalDirectory =
                    JobConfigManager.TryGetArgument(jobArgsDictionary, JobArgumentNames.TargetLocalDirectory);

                DontStoreCursor =
                    JobConfigManager.TryGetBoolArgument(jobArgsDictionary, JobArgumentNames.DontStoreCursor);

                // Initialized successfully, return true
                return true;
            }
            catch (Exception ex)
            {
                Trace.TraceError(ex.ToString());
            }
            return false;
        }

        public override async Task<bool> Run()
        {
            if (DontStoreCursor)
            {
                Trace.TraceWarning("REMEMBER that you requested NOT to store cursor at the end");
            }

            try
            {
                // Set defaults

                // Check required payload
                ArgCheck.Require(TargetBaseAddress, "TargetBaseAddress");
                ArgCheck.Require(CdnBaseAddress, "CdnBaseAddress");
                ArgCheck.Require(GalleryBaseAddress, "GalleryBaseAddress");

                // Clean input data
                if (!TargetBaseAddress.EndsWith("/"))
                {
                    TargetBaseAddress += "/";
                }
                var resolverBaseUri = new Uri(TargetBaseAddress);
                CdnBaseAddress = CdnBaseAddress.TrimEnd('/');
                GalleryBaseAddress = GalleryBaseAddress.TrimEnd('/');

                // Load Storage
                NuGet.Services.Metadata.Catalog.Persistence.Storage storage;
                string storageDesc;
                HttpMessageHandler httpMessageHandler = null;
                if (String.IsNullOrEmpty(TargetLocalDirectory))
                {
                    ArgCheck.Require(TargetStorageAccount, "ResolverStorage");
                    ArgCheck.Require(TargetStoragePath, "ResolverPath");
                    var dir = StorageHelpers.GetBlobDirectory(TargetStorageAccount, TargetStoragePath);
                    storage = new AzureStorage(dir, resolverBaseUri);
                    storageDesc = dir.Uri.ToString();
                }
                else
                {
                    ArgCheck.Require(TargetLocalDirectory, "TargetLocalDirectory");
                    storage = new FileStorage(TargetBaseAddress, TargetLocalDirectory);
                    storageDesc = TargetLocalDirectory;
                }

                if (String.IsNullOrEmpty(CatalogIndexPath))
                {
                    ArgCheck.Require(CatalogIndexUrl, "CatalogIndexUrl");
                }
                else
                {
                    ArgCheck.Require(CatalogIndexPath, "CatalogIndexPath");
                    var localHostUri = new Uri("http://localhost:8000");
                    httpMessageHandler = new FileSystemEmulatorHandler
                    {
                        BaseAddress = localHostUri,
                        RootFolder = @"c:\data\site",
                        InnerHandler = new HttpClientHandler()
                    };

                    CatalogIndexUrl = String.Join(String.Empty, localHostUri.ToString(), CatalogIndexPath);
                }
                storage.Verbose = true;


                Uri cursorUri = new Uri(resolverBaseUri, "meta/cursor.json");

                JobEventSourceLog.LoadingCursor(cursorUri.ToString());
                StorageContent content = await storage.Load(cursorUri);
                CollectorCursor lastCursor;

                if (content == null)
                {
                    lastCursor = CollectorCursor.None;
                }
                else
                {
                    JToken cursorDoc = JsonLD.Util.JSONUtils.FromInputStream(content.GetContentStream());
                    lastCursor = (CollectorCursor)(cursorDoc["http://schema.nuget.org/collectors/resolver#cursor"].Value<DateTime>("@value"));
                }
                JobEventSourceLog.LoadedCursor(lastCursor.Value);

                ResolverCollector collector = new ResolverCollector(storage, 200)
                {
                    ContentBaseAddress = CdnBaseAddress,
                    GalleryBaseAddress = GalleryBaseAddress
                };

                collector.ProcessedCommit += cursor =>
                {
                    if (DontStoreCursor)
                    {
                        Trace.TraceWarning("Not storing cursor as requested");
                    }
                    else if (!Equals(cursor, lastCursor))
                    {
                        StoreCursor(storage, cursorUri, cursor).Wait();
                        lastCursor = cursor;
                    }
                };

                JobEventSourceLog.EmittingResolverBlobs(
                    CatalogIndexUrl.ToString(),
                    storageDesc,
                    CdnBaseAddress,
                    GalleryBaseAddress);
                lastCursor = (DateTime)await collector.Run(
                    new Uri(CatalogIndexUrl),
                    lastCursor,
                    httpMessageHandler);
                JobEventSourceLog.EmittedResolverBlobs();

            }
            catch (StorageException ex)
            {
                Trace.TraceError(ex.ToString());
                return false;
            }
            catch (AggregateException ex)
            {
                if (ShouldThrow(ex))
                {
                    throw;
                }
                return false;
            }
            catch (Exception ex)
            {
                if (ShouldThrow(ex.InnerException))
                {
                    throw;
                }
                return false;
            }

            return true;
        }

        private bool ShouldThrow(Exception ex)
        {
            if (ex != null && ex is AggregateException)
            {
                var aggregateEx = ex as AggregateException;
                if (aggregateEx.InnerExceptions.Count == 1 && aggregateEx.InnerExceptions[0] is HttpRequestException)
                {
                    Trace.TraceError(ex.ToString());
                    return false;
                }
            }

            return true;
        }

        private async Task StoreCursor(NuGet.Services.Metadata.Catalog.Persistence.Storage storage, Uri cursorUri, CollectorCursor value)
        {
            if (!Equals(value, CollectorCursor.None))
            {
                JobEventSourceLog.StoringCursor(value.Value);
                var cursorContent = new JObject { 
                { "http://schema.nuget.org/collectors/resolver#cursor", new JObject { 
                    { "@value", value.Value }, 
                    { "@type", "http://www.w3.org/2001/XMLSchema#dateTime" } } }, 
                { "http://schema.nuget.org/collectors/resolver#source", CatalogIndexUrl } }.ToString();
                await storage.Save(cursorUri, new StringStorageContent(
                    cursorContent,
                    contentType: "application/json",
                    cacheControl: "no-store"));
                JobEventSourceLog.StoredCursor();
            }
        }
    }

    public class JobEventSource : EventSource
    {
        public static readonly JobEventSource Log = new JobEventSource();

        private JobEventSource() { }

        [Event(
            eventId: 1,
            Level = EventLevel.Informational,
            Message = "Emitted metadata blob '{0}'")]
        public void EmitBlob(string blobname) { WriteEvent(1, blobname); }

        [Event(
            eventId: 2,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Start,
            Task = Tasks.EmitResolverBlobs,
            Message = "Emitting Resolver Blobs to '{1}' using catalog at '{0}', cdn at '{2}', gallery at '{3}'")]
        public void EmittingResolverBlobs(string catalog, string destination, string cdnBase, string galleryBase) { WriteEvent(2, catalog, destination, cdnBase, galleryBase); }

        [Event(
            eventId: 3,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Stop,
            Task = Tasks.EmitResolverBlobs,
            Message = "Emitted Resolver Blobs.")]
        public void EmittedResolverBlobs() { WriteEvent(3); }

        [Event(
            eventId: 4,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Stop,
            Task = Tasks.LoadingCursor,
            Message = "Loaded cursor: {0}")]
        public void LoadedCursor(string cursorValue) { WriteEvent(4, cursorValue); }

        [Event(
            eventId: 5,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Start,
            Task = Tasks.StoringCursor,
            Message = "Storing next cursor: {0}")]
        public void StoringCursor(string cursorValue) { WriteEvent(5, cursorValue); }

        [Event(
            eventId: 6,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Start,
            Task = Tasks.LoadingCursor,
            Message = "Loading cursor from {0}")]
        public void LoadingCursor(string cursorUri) { WriteEvent(6, cursorUri); }

        [Event(
            eventId: 7,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Stop,
            Task = Tasks.StoringCursor,
            Message = "Stored cursor.")]
        public void StoredCursor() { WriteEvent(7); }

        [Event(
            eventId: 8,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Start,
            Task = Tasks.HttpRequest,
            Message = "{0} {1}")]
        public void SendingHttpRequest(string method, string uri) { WriteEvent(8, method, uri); }

        [Event(
            eventId: 9,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Stop,
            Task = Tasks.HttpRequest,
            Message = "{0} {1}")]
        public void ReceivedHttpResponse(int statusCode, string uri) { WriteEvent(9, statusCode, uri); }

        [Event(
            eventId: 10,
            Level = EventLevel.Informational,
            Opcode = EventOpcode.Stop,
            Task = Tasks.HttpRequest,
            Message = "{0} {1}")]
        public void HttpException(string uri, string exception) { WriteEvent(10, uri, exception); }

        public static class Tasks
        {
            public const EventTask EmitResolverBlobs = (EventTask)0x1;
            public const EventTask LoadingCursor = (EventTask)0x2;
            public const EventTask StoringCursor = (EventTask)0x3;
            public const EventTask HttpRequest = (EventTask)0x4;
        }
    }

}
