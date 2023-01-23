using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AnalysisServices;
using Newtonsoft.Json;
using System.IO;
using CommandLine;
using Newtonsoft.Json.Linq;
using System.Runtime.InteropServices;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Microsoft.Extensions.Configuration;
using System.Text;

namespace PBITracer
{
    class Program
    {
        public class Options
        {
            [Option('s', "server", Required = true, HelpText = "Power BI Premium XMLA Workspace Connection")]
            public string Server { get; set; }

            [Option('d', "database", Required = true, HelpText = "Power BI Dataset name")]
            public string Database { get; set; }

            [Option('o', "output", Required = false, Default = ".\\output", HelpText = "Output folder")]
            public string outputPath { get; set; }

            [Option('u', "user", Required = false, HelpText = "Username / Service Principal Id (app:<id>@<tenantid>)")]
            public string username { get; set; }

            [Option('p', "password", Required = false, HelpText = "Password / Service Principal Secret")]
            public string password { get; set; }

            [Option('l', "logging", Required = false, Default = true, HelpText = "Logging to rolling files")]
            public bool Logging { get; set; }

            [Option('e', "events", Required = false, Default = new string[] { "JobGraph", "ProgressReportEnd", "QueryEnd" }, HelpText = "Events to trace, ex: QueryEnd, ProgressReportEnd")]
            public IList<string> events { get; set; }

            [Option('t', "timeout", Required = false, Default = 3600, HelpText = "Trace timeout")]
            public int Timeout { get; set; }
        }

        private static Dictionary<TraceEventClass, List<TraceColumn>> listEventClassColumnCombination = new Dictionary<TraceEventClass, List<TraceColumn>>();
        private static Microsoft.AnalysisServices.Tabular.Trace trace;
        private static StreamWriter jsonFile;
        private static JsonTextWriter jsonWriter;
        private static Newtonsoft.Json.JsonSerializer serializer;
        private static string traceId;
        private static ILogger logger;
        private static bool receivedTrace = false;
        private static bool disposedResources = false;
        private static object disposedResourcesLocker = new object();
        private static string outputFilePath;
        private static Microsoft.AnalysisServices.Tabular.Server conn;

        private static readonly CancellationTokenSource canToken = new CancellationTokenSource();
        private static readonly string tracerIdPrefix = "PBI_Tracer";

        static async Task Main(string[] args)
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

                Console.CancelKeyPress += new ConsoleCancelEventHandler(OnCancel);

                var appPath = Directory.GetCurrentDirectory();

                var appSettingsFile = Path.Combine(appPath, "appsettings.json");

                var configuration = new ConfigurationBuilder()
                 .SetBasePath(appPath)
                 .AddJsonFile(appSettingsFile, optional: true, reloadOnChange: true)
                 .Build();

                var loggerConfig = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console();

                var cmdLineParser = Parser.Default.ParseArguments<Options>(args);

                if (cmdLineParser.Tag == ParserResultType.Parsed)
                {
                    cmdLineParser.WithParsed(o =>
                    {
                        if (o.Logging)
                        {
                            loggerConfig = loggerConfig.WriteTo.File("logs\\log_.txt"
                                , rollingInterval: RollingInterval.Day
                                );
                        }

                        logger = loggerConfig.CreateLogger();
                    });

                    await Parser.Default.ParseArguments<Options>(args)
                        .WithParsedAsync(Worker);
                }
                else
                {                                      
                    logger = loggerConfig.CreateLogger();

                }

            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.Error(ex, ex.Message);
                }
                else
                {
                    Console.WriteLine(ex.ToString());
                }

                throw;
            }
        }

        async static Task Worker(Options o)
        {
            traceId = $"{tracerIdPrefix}_{o.Database}";

            logger.Information("PBI Tracer on Server '{0}' | TraceId: '{1}'", o.Server, traceId);

            serializer = new Newtonsoft.Json.JsonSerializer();

            serializer.Error += delegate (object s2, Newtonsoft.Json.Serialization.ErrorEventArgs args2)
            {
                args2.ErrorContext.Handled = true;
            };

            var outputPath = o.outputPath;

            var fileName = $"{o.Database}.{Guid.NewGuid().ToString("N")}.json";

            fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));

            outputFilePath = Path.Combine(outputPath, fileName);

            logger.Information("Preparing output file: '{0}'", outputFilePath);

            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            jsonFile = File.CreateText(outputFilePath);

            jsonWriter = new JsonTextWriter(jsonFile);

            jsonWriter.AutoCompleteOnClose = true;

            jsonWriter.WriteStartArray();

            try
            {
                var connStrBuilder = new DbConnectionStringBuilder();

                connStrBuilder["DataSource"] = o.Server;

                if (!string.IsNullOrEmpty(o.Database))
                {
                    connStrBuilder["Initial Catalog"] = o.Database;
                }

                if (!string.IsNullOrEmpty(o.username))
                {
                    connStrBuilder["User Id"] = o.username;
                    connStrBuilder["Password"] = o.password;
                }

                var connStr = connStrBuilder.ConnectionString;

                conn = new Microsoft.AnalysisServices.Tabular.Server();

                logger.Information("Connecting to server {0}", o.Server);

                conn.Connect(connStr);

                var previousTraces = conn.Traces.Cast<Microsoft.AnalysisServices.Tabular.Trace>().Where(t => t.Name == traceId).ToList();

                if (previousTraces.Count != 0)
                {
                    logger.Information("Cleaning previous traces");

                    foreach (var trace in previousTraces)
                    {
                        if (trace.IsStarted)
                        {
                            trace.Stop();
                        }
                        trace.Drop();
                    }
                }

                logger.Information("Preparing the trace configuration");

                trace = conn.Traces.Add(traceId);
                
                trace.StopTime = DateTime.UtcNow.AddSeconds(o.Timeout);

                logger.Information("Trace Stop Time at: '{0:yyyy-MM-dd HH:mm:ss}'", trace.StopTime);

                trace.Audit = true;

                trace.Events.Clear();

                foreach (var eventTypeStr in o.events)
                {
                    var eventType = (Microsoft.AnalysisServices.TraceEventClass)Enum.Parse(typeof(Microsoft.AnalysisServices.TraceEventClass), eventTypeStr);
                    AddTraceEvent(trace, eventType);
                }

                trace.Update(UpdateOptions.Default, UpdateMode.CreateOrReplace);

                trace.OnEvent += Trace_OnEvent;

                logger.Information("Starting the trace");

                trace.Start();

                logger.Information("Waiting for trace data, CTRL + C to close");

                while (!canToken.IsCancellationRequested)
                {                   
                    await Task.Delay(3000);
                }
            }
            finally
            {
                DisposeResources();
            }

        }

        protected static void OnCancel(object sender, ConsoleCancelEventArgs args)
        {
            canToken.Cancel();
            args.Cancel = true;
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            DisposeResources();
        }

        private static void Trace_OnEvent(object sender, Microsoft.AnalysisServices.Tabular.TraceEventArgs e)
        {
            try
            {
                var activityId = e[TraceColumn.ActivityID];
                                
                switch (e.EventClass)
                {
                    case TraceEventClass.QueryEnd:
                    case TraceEventClass.QueryBegin:
                        {
                            string truncatedText = "";

                            if (!string.IsNullOrEmpty(e.TextData))
                            {
                                truncatedText = e.TextData.Substring(0, Math.Min(e.TextData.Length, 500));                                
                            }
                            
                            logger.Debug("TraceEvent: {0} | {1} | {2} | {3} | {4}", e.EventClass.ToString(), e.EventSubclass.ToString(), activityId, e.NTUserName, truncatedText);
                        }
                        break;
                    default:
                        {
                            logger.Debug("TraceEvent: {0} | {1} | {2}", e.EventClass.ToString(), e.EventSubclass.ToString(), activityId);
                        }
                        break;
                }

               
                var eventClassColumns = listEventClassColumnCombination[e.EventClass];

                var jsonObj = new JObject();

                jsonObj.Add("EventClassName", e.EventClass.ToString());

                jsonObj.Add("EventSubclassName", e.EventSubclass.ToString());
                
                foreach (var traceColumn in eventClassColumns)
                {                  
                    jsonObj.Add(traceColumn.ToString(), e[traceColumn]);
                }

                serializer.Serialize(jsonWriter, jsonObj);
                
                receivedTrace = true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error on 'Trace_OnEvent'");
            }
        }

        private static void AddTraceEvent(Microsoft.AnalysisServices.Tabular.Trace trace, TraceEventClass eventClass)
        {
            // https://docs.microsoft.com/en-us/analysis-services/trace-events/query-processing-events-data-columns?view=asallproducts-allversions

            logger.Information("Tracing event: {0}", eventClass.ToString());

            var traceEvent = new Microsoft.AnalysisServices.Tabular.TraceEvent(eventClass);

            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.EventClass);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.TextData);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.CurrentTime);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.Spid);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.SessionID);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ActivityID);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.RequestID);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.DatabaseName);


            switch (eventClass)
            {
                case TraceEventClass.ProgressReportCurrent:
                case TraceEventClass.ProgressReportEnd:
                    {
                        AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.IntegerData);
                    }
                    break;
            }

            if (eventClass == TraceEventClass.QueryEnd)
            {
                AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.NTUserName);
            }

            if (eventClass != TraceEventClass.DirectQueryEnd && eventClass != TraceEventClass.Error)
            {
                AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.EventSubclass);
            }

            if (eventClass == TraceEventClass.QueryEnd
                || eventClass == TraceEventClass.CommandEnd
                || eventClass == TraceEventClass.DAXQueryPlan)
            {
                AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ApplicationName);
            }

            switch (eventClass)
            {
                case TraceEventClass.CommandEnd:
                case TraceEventClass.CalculateNonEmptyEnd:
                case TraceEventClass.DirectQueryEnd:
                case TraceEventClass.DiscoverEnd:
                case TraceEventClass.ExecuteMdxScriptEnd:
                case TraceEventClass.FileSaveEnd:
                case TraceEventClass.ProgressReportEnd:
                case TraceEventClass.QueryCubeEnd:
                case TraceEventClass.QueryEnd:
                case TraceEventClass.QuerySubcube:
                case TraceEventClass.QuerySubcubeVerbose:
                case TraceEventClass.VertiPaqSEQueryEnd:
                    {
                        AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.Duration);
                        AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.CpuTime);
                        AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.StartTime);
                        AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.EndTime);

                        if (eventClass != TraceEventClass.QueryEnd)
                        {
                            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ObjectType);
                            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ObjectName);
                            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ObjectID);
                            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ObjectPath);
                        }
                    }
                    break;
                case TraceEventClass.Error:
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.Error);
                    break;

            }

            trace.Events.Add(traceEvent);
        }

        private static void AddColumnToTraceEvent(Microsoft.AnalysisServices.Tabular.TraceEvent traceEvent, TraceEventClass eventClass, TraceColumn traceColumn)
        {
            traceEvent.Columns.Add(traceColumn);

            if (!listEventClassColumnCombination.ContainsKey(eventClass))
            {
                listEventClassColumnCombination[eventClass] = new List<TraceColumn>();
            }

            var columnsRegistered = listEventClassColumnCombination[eventClass];

            if (!columnsRegistered.Any(s => s == traceColumn))
            {
                columnsRegistered.Add(traceColumn);
            }
        }

        private static void DisposeResources()
        {
            lock (disposedResourcesLocker)
            {
                if (!canToken.IsCancellationRequested)
                {
                    canToken.Cancel();
                }

                if (!disposedResources)
                {
                    logger.Information("Disposing resources...");

                    try
                    {
                        if (trace != null)
                        {
                            if (trace.IsStarted)
                            {
                                trace.Stop();
                            }

                            trace.Drop();
                            trace.Dispose();
                        }

                        if (conn != null)
                        {
                            conn.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Error closing trace");
                    }

                    try
                    {
                        if (jsonWriter != null)
                        {                            
                            jsonWriter.Close();
                        }

                        if (jsonFile != null)
                        {
                            jsonFile.Close();
                            jsonFile.Dispose();
                        }

                        if (!receivedTrace && File.Exists(outputFilePath))
                        {
                            File.Delete(outputFilePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Error closing file");
                    }

                    disposedResources = true;

                    logger.Information("Disposed all resources successfully.");

                    Log.CloseAndFlush();
                }                            
            }
        }
    }
}
