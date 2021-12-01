using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AnalysisServices;
using Newtonsoft.Json;
using System.IO;
using CommandLine;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace PBITracer
{
    class Program
    {
        public class Options
        {
            [Option('s', "server", Required = true, HelpText = "Power BI Premium XMLA Address")]
            public string Server { get; set; }

            [Option('d', "database", Required = true, HelpText = "Power BI Dataset name")]
            public string Database { get; set; }

            [Option('o', "output", Required = false, Default = ".\\output", HelpText = "Output folder")]
            public string outputPath { get; set; }

            [Option('u', "user", Required = false, HelpText = "Username / Service Principal Id (app:<id>@<tenantid>)")]
            public string username { get; set; }

            [Option('p', "password", Required = false, HelpText = "Password / Service Principal Secret")]
            public string password { get; set; }

            [Option('e', "events", Required = false, Default = new string[] { "JobGraph", "ProgressReportEnd", "QueryEnd" }, HelpText = "Events to trace, ex: QueryEnd, ProgressReportEnd")]
            public IList<string> events { get; set; }
        }

        private static Dictionary<TraceEventClass, List<TraceColumn>> listEventClassColumnCombination = new Dictionary<TraceEventClass, List<TraceColumn>>();
        private static Microsoft.AnalysisServices.Tabular.Trace trace;
        private static StreamWriter jsonFile;
        private static JsonTextWriter jsonWriter;
        private static Newtonsoft.Json.JsonSerializer serializer;
        private static string traceId;
        private static ILogger logger;
        private static bool receivedTrace = false;
        private static bool cleanedResources = false;
        private static string outputFilePath;
        private static Microsoft.AnalysisServices.Tabular.Server conn;

        private static readonly CancellationTokenSource canToken = new CancellationTokenSource();

        static async Task Main(string[] args)
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

                Console.CancelKeyPress += new ConsoleCancelEventHandler(OnCancel);

                var serviceProvider = Bootstrap.ConfigureServices();

                logger = serviceProvider.GetService<ILogger<Program>>();

                await Parser.Default.ParseArguments<Options>(args)
                      .WithParsedAsync(Worker);

            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.LogError(ex, ex.Message);
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
            traceId = $"PBI_Tracer_{o.Database}";

            logger.LogInformation("PBI Tracer on Server '{0}' | TraceId: '{1}'", o.Server, traceId);

            serializer = new Newtonsoft.Json.JsonSerializer();

            serializer.Error += delegate (object s2, Newtonsoft.Json.Serialization.ErrorEventArgs args2)
            {
                args2.ErrorContext.Handled = true;
            };

            var outputPath = o.outputPath;

            var fileName = $"{o.Database}.{Guid.NewGuid().ToString("N")}.json";

            fileName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));

            outputFilePath = Path.Combine(outputPath, fileName);

            logger.LogInformation("Preparing output file: '{0}'", outputFilePath);

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

                logger.LogInformation("Connecting to server {0}", o.Server);

                conn.Connect(connStr);

                logger.LogInformation("Preparing the trace configuration");

                trace = conn.Traces.Add(traceId);

                trace.StopTime = DateTime.UtcNow.AddHours(1);

                trace.Audit = true;

                trace.Events.Clear();

                foreach (var eventTypeStr in o.events)
                {
                    var eventType = (Microsoft.AnalysisServices.TraceEventClass)Enum.Parse(typeof(Microsoft.AnalysisServices.TraceEventClass), eventTypeStr);
                    AddTraceEvent(trace, eventType);
                }

                trace.Update(UpdateOptions.Default, UpdateMode.CreateOrReplace);

                trace.OnEvent += Trace_OnEvent;

                logger.LogInformation("Starting the trace");

                trace.Start();

                logger.LogInformation("Waiting for trace data, CTRL + C to close");

                while (!canToken.IsCancellationRequested)
                {
                    await Task.Delay(5000);
                }
            }
            finally
            {
                CleanResources();
            }

        }

        protected static void OnCancel(object sender, ConsoleCancelEventArgs args)
        {
            canToken.Cancel();
            args.Cancel = true;
        }

        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            CleanResources();
        }

        private static void Trace_OnEvent(object sender, Microsoft.AnalysisServices.Tabular.TraceEventArgs e)
        {
            try
            {
                logger.LogDebug("TraceEvent: {0} - {1} - {2}", e.EventClass.ToString(), e.EventSubclass.ToString(), e[TraceColumn.ActivityID]);                

                var eventClassColumns = listEventClassColumnCombination[e.EventClass];

                var jsonObj = new JObject();

                foreach (var traceColumn in eventClassColumns)
                {
                    jsonObj.Add(traceColumn.ToString(), e[traceColumn]);
                }

                serializer.Serialize(jsonWriter, jsonObj);

                receivedTrace = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error on 'Trace_OnEvent'");
            }
        }

        private static void AddTraceEvent(Microsoft.AnalysisServices.Tabular.Trace trace, TraceEventClass eventClass)
        {
            logger.LogInformation("Tracing event: {0}", eventClass.ToString());

            var traceEvent = new Microsoft.AnalysisServices.Tabular.TraceEvent(eventClass);

            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.EventClass);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.TextData);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.CurrentTime);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.Spid);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.SessionID);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ActivityID);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.RequestID);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.DatabaseName);
            AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.IntegerData);

            if (eventClass == TraceEventClass.QueryEnd)
            {
                AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.NTUserName);
            }

            if (eventClass != TraceEventClass.DirectQueryEnd && eventClass != TraceEventClass.Error)
            {
                // DirectQuery doesn't have subclasses                
                AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.EventSubclass);
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
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.Duration);
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.CpuTime);
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.StartTime);
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.EndTime);
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ObjectName);
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ObjectID);
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ObjectPath);
                    AddColumnToTraceEvent(traceEvent, eventClass, TraceColumn.ObjectType);
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

        private static void CleanResources()
        {
            if (!cleanedResources)
            {
                logger.LogInformation("Cleaning resources...");

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
                    logger.LogError(ex, "Error closing file");
                }

                try
                {
                    if (trace != null)
                    {
                        trace.Stop();
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
                    logger.LogError(ex, "Error closing trace");
                }

                cleanedResources = true;
            }
        }
    }
}
