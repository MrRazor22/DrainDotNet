using CsvHelper;
using System;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DrainDotNet
{
    /// <summary>
    /// LogParser wraps the DrainCore algorithm with:
    ///  - Input parsing (regex-based log format parsing, preprocessing with regex replacements)
    ///  - Orchestration of Drain clustering over ParsedLogs
    ///  - Output enrichment (assigning EventId, EventTemplate, ParameterList)
    ///  - Saving results to CSV (structured logs and templates)
    /// This class is I/O + orchestration. The pure clustering logic lives in DrainCore.
    /// </summary>
    public class LogParser
    {
        private string PathIn;     // directory of input logs
        private string SavePath;   // directory for output results
        private string LogName;    // current log filename

        private string LogFormat;  // user-specified log format pattern, e.g. "<Date> <Time> <Content>"
        private List<string> Rex;  // regex patterns for preprocessing tokens
        private bool KeepPara;     // whether to extract parameter values from templates

        private readonly DrainCore Core;   // core clustering engine

        private List<ParsedLog> ParsedLogs;  // parsed log lines (with headers, content, metadata)

        /// <summary>
        /// Construct a LogParser with log format and config options.
        /// </summary>
        public LogParser(
            string log_format,
            string indir = "./",
            string outdir = "./result/",
            int depth = 4,
            double st = 0.4,
            int maxChild = 100,
            List<string> rex = null,
            List<string> uniqueEventPatterns = null,
            bool keep_para = true)
        {
            PathIn = indir;
            SavePath = outdir;
            LogFormat = log_format;
            Rex = rex ?? new List<string>();
            KeepPara = keep_para;

            Core = new DrainCore(depth, st, maxChild, uniqueEventPatterns ?? new List<string>());
        }

        /// <summary>
        /// Main entry point: parse the given log file.
        /// Steps:
        ///  - Load raw log lines and apply regex parsing
        ///  - Preprocess tokens with Rex
        ///  - Incrementally feed lines into DrainCore to build clusters
        ///  - Enrich parsed logs with EventId/EventTemplate
        ///  - Optionally save structured results to CSV
        /// </summary>
        public List<ParsedLog> Parse(string logName, bool autoSave = true)
        {
            Console.WriteLine("Parsing file: " + Path.Combine(PathIn, logName));
            var start = DateTime.Now;
            LogName = logName;

            Core.Clusters.Clear();
            Core.Root.ChildD.Clear();

            LoadData();

            int count = 0;
            foreach (var row in ParsedLogs)
            {
                var content = Preprocess(row.Content);
                var tokens = content.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                var matchCluster = Core.TreeSearch(tokens);

                if (matchCluster == null)
                {
                    Core.AddNewCluster(tokens, row.LineId);
                }
                else
                {
                    // if unique-event regex forces separation, create new cluster
                    bool hasStaticMismatch = false;
                    for (int i = 0; i < Math.Min(tokens.Count, matchCluster.LogTemplate.Count); i++)
                    {
                        string t1 = matchCluster.LogTemplate[i];
                        string t2 = tokens[i];
                        if ((Core.MatchesUniqueEventPatterns(t1) || Core.MatchesUniqueEventPatterns(t2)) && t1 != t2)
                        {
                            hasStaticMismatch = true;
                            break;
                        }
                    }

                    if (hasStaticMismatch)
                        Core.AddNewCluster(tokens, row.LineId);
                    else
                        Core.MergeIntoCluster(matchCluster, tokens, row.LineId);
                }

                count++;
                if (count % 1000 == 0 || count == ParsedLogs.Count)
                    Console.WriteLine($"Processed {count * 100.0 / ParsedLogs.Count:0.0}% of log lines.");
            }

            EnrichLogsWithEventIdTemplateParameters(Core.Clusters, ParsedLogs);
            if (autoSave) SaveResults(ParsedLogs, LogName);

            Console.WriteLine($"Parsing done. [Time taken: {DateTime.Now - start}]");
            return ParsedLogs;
        }

        /// <summary>
        /// Reloads previously saved structured logs from CSV for a given log file name.
        /// 
        /// The output path is derived deterministically as:
        ///   {SavePath}\{logName}_structured.csv
        /// 
        /// Example:
        ///   Input:  "HDFS_2k.log"
        ///   Output: "HDFS_2k.log_structured.csv"
        /// 
        /// This allows you to rehydrate ParsedLog objects even after an application restart,
        /// without needing to keep file paths in memory. It uses CsvHelper internally to
        /// parse the CSV and reconstructs LineId, EventId, EventTemplate, Content,
        /// ParameterList, and any ExtraFields.
        /// 
        /// Usage:
        ///   var parser = new LogParser("<Date> <Time> <Content>", outdir: @"D:\Logs");
        ///   var logs = parser.ReloadResults("system.log"); // loads "system.log_structured.csv"
        /// </summary>
        /// <param name="logName">The original input log filename (e.g. "system.log").</param>
        /// <returns>List of ParsedLog objects reconstructed from the saved structured CSV.</returns>
        public List<ParsedLog> ReloadResults(string logName)
        {
            // preserve extension: "HDFS_2k.log" => "HDFS_2k.log_structured.csv"
            var structuredPath = Path.Combine(SavePath, logName + "_structured.csv");

            if (!File.Exists(structuredPath))
                throw new FileNotFoundException("Structured CSV not found", structuredPath);

            var logs = new List<ParsedLog>();
            using (var reader = new StreamReader(structuredPath))
            using (var csv = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture))
            {
                foreach (var record in csv.GetRecords<dynamic>())
                {
                    var dict = (IDictionary<string, object>)record;

                    var log = new ParsedLog
                    {
                        LineId = int.Parse(dict["LineId"].ToString()),
                        EventId = dict["EventId"]?.ToString(),
                        EventTemplate = dict["EventTemplate"]?.ToString(),
                        Content = dict["Content"]?.ToString(),
                        ParameterList = dict["ParameterList"]?.ToString().Split('|').ToList() ?? new List<string>(),
                        ExtraFields = dict
                            .Where(kv => kv.Key != "LineId" &&
                                         kv.Key != "EventId" &&
                                         kv.Key != "EventTemplate" &&
                                         kv.Key != "Content" &&
                                         kv.Key != "ParameterList")
                            .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "")
                    };
                    logs.Add(log);
                }
            }
            return logs;
        }


        /// <summary>
        /// Parse input log file into ParsedLog objects using log format regex.
        /// </summary>
        private void LoadData()
        {
            var (headers, regex) = GenerateLogformatRegex(LogFormat);
            ParsedLogs = LogToParsedLogs(Path.Combine(PathIn, LogName), regex, headers);
        }

        /// <summary>
        /// Apply regex substitutions (Rex) to normalize tokens (replace with <*>).
        /// </summary>
        private string Preprocess(string line)
        {
            foreach (var r in Rex) line = Regex.Replace(line, r, "<*>");
            return line;
        }

        /// <summary>
        /// Turn raw file lines into ParsedLogs using regex pattern.
        /// Each log gets LineId, Content, and ExtraFields from headers.
        /// </summary>
        private List<ParsedLog> LogToParsedLogs(string logFile, Regex regex, List<string> headers)
        {
            var logs = new List<ParsedLog>();
            int linecount = 0;
            foreach (var line in File.ReadLines(logFile))
            {
                try
                {
                    var m = regex.Match(line.Trim());
                    if (!m.Success || m.Groups.Count == 0)
                    {
                        Console.WriteLine("[Warning] Skip line: " + line);
                        continue;
                    }

                    var log = new ParsedLog
                    {
                        LineId = ++linecount,
                        Content = m.Groups["Content"].Success ? m.Groups["Content"].Value : ""
                    };

                    foreach (var h in headers)
                    {
                        if (h == "Content") continue;
                        log.ExtraFields[h] = m.Groups[h].Success ? m.Groups[h].Value : "";
                    }
                    logs.Add(log);
                }
                catch
                {
                    Console.WriteLine("[Warning] Skip line: " + line);
                }
            }
            Console.WriteLine("Total lines: " + logs.Count);
            return logs;
        }

        /// <summary>
        /// Generate regex for given log format string, e.g. "<Date> <Time> <Content>"
        /// Returns list of header names and compiled regex.
        /// </summary>
        private (List<string> headers, Regex regex) GenerateLogformatRegex(string logformat)
        {
            var headers = new List<string>();
            var splitters = Regex.Split(logformat, "(<[^<>]+>)");
            var sb = new StringBuilder();
            for (int k = 0; k < splitters.Length; k++)
            {
                if (k % 2 == 0)
                {
                    var splitter = Regex.Replace(splitters[k], " +", "\\s+");
                    sb.Append(splitter);
                }
                else
                {
                    var header = splitters[k].Trim('<', '>');
                    sb.Append($"(?<{header}>.*?)");
                    headers.Add(header);
                }
            }
            var pattern = "^" + sb.ToString() + "$";
            return (headers, new Regex(pattern));
        }

        /// <summary>
        /// Assigns EventId (MD5 hash), EventTemplate (string template), and extracted parameters to ParsedLogs.
        /// Adds progress output every 1000 enriched logs and prints total elapsed time.
        /// </summary>
        private void EnrichLogsWithEventIdTemplateParameters(List<LogCluster> logCluL, List<ParsedLog> parsedLogs)
        {
            var start = DateTime.Now;
            int total = logCluL.Sum(c => c.LogIDL.Count);
            int processed = 0;

            foreach (var cluster in logCluL)
            {
                var templateStr = string.Join(" ", cluster.LogTemplate);
                var templateId = MD5Short(templateStr);

                foreach (var logId in cluster.LogIDL)
                {
                    var log = parsedLogs.First(l => l.LineId == logId);
                    log.EventId = templateId;
                    log.EventTemplate = templateStr;
                    if (KeepPara && (log.ParameterList == null || log.ParameterList.Count == 0))
                        log.ParameterList = GetParameterListFromTemplate(log.EventTemplate, log.Content);

                    processed++;
                    if (processed % 1000 == 0 || processed == total)
                        Console.WriteLine($"Enriched {processed * 100.0 / total:0.0}% of logs.");
                }
            }

            Console.WriteLine($"Enrichment done. [Time taken: {DateTime.Now - start}]");
        }


        /// <summary>
        /// Save structured logs and templates to CSV files in SavePath.
        /// </summary>
        private void SaveResults(List<ParsedLog> parsedLogs, string logName)
        {
            Directory.CreateDirectory(SavePath);

            var structuredPath = Path.Combine(SavePath, logName + "_structured.csv");
            using (var w = new StreamWriter(structuredPath))
            {
                var headers = new List<string> { "LineId", "EventId", "EventTemplate", "Content", "ParameterList" }
                              .Concat(parsedLogs.SelectMany(l => l.ExtraFields.Keys).Distinct()).ToList();
                w.WriteLine(string.Join(",", headers.Select(EscapeCsv)));

                foreach (var log in parsedLogs)
                {
                    var row = new List<string>
                    {
                        log.LineId.ToString(),
                        log.EventId,
                        log.EventTemplate,
                        log.Content,
                        string.Join("|", log.ParameterList)
                    };
                    row.AddRange(headers.Skip(5).Select(h => log.ExtraFields.ContainsKey(h) ? log.ExtraFields[h] : ""));
                    w.WriteLine(string.Join(",", row.Select(EscapeCsv)));
                }
            }

            var templateCounts = parsedLogs.GroupBy(l => l.EventTemplate)
                                           .ToDictionary(g => g.Key, g => g.Count());
            var templatePath = Path.Combine(SavePath, LogName + "_templates.csv");
            using (var w = new StreamWriter(templatePath))
            {
                w.WriteLine("EventId,EventTemplate,Occurrences");
                foreach (var kv in templateCounts)
                {
                    var id = MD5Short(kv.Key);
                    w.WriteLine($"{id},{EscapeCsv(kv.Key)},{kv.Value}");
                }
            }
        }

        private string EscapeCsv(string v) => '"' + (v ?? "").Replace("\"", "\"\"") + '"';

        private string MD5Short(string input)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
                var sb = new StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString().Substring(0, 8);
            }
        }

        /// <summary>
        /// Extracts parameter values from log content based on the template's <*> tokens.
        /// </summary>
        private List<string> GetParameterListFromTemplate(string template, string content)
        {
            var parameters = new List<string>();
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(content))
                return parameters;

            var templateTokens = template.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var contentTokens = content.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (templateTokens.Length != contentTokens.Length)
                return parameters;

            for (int i = 0; i < templateTokens.Length; i++)
            {
                var tmplToken = templateTokens[i];
                var contentToken = contentTokens[i];

                if (tmplToken == "<*>")
                    parameters.Add(contentToken);
                else if (tmplToken.Contains("<*>"))
                    parameters.Add(CleanParams(tmplToken, contentToken));
            }
            return parameters;
        }

        private string CleanParams(string tmplToken, string msgToken)
        {
            var pattern = Regex.Escape(tmplToken).Replace(Regex.Escape("<*>"), "(.+?)");
            var match = Regex.Match(msgToken, $"^{pattern}$");
            return match.Success ? match.Groups[1].Value : msgToken;
        }

        /// <summary>
        /// Debug: print the current prefix tree to console.
        /// </summary>
        private void PrintTree(Node node, int dep)
        {
            var p = new string(' ', dep * 4);
            if (node.Depth == 0) p += "Root";
            else if (node.Depth == 1) p += "<" + node.DigitOrToken + ">";
            else p += node.DigitOrToken?.ToString();
            Console.WriteLine(p);
            if (node.Depth == GetDepthInternal()) return;
            foreach (var kv in node.ChildD)
                if (kv.Value is Node child) PrintTree(child, dep + 1);
        }

        private int GetDepthInternal()
            => typeof(DrainCore).GetField("Depth", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is var fi && fi != null
               ? (int)fi.GetValue(Core)
               : 0;
    }
}
