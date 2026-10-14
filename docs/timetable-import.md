# Importing a semester's timetable

The faculty publishes each semester's timetables as Excel workbooks, shared with UPT accounts only
(CampusPulse-ro/api#5). The API has an import for them: a mode of the API binary, run by hand, never by
the app or a deploy.

**Never commit the faculty's files or the overrides file.** Both describe UPT-only data, and this repo
is public. Keep them in a private folder outside the clone. The tests build small synthetic workbooks
of their own (`tests/CampusPulse.Api.Tests/TimetableWorkbook.cs`). Every name, subject and room in them,
and in the examples below, is invented.

## Running it

```bash
# Locally, against your dev database (ConnectionStrings__Postgres, as for the app):
dotnet run --project src/CampusPulse.Api -- import-timetable --overrides ../private/overrides.yaml --parse-only ../private/*.xlsx
dotnet run --project src/CampusPulse.Api -- import-timetable --overrides ../private/overrides.yaml --dry-run ../private/*.xlsx

# On the Pi, with the release an environment runs (deploy/pi/README.md):
campuspulse-import-timetable stage --overrides overrides.yaml --dry-run Timetable*.xlsx
campuspulse-import-timetable stage --overrides overrides.yaml Timetable*.xlsx
```

- **`--parse-only`** reads the files and prints everything it found, plus every warning. It needs no
  database: use it while writing the overrides.
- **`--dry-run`** does everything a real run does, in a transaction it then rolls back: it prints what
  would change (`+` added, `-` removed), what students would lose, and whether it would be refused.
- **Without either**, it does the same and commits.
- A workbook whose contents (SHA-256), the overrides (SHA-256) and the parser version all match its last
  import is skipped. `--force` imports it anyway.
- **Exit codes:**
  - 0: done, or nothing to do;
  - 1: failed (an error, an unreadable sheet, an unknown workbook);
  - 2: refused, nothing written;
  - 64: usage.

**Read every warning.** Each names its cell (`An 1!K33`), and is either fixed in the overrides or
accepted. Warnings cover:
- one-hour cells;
- unresolved or guessed abbreviations;
- offers without a time;
- a cell with two marks;
- a cell in a group column that became a note;
- repeated group columns;
- overrides that match nothing.

## Workbooks are recognised by their sheets

The overrides' `sources` give each workbook an id of ours and the sheets it holds. A workbook is
whichever source's sheets it has, never its file name: "Timetable (1).xlsx" or "timetable_v2.xlsx"
replaces what "Timetable.xlsx" brought, instead of adding a second copy of every class.

The import refuses, with exit 1:
- a workbook with no known source's sheets, or with sheets of two sources;
- two workbooks of one source in one run;
- a source whose sheets were already imported under another id (changing an id would double every class:
  put the old id back).

## What a re-import keeps, and when it refuses

Per source, importing a new version:

- **replaces** its slots and notes;
- **keeps** its study groups, subjects and offered slots while they're the same:
  - groups by programme, year, series, group and subgroup;
  - subjects by name, ignoring case and diacritics;
  - offers by day, times, room and weeks.

  So students' groups, optional subjects and picks survive;
- **retires** the groups and subjects the workbook no longer has (never deletes them). A student keeps
  them, shown as "no longer offered" (`noLongerOffered` in `GET /me/settings` and `GET /me/subjects`), and
  they leave the pickers. One that comes back in a later import is offered again;
- **deletes** offered slots that changed or went, which clears the picks of them.

Before writing, it counts what students would lose:
- how many students' groups are no longer offered;
- how many chosen subjects are no longer offered;
- how many picks would be cleared.

If any count is above zero, or a sheet its source lists is missing from the workbook, the run is
**refused** (exit 2, nothing written) until it's run again with `--force`. A renamed subject counts too:
it's a new subject, and the old one is retired.

## How the sheets are read

The grid is the same on every sheet the faculty publishes:

- **Rows:** day blocks of hourly rows labelled `08-09` … `20-21` in column B, the day's name spelled down
  column A (`LUNI`, `MARTI` …, or `MONDAY` …); blank rows separate the days.
  - A block is the day its name says.
  - A sheet whose day names can't be read is read Monday to Friday in order, if it has exactly five
    blocks.
  - Otherwise it's an error, and nothing is written.
- **Columns:** one per subgroup (`1.1`, `1.2`, …), in a header row above the grid, under labels naming the
  series or specialisation. A sheet without group columns (master programmes) is one group.
- **A merged cell is one activity** for every subgroup it covers, for as many hours as its rows. A course
  merged across a whole series is one slot linked to all its subgroups.
- **The text** is the subject, a mark, the teacher, the room and the weeks:
  - marks: `(c)` course, `(l)` lab, `(s)` seminar, `(p)` project, `(c.f.)` facultative course, and
    combinations like `(l+p)`;
  - teacher: initials and a surname (`A. Testescu`);
  - room: after `sala`/`room`, or anything shaped like a room code (`R418a`, `707CP`);
  - weeks: `sapt 1-10`, `(saptamanile 3, 5, 7, 9)`, `weeks 5-14`, `weeks 2 & 4`, `s. 1-7 s. impara`,
    `odd weeks`, `începand cu a 2-a săptămână`, `2h/12sapt`. Without any, every week. Weeks that come to
    none (`sapt 15-16` in a 14-week semester) skip the activity, with a warning.

  The text is often split over a second cell just below with the same width (the room and weeks): that
  cell continues the first.
- **Two marks on separate lines** (`Esperanto (s) …` ⏎ `Volapuk (s) …`) are two activities.
- **A cell without a mark** in a group column is a note, with a warning. The overrides can make it an
  activity (`kind`).
- **One-hour cells, every other week.** Two-hour activities start at even hours (08, 10, …), so 08-10 is
  a block. A two-hour activity that runs every other week is written in one row of its block, alternating
  with what's in the other row.
  - **What counts as alternating:** a one-hour cell whose block's other row has another one-hour cell in
    the same column, or in the same room for another subgroup.
  - **How it's read:** two hours, in the top row's parity (odd weeks by default) or the bottom row's (the
    other parity), each such cell listed in the warnings. A cell whose text states a parity keeps it.
  - `topRowWeeks: even` flips a sheet; `alternate: false` reads its one-hour cells as written; `cells` sets
    any one cell.
  - A one-hour cell with nothing in its block's other row is read as written (one hour, every week), with
    a warning.
- **Abbreviations** (`LSV (l)`) resolve to a course of the same programme on the same sheet:
  1. an alias from the overrides;
  2. a course that names its own abbreviation (`Programare Kotlin - PK`);
  3. initials (`LSV` = *Logica senzorilor virtuali*; acronym words count whole; a trailing number may be
     left out: `TN` = *Teoria nodurilor 1*);
  4. a beginning (`OPT` = *Optica*);
  5. a guess, which warns: the only course with those letters in order (`HS` = *Hidrostatica*).

  A course name with a typo or two of another is read as that one (warned). Names that differ only in a
  number or a numeral like II (*Proiect integrator 1* and *2*) are different subjects.
- **Columns beside the grid** hold offered subjects, each with the times it's offered at, written out:
  `ITSR (l) [R+E] sala R521 Marti 10-12, Joi 8-10`. `[R]`/`[E]` tags say which specialisations.
  - An optional or facultative subject with no time written is placed at its grid position (warned).
  - Columns not given a role hold notes.
- **Optional courses grouped differently from the timetable groups:** the sheet's `optionalSubjects`
  makes their classes on the grid offers to the whole year, whatever column they sit in.
- **Everything else** (sport, consultation hours, footnotes, rotas per subgroup written out) becomes a
  note of its programme and year.

## The overrides file

YAML. Unknown keys are refused, so a typo fails the run instead of being ignored. An example (all
values invented):

```yaml
semester: { academicYear: "2030-2031", number: 1 }

# Week 1 starts on a Monday; each following Monday starts the next week, except whole weeks
# (Monday-Friday) inside a break. A break covering part of a week makes those days non-teaching.
calendar:
  firstWeekStartsOn: "2030-09-30"
  weeks: 14
  breaks: [ { from: "2030-12-23", to: "2031-01-05" } ]
  nonTeachingDays: [ { date: "2030-12-02", reason: "Public holiday" } ]

programmes:                   # code -> name (codes are ours)
  ALFA: "Programme Alfa"
  MDATA: "Master MDATA"

sources:                      # workbook id -> the sheets it holds (how a workbook is recognised)
  main: { sheets: ["An 1", "An 1 MDATA"] }

sheets:                       # by sheet name; a sheet no source lists, or not listed here, is skipped
  "An 1":
    year: 1
    columns:                  # group-header label as written -> programme (and series)
      "Seria 1 (X)": { programme: ALFA, series: X }
      "Seria 2 (Y)": { programme: ALFA, series: Y }
    columnRoles:              # column letter -> optional | facultative | pickSlot | notes | grid
      Q: facultative
    tags: { R: ROB, E: SUB }  # an optional lab's [R]/[E] -> programme
    aliases: { GEO: "Geometria fractalilor" }
    optionalSubjects: ["Vizualizare stiintifica"]   # its grid classes are offers to the whole year
    topRowWeeks: odd          # (default) a one-hour alternating cell in the top row: odd weeks
    alternate: true           # (default) false reads one-hour cells as written
  "An 1 MDATA": { year: 1, programme: MDATA, columnRoles: { E: pickSlot } }   # one group
  "Legend": { skip: true }

cells:                        # by the cell's top-left "Sheet!A1"
  "An 1!K33": { text: "Optica", end: "10:00", weeks: even }   # text: the cell must start with it, or this is ignored
  "An 1!C29": { text: "Teh.avans.", kind: course }            # a class written without its mark
  # also: skip, asNote, subject, start, room, teacher, note
rooms:
  K210: { building: "North", floor: "2", capacity: 120 }
```

An override whose `text` doesn't match the cell is ignored with a warning (the address moved in a newer
file). Cells, aliases and optional subjects that match nothing, and sheets no source lists, are warned.

Column roles:
- **optional:** optional subjects, each offered at several times. A student opts in and picks one.
- **facultative:** the same, marked facultative.
- **pickSlot:** labs everyone has, at one of several times (master sheets). The course on the grid stays
  for everyone.
- **notes:** text shown with the timetable.
- **grid:** a column of the grid. A sheet without group columns uses this for its own.
