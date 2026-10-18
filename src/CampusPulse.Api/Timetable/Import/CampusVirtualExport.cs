using System.Text.Encodings.Web;
using System.Text.Json;

namespace CampusPulse.Api.Timetable.Import;

/// <summary>
/// What <c>import-timetable --subjects-json FILE</c> writes for scripts/campus-virtual-courses.py: the subjects
/// the workbooks have and the overrides' campusVirtual section, so the script (stdlib Python, no YAML
/// parser) can match them to the catalog and rewrite that section. <see cref="Document.OverridesSha256"/> is the
/// overrides file's: the script refuses a file that changed since, so a stale export can't undo an edit.
/// </summary>
public static class CampusVirtualExport
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record Subject(string Source, string Programme, int Year, string Name, string? ShortName, string Choice);

    public sealed record Document(
        string OverridesSha256,
        IReadOnlyList<string> Programmes,
        IReadOnlyList<string> Search,
        IReadOnlyDictionary<string, List<string>> Codes,
        IReadOnlyDictionary<string, Dictionary<int, Dictionary<string, CampusVirtualCourse>>> Courses,
        IReadOnlyList<Subject> Subjects);

    public static string Json(ImportOverrides overrides, string overridesSha256, IEnumerable<ImportFile> files) =>
        JsonSerializer.Serialize(
            new Document(
                overridesSha256,
                [.. overrides.Programmes.Keys.Order(StringComparer.Ordinal)],
                overrides.CampusVirtual.Search,
                overrides.CampusVirtual.Codes,
                overrides.CampusVirtual.Courses,
                [.. files.SelectMany(f => f.Parsed.Subjects.Values.Select(s => new Subject(f.Source, s.Key.Programme, s.Key.Year, s.Key.Name,
                    s.ShortName, s.Choice.ToString())))
                    .OrderBy(s => s.Programme, StringComparer.Ordinal).ThenBy(s => s.Year).ThenBy(s => s.Name, StringComparer.Ordinal)]),
            Options);
}
