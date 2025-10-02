using DrainDotNet;
using System;
using System.Collections.Generic;
using System.IO;

namespace SampleApp
{
    internal class Program
    {
        static void Main(string[] args)
        {
            string baseDir = AppContext.BaseDirectory;
            string inputDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\data\loghub_2k\HDFS"));
            string outputDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\result"));

            string logFile = "HDFS_2k.log"; // The input log file name
            string logFormat = "<Date> <Time> <Pid> <Level> <Component>: <Content>"; // HDFS log format


            // Regular expression list for optional preprocessing (default: empty)
            var regex = new List<string>
{
@"blk_(|-)[0-9]+", // block id
@"(/|)([0-9]+\.){3}[0-9]+(:[0-9]+|)(:|)", // IP
@"(?<=[^A-Za-z0-9])(\-?\+?\d+)(?=[^A-Za-z0-9])|[0-9]+$" // Numbers
};


            double st = 0.5; // Similarity threshold
            int depth = 4; // Depth of all leaf nodes


            var parser = new LogParser(logFormat, indir: inputDir, outdir: outputDir, depth: depth, st: st, rex: regex);
            parser.Parse(logFile);

            var reloaded = parser.ReloadResults(logFile);
            Console.WriteLine($"Reloaded {reloaded.Count} logs.");
        }
    }
}
