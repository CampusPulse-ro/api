using CampusPulse.Api.Timetable.Import;
using ClosedXML.Excel;

namespace CampusPulse.Api.Tests;

/// <summary>
/// Builds small workbooks shaped like the AC faculty's timetables, for the importer's tests: day blocks
/// of hourly rows (08-09 … 20-21) labelled in column B, a group header row, merged cells. The real
/// files are UPT-only and never go in this repo, not even as fixtures.
/// </summary>
public sealed class TimetableWorkbook : IDisposable
{
    private readonly XLWorkbook _workbook = new();
    private byte[]? _bytes;

    /// <param name="days">the day names spelled down column A, one per block (default Monday to Friday, in Romanian).</param>
    public TimetableSheet Sheet(string name, int firstGridRow = 5, string[]? days = null) =>
        new(_workbook.AddWorksheet(name), firstGridRow, days ?? ["LUNI", "MARTI", "MIERCURI", "JOI", "VINERI"]);

    /// <summary>The saved file. Saved once: ClosedXML can't save the same workbook twice.</summary>
    public byte[] Bytes()
    {
        if (_bytes is null)
        {
            using var stream = new MemoryStream();
            _workbook.SaveAs(stream);
            _bytes = stream.ToArray();
        }

        return _bytes;
    }

    public IReadOnlyList<SheetGrid> Grids()
    {
        using var stream = new MemoryStream(Bytes());
        return SheetGrid.Load(stream);
    }

    public void Dispose() => _workbook.Dispose();
}

public sealed class TimetableSheet
{
    public const int HoursPerDay = 13;

    private readonly IXLWorksheet _sheet;
    private readonly int _firstGridRow;

    public TimetableSheet(IXLWorksheet sheet, int firstGridRow, string[] days)
    {
        _sheet = sheet;
        _firstGridRow = firstGridRow;
        for (var day = 0; day < days.Length; day++)
        {
            for (var hour = 8; hour < 8 + HoursPerDay; hour++)
            {
                var row = Row(day, hour);
                _sheet.Cell(row, 2).Value = $"{hour:00}-{hour + 1:00}";
                var letter = hour - 10;
                if (letter >= 0 && letter < days[day].Length)
                {
                    _sheet.Cell(row, 1).Value = days[day][letter].ToString();
                }
            }
        }
    }

    /// <summary>The row of an hour on a day (0 = Monday): days are 13 rows plus a blank one apart.</summary>
    public int Row(int day, int hour) => _firstGridRow + (day * (HoursPerDay + 1)) + (hour - 8);

    public int LastGridRow => Row(4, 20);

    public TimetableSheet Text(int row, string column, string text)
    {
        _sheet.Cell(row, column).Value = text;
        return this;
    }

    /// <summary>Text in a merged area (or one cell when it's a single one).</summary>
    public TimetableSheet Merged(int row, string column, int lastRow, string lastColumn, string text)
    {
        _sheet.Cell(row, column).Value = text;
        if (lastRow != row || lastColumn != column)
        {
            _sheet.Range(row, XLHelper.GetColumnNumberFromLetter(column), lastRow, XLHelper.GetColumnNumberFromLetter(lastColumn)).Merge();
        }

        return this;
    }

    /// <summary>A header row of group codes starting at <paramref name="firstColumn"/>.</summary>
    public TimetableSheet Groups(int row, string firstColumn, params string[] codes)
    {
        var column = XLHelper.GetColumnNumberFromLetter(firstColumn);
        foreach (var code in codes)
        {
            _sheet.Cell(row, column++).Value = code;
        }

        return this;
    }

    /// <summary>An activity on <paramref name="day"/> from <paramref name="hour"/> for <paramref name="hours"/>
    /// rows, across columns <paramref name="column"/> to <paramref name="lastColumn"/>.</summary>
    public TimetableSheet Activity(int day, int hour, string column, string text, int hours = 2, string? lastColumn = null) =>
        Merged(Row(day, hour), column, Row(day, hour) + hours - 1, lastColumn ?? column, text);
}
