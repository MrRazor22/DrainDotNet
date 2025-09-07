# DrainDotNet

DrainDotNet is a simple C# port of the popular Drain log parser. It takes raw logs and automatically groups them into templates so you can easily see log patterns.

## How to use
1. Put your log file in the `data` folder (see `Program.cs` for path).
2. Build and run the project.
3. Results will be written into the `result` folder:
   - `*_structured.csv` — each log line matched with a template.
   - `*_templates.csv` — unique log templates with counts.

## Why use it?
- Very easy to run, just point it at your logs.
- Cleans up noisy logs and shows the main patterns.
- Works out of the box with common formats, can be tweaked with regex.

## License
Apache 2.0 (same as the original Drain).
