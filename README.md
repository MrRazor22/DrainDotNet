# DrainDotNet

DrainDotNet is a C# port of the popular Drain log parser, with several improvements to make it faster, more reliable, and more user-friendly. It takes raw logs and automatically groups them into templates so you can easily see log patterns.

## Key Improvements over the original Drain
- **UniqueEventPatterns**: You can provide regex patterns that mark certain tokens as *important*. If a log contains these tokens and they change, DrainDotNet will always create a new event/template instead of merging them. This gives you more control over clustering.
- **Faster Parameter Extraction**: The original Drain used regex-heavy logic for extracting parameters. DrainDotNet uses a simpler, token-based method that:
  - Runs much faster (no heavy regex overhead).
  - Handles tricky cases like `time: 15> ms`, which used to confuse Drain and produce broken templates like `time: <*>>`.
- **Edge Case Handling**: Robust against logs with odd punctuation or mixed tokens.
- **Strongly Typed Output**: `Parse()` returns a `List<ParsedLog>` in code (with `LineId`, `Content`, `EventId`, `EventTemplate`, `ParameterList`, and extra fields), so you don’t have to re-parse CSVs if you want to use results directly.
- **Optional Auto-Save**: Results are saved to CSV by default. You can disable this with `autoSave: false` if you only want in-memory results.
- **MD5 Hash Event IDs**: Templates get stable 8-character Event IDs. Collisions are theoretically possible, but for typical datasets (even 100k+ templates) it’s practically safe.

## How to use
1. Put your log file in the `data` folder (see `Program.cs` for path).
2. Build and run the project.
3. Results will be written into the `outputDir` path specified:
   - `*_structured.csv` — each log line matched with a template (includes `ParameterList`).
   - `*_templates.csv` — unique log templates with counts.

   Or use directly in code:

	```csharp
	using DrainDotNet;
	var logFormat = "<Date> <Time> <Pid> <Level> <Component> <Content>";
	var parser = new LogParser(logFormat, indir: "./data/", outdir: "./result/");
	// Parse logs and also save CSVs (default)
	var parsedLogs = parser.Parse("HDFS.log");
	// Parse logs but keep results in memory only
	var parsedInMemory = parser.Parse("HDFS.log", autoSave: false);
	```



## License
Apache 2.0 (same as the original Drain).