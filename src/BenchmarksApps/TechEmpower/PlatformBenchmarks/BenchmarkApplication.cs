// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.Extensions.ObjectPool;
using RazorSlices;

namespace PlatformBenchmarks
{
    public class Burner {

         public static void ReallyBurnCycles(int effort) {
             for (int i = 0; i < effort; i++) {
                 double now = (double)nanoTime();
                 double eps = 1.0e-8;
                 double one_approx = Math.Sqrt(Math.Sin(now+eps) * Math.Sin(now) + Math.Cos(now) * Math.Cos(now+eps));
                 if (Math.Abs(one_approx - 1.0) > 0.5) {
                     Console.WriteLine("Trig failure");
                 }
             }
         }

         public static long nanoTime() {
              long nano = 10000L * Stopwatch.GetTimestamp();
              nano /= TimeSpan.TicksPerMillisecond;
              nano *= 100L;
              return nano;
         }
    }

    public sealed partial class BenchmarkApplication
    {
        /*  I couldn't make this work
        const string? aha_roi_dir = Environment.GetEnvironmentVariable("AHA_ROI_DIR");
        const string AHA_ROI = (aha_roi_dir != null) ? aha_roi_dir : "/home/robhenry/git-work/robhenry-perf/aha_roi/";
        */
        const string AHA_ROI = "/home/robhenry/git-work/robhenry-perf/aha_roi/";  // TODO
        [DllImport(AHA_ROI + "aha_roi_lib.so")]
        private static extern void aha_roi_start(uint user_value, uint roi_mask);

        [DllImport(AHA_ROI + "aha_roi_lib.so")]
        private static extern void aha_roi_stop(uint user_value, uint roi_mask);

        private static uint aha_roi_unique =
           (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private static uint aha_roi_mask = (1<<2);  // (1<<2) is AHA_ROI_PRINTF

        public static ReadOnlySpan<byte> ApplicationName => "Kestrel Platform-Level Application"u8;

        private static ReadOnlySpan<byte> _crlf => "\r\n"u8;
        private static ReadOnlySpan<byte> _eoh => "\r\n\r\n"u8; // End Of Headers
        private static ReadOnlySpan<byte> _http11OK => "HTTP/1.1 200 OK\r\n"u8;
        private static ReadOnlySpan<byte> _http11NotFound => "HTTP/1.1 404 Not Found\r\n"u8;
        private static ReadOnlySpan<byte> _headerServer => "Server: K"u8;
        private static ReadOnlySpan<byte> _headerContentLength => "Content-Length: "u8;
        private static ReadOnlySpan<byte> _headerContentLengthZero => "Content-Length: 0"u8;
        private static ReadOnlySpan<byte> _headerContentTypeText => "Content-Type: text/plain"u8;
        private static ReadOnlySpan<byte> _headerContentTypeJson => "Content-Type: application/json"u8;
        private static ReadOnlySpan<byte> _headerContentTypeHtml => "Content-Type: text/html; charset=UTF-8"u8;

        private static ReadOnlySpan<byte> _dbPreamble => 
            "HTTP/1.1 200 OK\r\n"u8 +
            "Server: K\r\n"u8 +
            "Content-Type: application/json\r\n"u8 +
            "Content-Length: "u8;

        private static ReadOnlySpan<byte> _plainTextBody => "Hello, World!"u8;
        private static ReadOnlySpan<byte> _contentLengthGap => "    "u8;

        public static RawDb RawDb { get; set; }
        public static DapperDb DapperDb { get; set; }
        public static EfDb EfDb { get; set; }

        private static readonly DefaultObjectPool<ChunkedBufferWriter<WriterAdapter>> ChunkedWriterPool
            = new(new ChunkedWriterObjectPolicy());

        private sealed class ChunkedWriterObjectPolicy : IPooledObjectPolicy<ChunkedBufferWriter<WriterAdapter>>
        {
            public ChunkedBufferWriter<WriterAdapter> Create() => new();

            public bool Return(ChunkedBufferWriter<WriterAdapter> writer)
            {
                writer.Reset();
                return true;
            }
        }

        private static Thread thWaitForFullGC;
        private static void LaunchGCNotifier() {
            // Register for a notification.
            GC.RegisterForFullGCNotification(GCBehavior.maxGenerationThreshold, GCBehavior.largeObjectHeapThreshold);
            Console.WriteLine("Registered for GC notification via LaunchGCNotifier.");

             GCNotifyProgram.checkForNotify = true;
             GCNotifyProgram.bAllocate = true;

             thWaitForFullGC = new Thread(new ThreadStart(GCNotifyProgram.WaitForFullGCProc));
             thWaitForFullGC.Start();
        }

        private static uint getVariable(string name, uint value_old) {
            var as_str = Environment.GetEnvironmentVariable(name);
            if (as_str != null) {
                try {
                    uint value_new = UInt32.Parse(as_str);
                    value_old = value_new;
                    Console.WriteLine($"Got a new value: {value_new} from {name}={as_str}");
                } catch (FormatException _e) {
                    Console.WriteLine($"ERROR: garbage for {name} {as_str}: {_e}");
                }
            } else {
                Console.WriteLine($"WARNING: NO environment variable {name}");
            }
            return value_old; // possibly modified
        }

        private static uint env_count = 0;  // how many http cycles
        private static uint aha_roi_start_count =  20;
        private static uint aha_roi_stop_count = 2020;

        //
        // The handlers for all of the different teche
        // scenarios have been modified
        // to call BurnCycles once per request/response cycle.
        //
        private static void RequestCallBackForMonitoring(int effort)
        {
            Burner.ReallyBurnCycles(50*effort);  // DO NOT COMMIT long term TODO
            //
            // Interlocked.Increment returns the value after the increment.
            //
            var captured_env_count = Interlocked.Increment(ref env_count) - 1; // WATCHOUT: this is a hot spot
            if (captured_env_count == 0) {
                aha_roi_unique = getVariable(
                  "AHA_ROI_UNIQUE", aha_roi_unique);
                aha_roi_mask = getVariable(
                  "AHA_ROI_MASK", aha_roi_mask);
                aha_roi_start_count = getVariable(
                  "AHA_ROI_START_COUNT", aha_roi_start_count);
                aha_roi_stop_count = getVariable(
                  "AHA_ROI_STOP_COUNT", aha_roi_stop_count);

                EnvironmentDump.dump_Main(new string[]{"foo", "bar"});

                if (GCBehavior.do_trace_GC_notify) {
                    LaunchGCNotifier();
                } else {
                    if (GCBehavior.do_GC_stress) {
                        GCNotifyProgram.bAllocate = true;
                    }
                }
            }
            if (captured_env_count == aha_roi_start_count) {
                aha_roi_start(aha_roi_unique, aha_roi_mask);
            }

            if (captured_env_count == aha_roi_stop_count) {
                aha_roi_stop(aha_roi_unique, aha_roi_mask);
            }

            if (GCBehavior.do_GC_stress) {
                if (env_count % 13 == 0) {
                    if (GCNotifyProgram.bAllocate) {
                        //
                        // make and save garbage until the next GC cycle
                        // the GC cycle will set bAllocate to false,
                        // so that we stop mutating in parallel with the GC,
                        // which seems to result in OOM (on Windows at least)
                        //
                        // Console.WriteLine($"GARBAGE on {env_count}");
                        //
                        GCNotifyProgram.load.Add(new byte[10 * 1024]);
                        GCNotifyProgram.btrees.Add(new BTree(env_count % 4));
                    }
                }
            }

            if (GCBehavior.do_burn_cycles_on_request_response) {
                if (env_count % 50000 == 0) {
                    Burner.ReallyBurnCycles(effort);
                }
            }
        }

#if DATABASE
          private readonly static SliceFactory<List<FortuneUtf8>> FortunesTemplateFactory = RazorSlice.ResolveSliceFactory<List<FortuneUtf8>>("/Templates/FortunesUtf8.cshtml");
          private readonly static SliceFactory<List<FortuneUtf16>> FortunesDapperTemplateFactory = RazorSlice.ResolveSliceFactory<List<FortuneUtf16>>("/Templates/FortunesUtf16.cshtml");
          private readonly static SliceFactory<List<FortuneEf>> FortunesEfTemplateFactory = RazorSlice.ResolveSliceFactory<List<FortuneEf>>("/Templates/FortunesEf.cshtml");
#endif

          [ThreadStatic]
          private static Utf8JsonWriter t_writer;

          private static readonly JsonContext SerializerContext = JsonContext.Default;

          [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]
          [JsonSerializable(typeof(JsonMessage))]
          [JsonSerializable(typeof(CachedWorld[]))]
          [JsonSerializable(typeof(World[]))]
          private partial class JsonContext : JsonSerializerContext
          {
          }

          public static class Paths
          {
              public static ReadOnlySpan<byte> Json => "/json"u8;
              public static ReadOnlySpan<byte> Plaintext => "/plaintext"u8;
              public static ReadOnlySpan<byte> SingleQuery => "/db"u8;
              public static ReadOnlySpan<byte> FortunesRaw => "/fortunes"u8;
              public static ReadOnlySpan<byte> FortunesDapper => "/fortunes/dapper"u8;
              public static ReadOnlySpan<byte> FortunesEf => "/fortunes/ef"u8;
              public static ReadOnlySpan<byte> Updates => "/updates/"u8;
              public static ReadOnlySpan<byte> MultipleQueries => "/queries/"u8;
              public static ReadOnlySpan<byte> Caching => "/cached-worlds/"u8;
          }

          private RequestType _requestType;
          private int _queries;

          public void OnStartLine(HttpVersionAndMethod versionAndMethod, TargetOffsetPathLength targetPath, Span<byte> startLine)
          {
              _requestType = versionAndMethod.Method == HttpMethod.Get ? GetRequestType(startLine.Slice(targetPath.Offset, targetPath.Length), ref _queries) : RequestType.NotRecognized;
          }

          private static RequestType GetRequestType(ReadOnlySpan<byte> path, ref int queries)
          {
#if !DATABASE
              if (path.Length == 10 && path.SequenceEqual(Paths.Plaintext))
              {
                  return RequestType.PlainText;
              }
              else if (path.Length == 5 && path.SequenceEqual(Paths.Json))
              {
                  return RequestType.Json;
              }
#else
              if (path.Length == 3 && path[0] == '/' && path[1] == 'd' && path[2] == 'b')
              {
                  return RequestType.SingleQuery;
              }
              if (path[1] == 'f')
              {
                  return path.Length switch
                  {
                      9 when path.SequenceEqual(Paths.FortunesRaw) => RequestType.FortunesRaw,
                      16 when path.SequenceEqual(Paths.FortunesDapper) => RequestType.FortunesDapper,
                      12 when path.SequenceEqual(Paths.FortunesEf) => RequestType.FortunesEf,
                      _ => RequestType.NotRecognized
                  };
              }
              if (path.Length >= 15 && path[1] == 'c' && path.StartsWith(Paths.Caching))
              {
                  queries = ParseQueries(path.Slice(15));
                  return RequestType.Caching;
              }
              if (path.Length >= 9 && path[1] == 'u' && path.StartsWith(Paths.Updates))
              {
                  queries = ParseQueries(path.Slice(9));
                  return RequestType.Updates;
              }
              if (path.Length >= 9 && path[1] == 'q' && path.StartsWith(Paths.MultipleQueries))
              {
                  queries = ParseQueries(path.Slice(9));
                  return RequestType.MultipleQueries;
              }
#endif
              return RequestType.NotRecognized;
          }


#if !DATABASE
          private void ProcessRequest(ref BufferWriter<WriterAdapter> writer)
          {
              if (_requestType == RequestType.PlainText)
              {
                  PlainText(ref writer);
              }
              else if (_requestType == RequestType.Json)
              {
                  Json(ref writer, Writer);
              }
              else
              {
                  Default(ref writer);
              }
          }
#else

          private static int ParseQueries(ReadOnlySpan<byte> parameter)
          {
              if (!Utf8Parser.TryParse(parameter, out int queries, out _))
              {
                  queries = 1;
              }
              else
              {
                  queries = Math.Clamp(queries, 1, 500);
              }

              return queries;
          }

          private Task ProcessRequestAsync() => _requestType switch
          {
              RequestType.FortunesRaw => FortunesRaw(Writer),
              RequestType.FortunesDapper => FortunesDapper(Writer),
              RequestType.FortunesEf => FortunesEf(Writer),
              RequestType.SingleQuery => SingleQuery(Writer),
              RequestType.Caching => Caching(Writer, _queries),
              RequestType.Updates => Updates(Writer, _queries),
              RequestType.MultipleQueries => MultipleQueries(Writer, _queries),
              _ => Default(Writer)
          };

          private static Task Default(PipeWriter pipeWriter)
          {
              var writer = GetWriter(pipeWriter, sizeHint: _defaultPreamble.Length + DateHeader.HeaderBytes.Length);
              Default(ref writer);
              writer.Commit();
              return Task.CompletedTask;
          }
#endif
          private static ReadOnlySpan<byte> _defaultPreamble =>
              "HTTP/1.1 200 OK\r\n"u8 +
              "Server: K"u8 + "\r\n"u8 +
              "Content-Type: text/plain"u8 +
              "Content-Length: 0"u8;

          private static void Default(ref BufferWriter<WriterAdapter> writer)
          {
              writer.Write(_defaultPreamble);

              // Date header
              writer.Write(DateHeader.HeaderBytes);
          }

          private enum RequestType
          {
              NotRecognized,
              PlainText,
              Json,
              FortunesRaw,
              FortunesDapper,
              FortunesEf,
              SingleQuery,
              Caching,
              Updates,
              MultipleQueries
          }
    }

    public class GCNotifyProgram
    {
        //
        // Variable for continually checking in the
        // While loop in the WaitForFullGCProc method.
        //
        public static bool checkForNotify = false;

        //
        // Variable for suspending work
        // (such servicing allocated server requests)
        // after a notification is received and then
        // resuming allocation after inducing a garbage collection.
        //
        public static bool bAllocate = false;

        //
        // Variable for ending the example.
        //
        public static bool finalExit = false;

        //
        // Collection for objects that
        // simulate the server request workload.
        //
        public static List<byte[]> load = new List<byte[]>();
        public static List<BTree> btrees = new List<BTree>();

        public static void OnFullGCApproachNotify()
        {
            Console.WriteLine("OnFullGCApproachNotify()");
            if (GCBehavior.do_burn_cycles_on_gc_callback) {
                Burner.ReallyBurnCycles(200);
            }
            if (true) {
                Console.WriteLine("Redirecting requests.");
                //
                // Method that tells the request queuing
                // server to not direct requests to this server.
                //
                RedirectRequests();

                //
                // Method that provides time to
                // finish processing pending requests.
                //
                FinishExistingRequests();

                //
                // This is a good time to induce a GC collection
                // because the runtime will induce a full GC soon.
                // To be very careful, you can check precede with a
                // check of the GC.GCCollectionCount to make sure
                // a full GC did not already occur since last notified.
                //
            }

            if (true) {
                Console.WriteLine("Started  calling GC.Collect()");
                GC.Collect(); // forces an intermediate collect of all generations
                Console.WriteLine("Finished calling GC.Collect()");
            }
        }

        public static void OnFullGCCompleteEndNotify()
        {
          Console.WriteLine("OnFullGCCompleteEndNotify()");
          if (GCBehavior.do_burn_cycles_on_gc_callback) {
            Burner.ReallyBurnCycles(200);
          }
          if (true) {
            //
            // Call a method that informs the request queuing server
            // that this server is ready to accept requests again.
            //
            AcceptRequests();
            Console.WriteLine("Accepting requests again.");
          }
        }

        public static void WaitForFullGCProc()
        {
            while (true)
            {
                // CheckForNotify is set to true and false in Main.
                while (checkForNotify)
                {
                    Console.WriteLine("WaitForFullGCProc() checkForNotify step 1");
                    // Check for a notification of an approaching collection.
                    GCNotificationStatus s = GC.WaitForFullGCApproach();
                    if (s == GCNotificationStatus.Succeeded)
                    {
                        Console.WriteLine("GC Notification raised WaitForFullApproach.");
                        OnFullGCApproachNotify();
                    }
                    else if (s == GCNotificationStatus.Canceled)
                    {
                        Console.WriteLine("GC Notification cancelled.");
                        break;
                    }
                    else
                    {
                        // This can occur if a timeout period
                        // is specified for WaitForFullGCApproach(Timeout)
                        // or WaitForFullGCComplete(Timeout)
                        // and the time out period has elapsed.
                        Console.WriteLine("GC Notification not applicable.");
                        break;
                    }

                    // Check for a notification of a completed collection.
                    Console.WriteLine("WaitForFullGCProc() checkForNotify step 2");
                    GCNotificationStatus status = GC.WaitForFullGCComplete();
                    if (status == GCNotificationStatus.Succeeded)
                    {
                        Console.WriteLine("GC Notification raised WaitForFullGCComplete.");
                        OnFullGCCompleteEndNotify();
                    }
                    else if (status == GCNotificationStatus.Canceled)
                    {
                        Console.WriteLine("GC Notification cancelled.");
                        break;
                    }
                    else
                    {
                        // Could be a time out.
                        Console.WriteLine("GC Notification not applicable.");
                        break;
                    }
                    Console.WriteLine("WaitForFullGCProc() checkForNotify step 3");
                }

                Thread.Sleep(500);
                // FinalExit is set to true right before
                // the main thread cancelled notification.
                if (finalExit)
                {
                    break;
                }
            }
        }

        private static void RedirectRequests()
        {
            // Code that sends requests
            // to other servers.

            // Suspend work.
            bAllocate = false;
        }

        private static void FinishExistingRequests()
        {
            // Code that waits a period of time
            // for pending requests to finish.

            // Clear the simulated workload.
            load.Clear();
            btrees.Clear();
        }

        private static void AcceptRequests()
        {
            // Code that resumes processing
            // requests on this server.

            // Resume work.
            bAllocate = true;
        }
    }

    public class BTree
    {
        public BTree left;
        public BTree right;
        public BTree(ulong depth) {
             if (depth > 0) {
                left = new BTree(depth - 1);
                right = new BTree(depth - 1);
             } else {
               left = null;
               right = null;
             }
        }
        ~BTree() {
            //
            // empirically, this work done in the logical processors for app,
            // *not* for the GC.
            //
            if (GCBehavior.do_burn_cycles_on_finalizer) {
                Burner.ReallyBurnCycles(200);
            }
        }
    }


    public static class GCBehavior
    {
        //
        // knobs to control behavior of the Garbage Collector,
        // also used as a convenient parking place for other
        // behavior just for this application,
        // such as heartbeating by executing weird instructions
        // observable to an independent PMU collection job.
        //
        public static int maxGenerationThreshold = 10;
        public static int largeObjectHeapThreshold = 10;

        //
        // Normally, the techempower json scenario generates very little garbage.
        // If you do artificially make garbage (here with do_GC_stress)
        // then you MUST to turn off the generation of garbage
        // when the GC is running, otherwise your app will Out of Memory (OOM).
        //
        public static bool do_GC_stress = false;
        public static bool do_trace_GC_notify = false;  // *must* be true if do_GC_stress is true

        //
        // do_burn_cycles_on_request_response controls burning
        // double precision cycles on each teche request/reponse cycle.
        // Double precision is unlikely to be in this workload,
        // and for most ISAs has its own set of PMU counters,
        // making this an easy to spot heartbeat.
        //
        public static bool do_burn_cycles_on_request_response = false;

        //
        // do_burn_cycles_on_finalizer controls
        // burning double precision cycles on BTree finalizer.
        // The finalzer is not run on a GC thread.
        //
        public static bool do_burn_cycles_on_finalizer = false;

        //
        // do_burn_cycles_on_gc_callback controls if double-precision work is done
        // for the side effects on the independently observed PMU counters.
        //
        public static bool do_burn_cycles_on_gc_callback = false;
    }

    public class EnvironmentDump
    {
        public static void dump_Main(string[] args)
        {
            // Console.WriteLine($"Started envprint");
            var stopwatch = Stopwatch.StartNew();
            DumpInfo(stopwatch);
        }

        public static bool IsNumber(object value)
        {
            return value is sbyte
                    || value is byte
                    || value is short
                    || value is ushort
                    || value is int
                    || value is uint
                    || value is long
                    || value is ulong
                    || value is float
                    || value is double
                    || value is decimal;
        }

        public static string GetDotnetExeDirectory()
        {
            Console.WriteLine($"Environment.ProcessPath={Environment.ProcessPath}");
            var dotnetRootPath = Path.GetDirectoryName(Environment.ProcessPath);

            Console.WriteLine($"dotnetRootPath={dotnetRootPath}");
            Console.WriteLine($"Path.GetFileName(dotnetRootpath)={Path.GetFileName(dotnetRootPath)}");
            Console.WriteLine($"contains dotnet {Path.GetFileName(dotnetRootPath).Contains("dotnet")}");
            Console.WriteLine($"contains    x64 {Path.GetFileName(dotnetRootPath).Contains("x64")}");
            Console.WriteLine($"contains  arm64 {Path.GetFileName(dotnetRootPath).Contains("arm64")}");
            Console.WriteLine($"equals d {Path.GetFileName(dotnetRootPath).Equals("d")}");
            dotnetRootPath = Path.GetFileName(dotnetRootPath).Contains("dotnet") || Path.GetFileName(dotnetRootPath).Contains("x64") || Path.GetFileName(dotnetRootPath).Equals("d") ? dotnetRootPath : Path.Combine(dotnetRootPath, "dotnet");
            Console.WriteLine($"returns {dotnetRootPath}");
            return dotnetRootPath;
        }

        public static void DumpInfo(Stopwatch stopwatch) {
            GetDotnetExeDirectory();
            var name = Environment.MachineName;
            var runtime_dir = RuntimeEnvironment.GetRuntimeDirectory();

            var nproc = Environment.ProcessorCount;

            var time_ms = stopwatch.ElapsedMilliseconds;
            var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK");
            var osversion = Environment.OSVersion.Version.ToString();
            var clrversion = Environment.Version;
            var pid = Environment.ProcessId;
            var pagesize = Environment.SystemPageSize;
            var tickcount = Environment.TickCount;
            var domainname = Environment.UserDomainName.ToString();
            var username = Environment.UserName.ToString();
            var version = Environment.Version.ToString();
            var workingset = Environment.WorkingSet;

            int maxWorkerThreads;
            int maxPortThreads;
            ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxPortThreads);

            int minWorkerThreads;
            int minPortThreads;
            ThreadPool.GetMinThreads(out minWorkerThreads, out minPortThreads);

            int availWorkerThreads;
            int availPortThreads;
            ThreadPool.GetAvailableThreads(out availWorkerThreads, out availPortThreads);

            int threadCount;
            threadCount = System.Threading.ThreadPool.ThreadCount;

            Dictionary<string, object> dict_vars = new Dictionary<string, object>(){
                {"clrversion", clrversion},
                {"domainname", domainname},
                {"name", name},
                {"now", now},
                {"osversion", osversion},
                {"runtime", runtime_dir},
                {"tickcount", tickcount},
                {"time_ms", time_ms},
                {"username", username},
                {"version", version},

                {"nproc", nproc},
                {"pagesize", pagesize},
                {"pid", pid},
                {"workingset", workingset},

                {"maxWorkerThreads", maxWorkerThreads},
                {"maxPortThreads", maxPortThreads},

                {"minWorkerThreads", minWorkerThreads},
                {"minPortThreads", minPortThreads},

                {"availWorkerThreads", availWorkerThreads},
                {"availPortThreads", availPortThreads},

                {"threadCount", threadCount},

            };

            //
            // This list is from ./src/coreclr/gc/gcconfig.h as of 20Jan2022
            //
            // The exposed GC knobs can be seen here:
            //    https://github.com/steveharter/dotnet_coreclr/blob/master/Documentation/project-docs/clr-configuration-knobs.md#garbage-collector-configuration-knobs
            //
            // ARGH! Argh! The variations in case conventions are totally nuts.
            //
            // Note there does not seem to be a way to find out what the default is.
            //
            // field numbers here are in callout order from
            //   ./src/coreclr/gc/gcconfig.h
            // as of 26Jan2022
            //
            // Fished for more variables whose name (case insensitive) contains numa or cpugroups
            // Found more in
            //    ./src/coreclr/inc/clrconfigvalues.h
            //
            string [] gc_vars = {

                "Thread_UseAllCpuGroups",  // Specifies whether to query and use CPU group information for determining the processor count.
                "Thread_AssignCpuGroups",  // Specifies whether to automatically distribute threads created by the CLR across CPU Groups. Effective only when Thread_UseAllCpuGroups and GCCpuGroup are enabled
                "BGCFLEnableFF",
                "BGCFLEnableKd",
                "BGCFLEnableKi",
                "BGCFLEnableSmooth",
                "BGCFLEnableTBH",
                "BGCFLGradualD",
                "BGCFLSmoothFactor",
                "BGCFLSweepGoal",
                "BGCFLSweepGoalLOH",
                "BGCFLTuningEnabled",
                "BGCFLff",
                "BGCFLkd",
                "BGCFLki",
                "BGCFLkp",
                "BGCG2RatioStep",
                "BGCMLki",
                "BGCMLkp",
                "BGCMemGoal",
                "BGCMemGoalSlack",
                "BGCSpin",
                "BGCSpinCount",
                "GCBreakOnOOM",
                "GCCompactRatio",
                "GCConfigLogEnabled",
                "GCConfigLogFile",
                "GCConserveMemory",
                "GCCpuGroup",
                "GCEnabledInstructionSets",
                "GCGen0MaxBudget",
                "GCGen1MaxBudget",
                "GCHeapAffinitizeMask",
                "GCHeapAffinitizeRanges",
                "GCHeapCount",
                "GCHeapHardLimit",
                "GCHeapHardLimitLOH",
                "GCHeapHardLimitLOHPercent",
                "GCHeapHardLimitPOH",
                "GCHeapHardLimitPOHPercent",
                "GCHeapHardLimitPercent",
                "GCHeapHardLimitSOH",
                "GCHeapHardLimitSOHPercent",
                "GCHighMemPercent",
                "GCLOHCompact",
                "GCLOHThreshold",
                "GCLargePages",
                "GCLatencyLevel",
                "GCLatencyMode",
                "GCLogEnabled",
                "GCLogFile",
                "GCLogFileSize",
                "GCLowSkipRatio",
                "GCNoAffinitize",
                "GCNumaAware",
                "GCProvModeStress",
                "GCRegionsRange",
                "GCRegionsSize",
                "GCRetainVM",
                "GCSegmentSize",
                "GCTotalPhysicalMemory",
                "GCgen0size",
                "HeapVerify",
                "gcConcurrent",
                "gcConservative",
                "gcForceCompact",
                "gcServer",
            };
            foreach (string var_name in gc_vars) {
                string full_name = "DOTNET_" + var_name;
                Console.WriteLine($"# XXXXXX Probe {full_name}");

                var gcenv0 = Environment.GetEnvironmentVariable(full_name);
                if (gcenv0 != null) {
                     dict_vars["DOTNET_" + var_name] = gcenv0;
                     Console.WriteLine($"# DOTNET_{var_name}={gcenv0}");
                }
            }

            string sep = "";
            Console.Write("{");
            foreach (var kvp in dict_vars) {
                try {
                    //
                    // TODO(robhenry): json does not allow comments
                    // just print the value as a string,
                    // and disentangle it in the reader of this json
                    //
                    if (IsNumber(kvp.Value)) {
                        Console.WriteLine($"{sep}\"{kvp.Key}\":{kvp.Value}");
                    } else {
                        Console.WriteLine($"{sep}\"{kvp.Key}\":\"{kvp.Value}\"");
                    }
                    sep = ",";
                } catch (System.TypeLoadException e) {
                    Console.WriteLine($"Caught...");
                    Console.WriteLine($"Caught... {e.GetType()}");
                    Console.WriteLine($"Caught... {e.Message}");
                    Console.WriteLine($"{e}");
                }
            }
            Console.Write("}");
            Console.WriteLine("");
        }
    }
}
