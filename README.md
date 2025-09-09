# DrainDotNet

DrainDotNet is a C# port of the popular Drain log parser, with several improvements to make it faster, more reliable, and more user-friendly. It takes raw logs and automatically groups them into templates so you can easily see log patterns.

## Key Improvements over the original Drain
- **UniqueEventPatterns**: You can now provide regex patterns that mark certain tokens as “important.” If a log contains these tokens and they change, DrainDotNet will always create a new event/template instead of merging them. This gives users more control over clustering.
- **Faster Parameter Extraction**: The original Drain used regex-heavy logic for extracting parameters. DrainDotNet uses a simpler, token-based method that:
  - Runs much faster (no heavy regex overhead).
  - Handles tricky edge cases like `time: 15> ms` -> which would confuse the original Drain and create broken templates like `time: <*>>`. The new logic avoids this and works cleanly.
- **Edge Case Handling**: Token-based parameter extraction is more robust when logs have unusual punctuation or mixed tokens.

## How to use
1. Put your log file in the `data` folder (see `Program.cs` for path).
2. Build and run the project:
   ```bash
   dotnet run
   ```
3. Results will be written into the `result` folder:
   - `*_structured.csv` — each log line matched with a template (includes `ParameterList`).
   - `*_templates.csv` — unique log templates with counts.

## Why use DrainDotNet?
- Simple to run — just point it at your logs.
- Cleans up noisy logs and shows the main patterns.
- More control with **UniqueEventPatterns**.
- Much faster and more reliable parameter extraction.
- Works out of the box with common formats, and can be tuned with regex.

## License
Apache 2.0 (same as the original Drain).