using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DrainDotNet
{
    #region DataTypes
    /// <summary>
    /// Represents a log cluster: a set of log lines that share a common template.
    /// </summary>
    public class LogCluster
    {
        /// <summary>
        /// The log template (token sequence with static and <*> placeholders).
        /// </summary>
        public List<string> LogTemplate { get; set; }

        /// <summary>
        /// The line IDs of logs belonging to this cluster.
        /// </summary>
        public List<int> LogIDL { get; set; }

        public LogCluster(List<string> logTemplate = null, List<int> logIDL = null)
        {
            LogTemplate = logTemplate ?? new List<string>();
            LogIDL = logIDL ?? new List<int>();
        }
    }

    /// <summary>
    /// A node in the prefix tree used by Drain to organize log templates.
    /// </summary>
    public class Node
    {
        /// <summary>
        /// Child dictionary: key can be int (length) or token string; value is Node or list of LogCluster.
        /// </summary>
        public Dictionary<object, object> ChildD { get; set; }

        /// <summary>
        /// Depth of this node in the tree.
        /// </summary>
        public int Depth { get; set; }

        /// <summary>
        /// Token or digit this node represents.
        /// </summary>
        public object DigitOrToken { get; set; }

        public Node(Dictionary<object, object> childD = null, int depth = 0, object digitOrToken = null)
        {
            ChildD = childD ?? new Dictionary<object, object>();
            Depth = depth;
            DigitOrToken = digitOrToken;
        }
    }

    /// <summary>
    /// Parsed representation of a log line before clustering.
    /// </summary>
    public class ParsedLog
    {
        public int LineId { get; set; }
        public string EventId { get; set; }
        public string EventTemplate { get; set; }
        public string Content { get; set; }
        public List<string> ParameterList { get; set; } = new List<string>();
        public Dictionary<string, string> ExtraFields { get; set; } = new Dictionary<string, string>();
    }
    #endregion

    // ======= PURE DRAIN ALGO =======

    /// <summary>
    /// Implementation of the Drain algorithm core logic (clustering and template management).
    /// Does not perform I/O — only works on token sequences and maintains in-memory clusters.
    /// </summary>
    public class DrainCore
    {
        private readonly int Depth;                // max depth of the prefix tree
        private readonly double St;                // similarity threshold
        private readonly int MaxChild;             // max children a node can have
        private readonly List<string> UniqueEventPatterns; // regex patterns that should not be wildcarded

        /// <summary>
        /// Root node of the prefix tree.
        /// </summary>
        public Node Root { get; } = new Node();

        /// <summary>
        /// List of all clusters formed so far.
        /// </summary>
        public List<LogCluster> Clusters { get; } = new List<LogCluster>();

        public DrainCore(int depth = 4, double st = 0.4, int maxChild = 100, List<string> uniqueEventPatterns = null)
        {
            Depth = Math.Max(1, depth - 2);
            St = st;
            MaxChild = maxChild;
            UniqueEventPatterns = uniqueEventPatterns ?? new List<string>();
        }

        private bool HasNumbers(string s) => s.Any(char.IsDigit);

        /// <summary>
        /// Checks whether the token matches any user-provided "unique event" regex.
        /// Such tokens are not replaced with wildcards.
        /// </summary>
        public bool MatchesUniqueEventPatterns(string token) => UniqueEventPatterns.Any(p => Regex.Match(token, p).Success);

        /// <summary>
        /// Search the prefix tree for the best matching cluster for the given token sequence.
        /// </summary>
        public LogCluster TreeSearch(List<string> seq)
        {
            if (!Root.ChildD.ContainsKey(seq.Count)) return null;
            var parentn = (Node)Root.ChildD[seq.Count];
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

        /// <summary>
        /// Add a brand new cluster with the given token sequence.
        /// </summary>
        public void AddNewCluster(List<string> tokens, int lineId)
        {
            var newCluster = new LogCluster(tokens, new List<int> { lineId });
            Clusters.Add(newCluster);
            AddSeqToPrefixTree(newCluster);
        }

        /// <summary>
        /// Merge a new log line into an existing cluster, updating the template if needed.
        /// </summary>
        public void MergeIntoCluster(LogCluster matchCluster, List<string> tokens, int lineId)
        {
            var newTemplate = GetTemplate(tokens, matchCluster.LogTemplate);
            matchCluster.LogIDL.Add(lineId);
            if (!newTemplate.SequenceEqual(matchCluster.LogTemplate))
                matchCluster.LogTemplate = newTemplate;
        }

        /// <summary>
        /// Generate a new template from two token sequences, replacing mismatched positions with <*>.
        /// Unique-event tokens are preserved if configured.
        /// </summary>
        public List<string> GetTemplate(List<string> seq1, List<string> seq2)
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

        /// <summary>
        /// Compute similarity between two sequences: ratio of exact matches, and number of parameters.
        /// </summary>
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

        /// <summary>
        /// Among candidate clusters, pick the best match by similarity and parameter count.
        /// </summary>
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

        /// <summary>
        /// Add a cluster's template into the prefix tree structure.
        /// </summary>
        private void AddSeqToPrefixTree(LogCluster logClust)
        {
            int seqLen = logClust.LogTemplate.Count;
            Node firstLayerNode;
            if (!Root.ChildD.ContainsKey(seqLen))
            {
                firstLayerNode = new Node(new Dictionary<object, object>(), 1, seqLen);
                Root.ChildD[seqLen] = firstLayerNode;
            }
            else firstLayerNode = (Node)Root.ChildD[seqLen];

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
    }
}
