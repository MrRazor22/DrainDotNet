
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DrainDotNet
{
    // DRain-like log parser translated from provided Python to C# (single-file).
    // Minimal external dependencies. Adjust paths and usage as needed. 
    public class LogCluster
    {
        public List<string> LogTemplate { get; set; }
        public List<int> LogIDL { get; set; }

        public LogCluster(List<string> logTemplate = null, List<int> logIDL = null)
        {
            LogTemplate = logTemplate ?? new List<string>();
            LogIDL = logIDL ?? new List<int>();
        }
    }

    public class Node
    {
        public Dictionary<object, object> ChildD { get; set; } // keys: int (length) or token string; values: Node or List<LogCluster>
        public int Depth { get; set; }
        public object DigitOrToken { get; set; }

        public Node(Dictionary<object, object> childD = null, int depth = 0, object digitOrToken = null)
        {
            ChildD = childD ?? new Dictionary<object, object>();
            Depth = depth;
            DigitOrToken = digitOrToken;
        }
    }
    public class ParsedLog
    {
        public int LineId { get; set; }
        public string EventId { get; set; }
        public string EventTemplate { get; set; }
        public string Content { get; set; }
        public List<string> ParameterList { get; set; } = new();
        public Dictionary<string, string> ExtraFields { get; set; } = new();
    }

    public class LogParser
    {
        private string PathIn;
        private int Depth;
        private double St;
        private int MaxChild;
        private string LogName;
        private string SavePath;
        private List<ParsedLog> ParsedLogs;
        private string LogFormat;
        private List<string> Rex;
        private List<string> UniqueEventPatterns;
        private bool KeepPara;

        public LogParser(string log_format, string indir = "./", string outdir = "./result/", int depth = 4, double st = 0.4, int maxChild = 100, List<string> rex = null, List<string> uniqueEventPatterns = null, bool keep_para = true)
        {
            PathIn = indir;
            Depth = Math.Max(1, depth - 2);
            St = st;
            MaxChild = maxChild;
            SavePath = outdir;
            LogFormat = log_format;
            Rex = rex ?? new List<string>();
            UniqueEventPatterns = uniqueEventPatterns ?? new List<string>();
            KeepPara = keep_para;
        }

        private bool HasNumbers(string s) => s.Any(char.IsDigit);

        private LogCluster TreeSearch(Node rn, List<string> seq)
        {
            if (!rn.ChildD.ContainsKey(seq.Count)) return null;
            var parentn = (Node)rn.ChildD[seq.Count];
            int seqLen = seq.Count;
            int currentDepth = 1;
            foreach (var token in seq)
            {
                if (currentDepth >= Depth || currentDepth > seqLen) break;
                if (parentn.ChildD.ContainsKey(token))
                    parentn = (Node)parentn.ChildD[token];
                else if (parentn.ChildD.ContainsKey("<*>"))
                    parentn = (Node)parentn.ChildD["<*>"];
                else
                    return null;
                currentDepth++;
            }
            var logClustL = (List<LogCluster>)parentn.ChildD.Values.FirstOrDefault(v => v is List<LogCluster>);
            if (logClustL == null) return null;
            return FastMatch(logClustL, seq);
        }

        private void AddSeqToPrefixTree(Node rn, LogCluster logClust)
        {
            int seqLen = logClust.LogTemplate.Count;
            Node firstLayerNode;
            if (!rn.ChildD.ContainsKey(seqLen))
            {
                firstLayerNode = new Node(new Dictionary<object, object>(), 1, seqLen);
                rn.ChildD[seqLen] = firstLayerNode;
            }
            else firstLayerNode = (Node)rn.ChildD[seqLen];

            Node parentn = firstLayerNode;
            int currentDepth = 1;
            foreach (var token in logClust.LogTemplate)
            {
                if (currentDepth >= Depth || currentDepth > seqLen)
                {
                    if (!parentn.ChildD.Any(kv => kv.Value is List<LogCluster>))
                        parentn.ChildD["leaf"] = new List<LogCluster> { logClust };
                    else
                        ((List<LogCluster>)parentn.ChildD.First(kv => kv.Value is List<LogCluster>).Value).Add(logClust);
                    break;
                }

                if (!parentn.ChildD.ContainsKey(token))
                {
                    if (!HasNumbers(token))
                    {
                        if (parentn.ChildD.ContainsKey("<*>"))
                        {
                            if (parentn.ChildD.Count < MaxChild)
                            {
                                var newNode = new Node(new Dictionary<object, object>(), currentDepth + 1, token);
                                parentn.ChildD[token] = newNode;
                                parentn = newNode;
                            }
                            else
                            {
                                parentn = (Node)parentn.ChildD["<*>"];
                            }
                        }
                        else
                        {
                            if (parentn.ChildD.Count + 1 < MaxChild)
                            {
                                var newNode = new Node(new Dictionary<object, object>(), currentDepth + 1, token);
                                parentn.ChildD[token] = newNode;
                                parentn = newNode;
                            }
                            else if (parentn.ChildD.Count + 1 == MaxChild)
                            {
                                var newNode = new Node(new Dictionary<object, object>(), currentDepth + 1, "<*>");
                                parentn.ChildD["<*>"] = newNode;
                                parentn = newNode;
                            }
                            else
                            {
                                parentn = (Node)parentn.ChildD["<*>"];
                            }
                        }
                    }
                    else
                    {
                        if (!parentn.ChildD.ContainsKey("<*>"))
                        {
                            var newNode = new Node(new Dictionary<object, object>(), currentDepth + 1, "<*>");
                            parentn.ChildD["<*>"] = newNode;
                            parentn = newNode;
                        }
                        else
                            parentn = (Node)parentn.ChildD["<*>"];
                    }
                }
                else
                {
                    parentn = (Node)parentn.ChildD[token];
                }
                currentDepth++;
            }
        }

        private bool MatchesUniqueEventPatterns(string token)
        {
            foreach (var pattern in UniqueEventPatterns)
                if (Regex.Match(token, pattern).Success) return true;
            return false;
        }

        private (double sim, int numPar) SeqDist(List<string> seq1, List<string> seq2)
        {
            if (seq1.Count != seq2.Count) throw new ArgumentException("sequences must be same length");
            int simTokens = 0; int numOfPar = 0;
            for (int i = 0; i < seq1.Count; i++)
            {
                var token1 = seq1[i]; var token2 = seq2[i];
                if (token1 == "<*>")
                {
                    if (MatchesUniqueEventPatterns(token2)) return (0.0, int.MaxValue);
                    numOfPar++; continue;
                }
                if (token1 == token2) simTokens++;
            }
            return ((double)simTokens / seq1.Count, numOfPar);
        }

        private LogCluster FastMatch(List<LogCluster> logClustL, List<string> seq)
        {
            LogCluster ret = null; double maxSim = -1; int maxNumPar = -1; LogCluster maxClust = null;
            foreach (var cl in logClustL)
            {
                var (curSim, curNum) = SeqDist(cl.LogTemplate, seq);
                if (curSim > maxSim || (Math.Abs(curSim - maxSim) < 1e-9 && curNum > maxNumPar))
                {
                    maxSim = curSim; maxNumPar = curNum; maxClust = cl;
                }
            }
            if (maxSim >= St) ret = maxClust;
            return ret;
        }

        private List<string> GetTemplate(List<string> seq1, List<string> seq2)
        {
            if (seq1.Count != seq2.Count) throw new ArgumentException("sequences must be same length");
            var result = new List<string>();
            for (int i = 0; i < seq1.Count; i++)
            {
                var token1 = seq1[i]; var token2 = seq2[i];
                if (token1 == token2) result.Add(token1);
                else if (MatchesUniqueEventPatterns(token1) || MatchesUniqueEventPatterns(token2)) result.Add(seq2[i]);
                else result.Add("<*>");
            }
            return result;
        }

        private void OutputResult(List<LogCluster> logCluL, List<ParsedLog> parsedLogs)
        {
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
                }
            }

            // structured CSV
            var structuredPath = Path.Combine(SavePath, LogName + "_structured.csv");
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

            // templates CSV
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

        private string EscapeCsv(string v) => '"' + v.Replace("\"", "\"\"") + '"';

        private string MD5Short(string input)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString().Substring(0, 8);
            }
        }

        private void PrintTree(Node node, int dep)
        {
            var p = new String(' ', dep * 4);
            if (node.Depth == 0) p += "Root";
            else if (node.Depth == 1) p += "<" + node.DigitOrToken + ">";
            else p += node.DigitOrToken?.ToString();
            Console.WriteLine(p);
            if (node.Depth == Depth) return;
            foreach (var kv in node.ChildD)
                if (kv.Value is Node child) PrintTree(child, dep + 1);
        }

        public List<ParsedLog> Parse(string logName)
        {
            Console.WriteLine("Parsing file: " + Path.Combine(PathIn, logName));
            var start = DateTime.Now;
            LogName = logName;
            var rootNode = new Node();
            var logCluL = new List<LogCluster>();

            LoadData();
            int count = 0;
            foreach (var row in ParsedLogs)
            {
                var content = Preprocess(row.Content);
                var tokens = content.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                var matchCluster = TreeSearch(rootNode, tokens);
                if (matchCluster == null)
                {
                    var newCluster = new LogCluster(tokens, new List<int> { row.LineId });
                    logCluL.Add(newCluster);
                    AddSeqToPrefixTree(rootNode, newCluster);
                }
                else
                {
                    var newTemplate = GetTemplate(tokens, matchCluster.LogTemplate);

                    bool hasStaticMismatch = false;
                    for (int i = 0; i < Math.Min(tokens.Count, matchCluster.LogTemplate.Count); i++)
                    {
                        string t1 = matchCluster.LogTemplate[i];
                        string t2 = tokens[i];

                        if ((MatchesUniqueEventPatterns(t1) || MatchesUniqueEventPatterns(t2)) && t1 != t2)//create new branch if different and matches no rex patterns provided 
                        {
                            hasStaticMismatch = true;
                            break;
                        }
                    }
                    if (hasStaticMismatch)
                    {
                        var newCluster = new LogCluster(tokens, new List<int> { row.LineId });
                        logCluL.Add(newCluster);
                        AddSeqToPrefixTree(rootNode, newCluster);
                    }
                    else
                    {
                        matchCluster.LogIDL.Add(row.LineId);
                        if (!newTemplate.SequenceEqual(matchCluster.LogTemplate))
                            matchCluster.LogTemplate = newTemplate;
                    }
                }
                count++;
                if (count % 1000 == 0 || count == ParsedLogs.Count)
                    Console.WriteLine($"Processed {count * 100.0 / ParsedLogs.Count:0.0}% of log lines.");
            }

            Directory.CreateDirectory(SavePath);
            OutputResult(logCluL, ParsedLogs);
            Console.WriteLine($"Parsing done. [Time taken: {DateTime.Now - start}]");

            return ParsedLogs;
        }

        private void LoadData()
        {
            var (headers, regex) = GenerateLogformatRegex(LogFormat);
            ParsedLogs = LogToParsedLogs(Path.Combine(PathIn, LogName), regex, headers);
        }

        private string Preprocess(string line)
        {
            foreach (var r in Rex) line = Regex.Replace(line, r, "<*>");
            return line;
        }

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

                    // put all other headers into ExtraFields
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
            var regex = new Regex(pattern);
            return (headers, regex);
        }

        //simpler robust faster version
        private List<string> GetParameterListFromTemplate(string template, string content)
        {
            var parameters = new List<string>();
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(content))
                return parameters;

            var templateTokens = template.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var contentTokens = content.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            if (templateTokens.Length != contentTokens.Length)
                return parameters; // mismatch, skip

            for (int i = 0; i < templateTokens.Length; i++)
            {
                var tmplToken = templateTokens[i];
                var contentToken = contentTokens[i];

                if (tmplToken == "<*>")
                {
                    parameters.Add(contentToken);
                }
                else if (tmplToken.Contains("<*>"))
                {
                    parameters.Add(CleanParams(tmplToken, contentToken));
                }
            }
            return parameters;
        }

        private string CleanParams(string tmplToken, string msgToken)
        {
            var pattern = Regex.Escape(tmplToken).Replace(Regex.Escape("<*>"), "(.+?)");
            var match = Regex.Match(msgToken, $"^{pattern}$");
            return match.Success ? match.Groups[1].Value : msgToken;
        }
    }

    // USAGE example (call from Main):
    // var parser = new LogParser("<Date> <Time> <Level> <Content>", indir: "./logs/", outdir: "./out/", depth:4);
    // parser.Parse("syslog.txt"); 
}
