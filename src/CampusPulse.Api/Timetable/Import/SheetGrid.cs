using ClosedXML.Excel;

namespace CampusPulse.Api.Timetable.Import;

/// <summary>A cell with text: its top-left position and, when merged, the last row and column it covers.</summary>
public sealed record GridCell(int Row, int Column, int LastRow, int LastColumn, string Text)
{
    public string Address(string sheet) =>
        LastRow == Row && LastColumn == Column
            ? $"{sheet}!{Letter(Column)}{Row}"
            : $"{sheet}!{Letter(Column)}{Row}:{Letter(LastColumn)}{LastRow}";

    /// <summary>The <c>Sheet!A1</c> key the overrides file uses (the top-left cell).</summary>
    public string Key(string sheet) => $"{sheet}!{Letter(Column)}{Row}";

    public static string Letter(int column)
    {
        var letters = "";
        for (; column > 0; column = (column - 1) / 26)
        {
            letters = (char)('A' + ((column - 1) % 26)) + letters;
        }

        return letters;
    }

    public static int ColumnNumber(string letters) =>
        letters.ToUpperInvariant().Aggregate(0, (n, c) => (n * 26) + (c - 'A' + 1));
}

/// <summary>
/// One worksheet as the parser sees it: the text of every cell that has some, each merged area as one
/// cell at its top-left corner. Formatting is ignored; nothing in the faculty's sheets depends on it.
/// </summary>
public sealed class SheetGrid
{
    private readonly Dictionary<(int Row, int Column), GridCell> _cells = [];
    private readonly Dictionary<(int Row, int Column), GridCell> _covering = [];

    public SheetGrid(string name, IEnumerable<GridCell> cells)
    {
        Name = name;
        foreach (var cell in cells)
        {
            _cells[(cell.Row, cell.Column)] = cell;
            for (var row = cell.Row; row <= cell.LastRow; row++)
            {
                for (var column = cell.Column; column <= cell.LastColumn; column++)
                {
                    _covering[(row, column)] = cell;
                }
            }
        }
    }

    public string Name { get; }

    public IEnumerable<GridCell> Cells => _cells.Values.OrderBy(c => c.Row).ThenBy(c => c.Column);

    /// <summary>The cell whose top-left corner is here, or null.</summary>
    public GridCell? At(int row, int column) => _cells.GetValueOrDefault((row, column));

    /// <summary>The cell covering this position (its own, or a merged area's), or null.</summary>
    public GridCell? Covering(int row, int column) => _covering.GetValueOrDefault((row, column));

    public string Text(int row, int column) => At(row, column)?.Text ?? "";

    public static IReadOnlyList<SheetGrid> Load(string path)
    {
        using var workbook = new XLWorkbook(path);
        return [.. workbook.Worksheets.Select(FromWorksheet)];
    }

    public static IReadOnlyList<SheetGrid> Load(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        return [.. workbook.Worksheets.Select(FromWorksheet)];
    }

    private static SheetGrid FromWorksheet(IXLWorksheet sheet)
    {
        var merged = new Dictionary<(int, int), IXLRange>();
        var covered = new HashSet<(int, int)>();
        foreach (var range in sheet.MergedRanges)
        {
            var first = range.FirstCell().Address;
            var last = range.LastCell().Address;
            merged[(first.RowNumber, first.ColumnNumber)] = range;
            for (var row = first.RowNumber; row <= last.RowNumber; row++)
            {
                for (var column = first.ColumnNumber; column <= last.ColumnNumber; column++)
                {
                    if (row != first.RowNumber || column != first.ColumnNumber)
                    {
                        covered.Add((row, column));
                    }
                }
            }
        }

        var cells = new List<GridCell>();
        foreach (var cell in sheet.CellsUsed(XLCellsUsedOptions.Contents))
        {
            var (row, column) = (cell.Address.RowNumber, cell.Address.ColumnNumber);
            if (covered.Contains((row, column)))
            {
                continue;
            }

            var text = cell.GetFormattedString().Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var (lastRow, lastColumn) = merged.TryGetValue((row, column), out var range)
                ? (range.LastCell().Address.RowNumber, range.LastCell().Address.ColumnNumber)
                : (row, column);
            cells.Add(new GridCell(row, column, lastRow, lastColumn, text));
        }

        return new SheetGrid(sheet.Name, cells);
    }
}
