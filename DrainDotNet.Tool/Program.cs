namespace DrainDotNet.Tool
{
    internal class Program
    {
        private const string ToolVersion = "1.0.0";

        /// <summary>
        /// DrainDotNet CLI entry point.
        /// Provides a command-line interface for parsing logs using the Drain algorithm.
        ///
        /// Example usage (PowerShell / CMD):
        ///   draindotnet parse --log HDFS_2k.log --format "<Date> <Time> <Pid> <Level> <Component>: <Content>" --indir ./SampleApp/data/loghub_2k/HDFS --out ./SampleApp/result
        ///
        /// Required:
        ///   --log <file>      Input log file name (inside indir).
        ///   --format <format> Log format string with headers (e.g. "<Date> <Time> <Content>").
        ///
        /// Optional:
        ///   --indir <dir>     Input directory (default: current directory).
        ///   --out <dir>       Output directory (default: ./result).
        ///
        /// Commands:
        ///   parse             Parse logs into structured templates.
        ///   --help, -h        Show usage help.
        ///   --version         Show tool version.
        /// </summary>
        static void Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
            {
                PrintHelp();
                return;
            }

            if (args[0] == "--version")
            {
                Console.WriteLine($"DrainDotNet Tool v{ToolVersion}");
                return;
            }

            if (args[0] != "parse")
            {
                Console.WriteLine("Error: Unknown command. Use 'parse' or --help");
                return;
            }

            string logFile = null;
            string logFormat = null;
            string inputDir = Directory.GetCurrentDirectory();
            string outputDir = Path.Combine(Directory.GetCurrentDirectory(), "result");

            // safer arg parsing
            for (int i = 1; i < args.Length; i++)
            {
                if (i >= args.Length - 1 && args[i].StartsWith("--"))
                {
                    Console.WriteLine($"Error: Missing value for {args[i]}");
                    return;
                }

                switch (args[i])
                {
                    case "--log":
                        logFile = args[++i];
                        break;
                    case "--format":
                        logFormat = args[++i];
                        break;
                    case "--indir":
                        inputDir = args[++i];
                        break;
                    case "--out":
                        outputDir = args[++i];
                        break;
                    default:
                        Console.WriteLine($"Error: Unknown option {args[i]}");
                        return;
                }
            }

            if (string.IsNullOrEmpty(logFile) || string.IsNullOrEmpty(logFormat))
            {
                Console.WriteLine("Error: --log and --format are required.");
                return;
            }

            // default regex list (IPs + numbers)
            var regex = new List<string>
            {
                @"(/|)([0-9]+\.){3}[0-9]+(:[0-9]+|)(:|)", // IPs
                @"(?<=[^A-Za-z0-9])(\-?\+?\d+)(?=[^A-Za-z0-9])|[0-9]+$" // numbers
            };

            double st = 0.5; // similarity threshold
            int depth = 4;   // prefix tree depth

            try
            {
                inputDir = Path.GetFullPath(inputDir);
                outputDir = Path.GetFullPath(outputDir);

                var fullLogPath = Path.Combine(inputDir, logFile);
                Console.WriteLine($"[INFO] Using file: {fullLogPath}");
                Console.WriteLine($"[INFO] Output dir: {outputDir}");
                if (!File.Exists(fullLogPath))
                {
                    Console.WriteLine($"[ERROR] Log file not found: {fullLogPath}");
                    return;
                }
                var parser = new LogParser(logFormat, indir: inputDir, outdir: outputDir, depth: depth, st: st, rex: regex);

                Console.WriteLine($"[INFO] Parsing {logFile}...");
                var parsed = parser.Parse(logFile);

                Console.WriteLine($"[INFO] Parsed {parsed.Count} logs. Results saved to: {outputDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.Message}");
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("DrainDotNet - Log parser based on the Drain algorithm");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  draindotnet parse --log <logFile> --format <logFormat> [--indir <inputDir>] [--out <outputDir>]");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine(@"  draindotnet parse --log HDFS_2k.log --format ""<Date> <Time> <Pid> <Level> <Component>: <Content>"" --indir ./data/HDFS --out ./result");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --log <file>       Input log file name.");
            Console.WriteLine("  --format <format>  Log format string.");
            Console.WriteLine("  --indir <dir>      Input directory (default: current dir).");
            Console.WriteLine("  --out <dir>        Output directory (default: ./result).");
            Console.WriteLine("  --help, -h         Show this help message.");
            Console.WriteLine("  --version          Show version.");
        }
    }
}
