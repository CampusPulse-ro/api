using System.Security.Cryptography;
using CampusPulse.Infrastructure;

namespace CampusPulse.Api.Timetable.Import;

/// <summary>
/// <c>CampusPulse.Api import-timetable --overrides FILE [--dry-run | --parse-only] [--force] [--subjects-json FILE] WORKBOOK.xlsx…</c>:
/// the one-off timetable import (CampusPulse-ro/api#5), run by hand. Never by the app or a deploy.
/// <list type="bullet">
/// <item><c>--parse-only</c> reads the files and prints what it found and every warning; no database.</item>
/// <item><c>--dry-run</c> also compares with the database and prints the changes, writing nothing.</item>
/// <item>Otherwise the same printout, then everything is written in one transaction.</item>
/// <item><c>--subjects-json FILE</c> also writes the subjects read, for scripts/campus-virtual-courses.py.</item>
/// </list>
/// Each workbook is recognised by its sheets (the overrides' sources), never by its file name. The database
/// comes from <c>ConnectionStrings__Postgres</c>, as for the app. On the Pi:
/// deploy/pi/campuspulse-import-timetable. Exit codes: 0 done (or nothing to do), 1 failed, 2 refused (students
/// would lose something, or a listed sheet is missing; --force writes it anyway), 64 usage.
/// </summary>
public static class ImportTimetableCommand
{
    public const string Name = "import-timetable";

    private const string Usage =
        "usage: CampusPulse.Api import-timetable --overrides FILE [--dry-run | --parse-only] [--force] [--subjects-json FILE] "
        + "WORKBOOK.xlsx [WORKBOOK.xlsx …]";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        string? overridesPath = null;
        string? subjectsJsonPath = null;
        var dryRun = false;
        var parseOnly = false;
        var force = false;
        var workbooks = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--overrides" when i + 1 < args.Length:
                    overridesPath = args[++i];
                    break;
                case "--subjects-json" when i + 1 < args.Length:
                    subjectsJsonPath = args[++i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--parse-only":
                    parseOnly = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case var flag when flag.StartsWith("--", StringComparison.Ordinal):
                    error.WriteLine($"unknown option {flag}");
                    error.WriteLine(Usage);
                    return 64;
                default:
                    workbooks.Add(args[i]);
                    break;
            }
        }

        if (overridesPath is null || workbooks.Count == 0 || (dryRun && parseOnly))
        {
            error.WriteLine(Usage);
            return 64;
        }

        try
        {
            var overridesBytes = await File.ReadAllBytesAsync(overridesPath);
            var overrides = ImportOverrides.Parse(System.Text.Encoding.UTF8.GetString(overridesBytes));
            foreach (var warning in overrides.Warnings())
            {
                output.WriteLine($"! {warning}");
            }

            var parser = new SheetParser(overrides);
            var files = new List<ImportFile>();
            foreach (var path in workbooks)
            {
                var bytes = await File.ReadAllBytesAsync(path);
                using var stream = new MemoryStream(bytes);
                var sheets = SheetGrid.Load(stream);
                var fileName = Path.GetFileName(path);
                if (overrides.SourceOf([.. sheets.Select(s => s.Name)], out var problem) is not { } source)
                {
                    error.WriteLine($"import-timetable failed: {fileName}: {problem}");
                    return ImportExit.Failed;
                }

                files.Add(new ImportFile(source, fileName, Sha256(bytes), parser.Parse(source, fileName, sheets)));
            }

            if (files.GroupBy(f => f.Source).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                error.WriteLine($"import-timetable failed: {string.Join(" and ", twice.Select(f => f.FileName))} are both source {twice.Key}");
                return ImportExit.Failed;
            }

            if (subjectsJsonPath is not null)
            {
                await File.WriteAllTextAsync(subjectsJsonPath, CampusVirtualExport.Json(overrides, files));
                output.WriteLine($"Subjects written to {subjectsJsonPath}.");
            }

            if (parseOnly)
            {
                foreach (var file in files)
                {
                    TimetableImporter.PrintDiff(TimetableImporter.Snapshot.Empty, file, output);
                }

                output.WriteLine("Parse only: no database was read or written.");
                return files.Any(f => f.Parsed.Errors.Count > 0) ? ImportExit.Failed : ImportExit.Done;
            }

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddCampusPulsePersistence(builder.Configuration);
            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CampusPulseDbContext>();
            var importer = new TimetableImporter(db, TimeProvider.System, output);
            return await importer.RunAsync(overrides, Sha256(overridesBytes), files, dryRun, force, CancellationToken.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
            or YamlDotNet.Core.YamlException or InvalidOperationException or ArgumentException
            or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            error.WriteLine($"import-timetable failed: {e.Message}");
            return 1;
        }
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
