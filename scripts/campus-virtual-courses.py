#!/usr/bin/env python3
"""Link the timetable's subjects to their Campus Virtual (UPT's Moodle) courses (CampusPulse-ro/api#45).

Once per semester, after the timetable files are in:

1. Export the subjects the import reads (any machine that runs the API's build; no database):
       CampusPulse.Api import-timetable --overrides overrides.yaml --parse-only \\
           --subjects-json subjects.json WORKBOOK.xlsx [WORKBOOK.xlsx ...]
2. Match them to the catalog and rewrite the overrides' campusVirtual section:
       python3 scripts/campus-virtual-courses.py --subjects subjects.json --overrides overrides.yaml \\
           [--env-file FILE] [--dry-run]
   The export records the overrides' SHA-256: if the file changed since, the script refuses (export again),
   so a stale export can't undo an edit.
3. Import as usual: each subject gets its course id and the timetable endpoints a campusVirtualUrl.

The catalog is searched with the read-only core_course_search_courses, for the shortname prefixes in the
overrides' campusVirtual.search. A Moodle shortname names the programme and year (PREFIX-<CODE><YEAR>-...).
A subject is matched among the courses of its programme's codes (campusVirtual.codes, by default the programme
code without punctuation) and year:
- by its name (1.0; case, diacritics, a note in brackets and a series suffix ignored);
- by an abbreviation equal to the course's (0.95): the subject is named by it, or the sheet uses it (3+ letters);
- by abbreviated words (0.9: "Secu. aplic. mobile"), by all of the words of one name in the other (0.85), or by
  spelling (0.8 and up), but only when each of the subject's words is in the course's, maybe misspelt.
A name with a number (1, II) never matches a course with another number. When the best course is placed by
abbreviation only and another one fits by name, the subject is ambiguous. One it can't place among its own
courses gets another programme's course of exactly its name, of the same level (search prefix) and year or of
no year (a course shared between programmes). Subjects with two equally good courses (one per series, say)
are ambiguous: left out and listed, as are the unmatched ones; every match under 1.0 is listed with its score.

An entry marked `hand: true` in the overrides is kept as it is, so a hand-made link survives the next run; a
hand entry without an id says the subject has no course. Comments inside the section are kept: one above an
entry stays above it, others move to the section's top. The file is written atomically, the old one kept as
FILE.bak.

The token is any student's moodle_mobile_app token: MOODLE_TOKEN from the environment, or else from --env-file
(KEY=VALUE lines). It goes only into the POST body of an https request to cv.upt.ro (MOODLE_URL, if set, must
be that host), and is never printed or written anywhere.

Exit codes: 0 done, 1 failed (catalog, token or files), 64 usage. Standard library only.
"""

import argparse
import datetime
import difflib
import hashlib
import html
import json
import os
import re
import shutil
import sys
import tempfile
import unicodedata
import urllib.error
import urllib.parse
import urllib.request

HOST = "cv.upt.ro"  # the only Moodle the token is sent to (the API links there too: CampusVirtual.cs)
MATCH = 0.8  # the lowest score that places a subject on a course of its programme and year
TIE = 0.05  # two courses scoring within this of the best are equally good: ambiguous
NAME, ABBREVIATION, ABBREVIATED_WORDS, CONTAINED = 1.0, 0.95, 0.9, 0.85
PER_PAGE = 100
STOP_WORDS = {"si", "de", "a", "in", "and", "of", "the", "for", "cu", "la", "pe", "din", "to", "al", "ale", "e", "o"}
ROMAN = {"i": 1, "ii": 2, "iii": 3, "iv": 4, "v": 5, "vi": 6, "vii": 7, "viii": 8, "ix": 9, "x": 10}
PREFIX = re.compile(r"[A-Za-z0-9]+(-[A-Za-z0-9]+)*")
BEGIN = "# --- campusVirtual"
END = "# --- end campusVirtual"


class Failure(Exception):
    """A problem that ends the run with exit code 1; its message never contains the token."""


# --- names


def normalize(text):
    """Lower case, no diacritics or punctuation, without a parenthesised note or a series suffix, and a roman
    numeral after the first word as digits ("Fizica II" -> "fizica 2")."""
    text = html.unescape(text or "")
    text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode().lower()
    text = re.sub(r"\(.*?\)", " ", text)
    text = re.sub(r"\b(seria|series)\b.*$", " ", text)
    text = re.sub(r"\bsemestrul\s*\d+\b", " ", text)
    found = re.sub(r"[^a-z0-9]+", " ", text).split()
    return " ".join(str(ROMAN[w]) if i > 0 and w in ROMAN else w for i, w in enumerate(found))


def words(text):
    """The normalized words without the stop words, in order."""
    return [w for w in normalize(text).split() if w not in STOP_WORDS]


def numbers(text):
    """The numbers a name carries as words, digits or roman numerals ("Fizica II" -> {2})."""
    return {int(w) for w in normalize(text).split() if w.isdigit()}


def letters(text):
    return re.sub(r"[^A-Za-z]", "", unicodedata.normalize("NFKD", text or "").encode("ascii", "ignore").decode()).upper()


def abbreviation(shortname):
    """A shortname's last part without a trailing number: PREFIX-ALFA1-O-GF2 -> GF."""
    return re.sub(r"\d+$", "", shortname.upper().split("-")[-1])


def abbreviated_words(ours, theirs):
    """Each of our words starts its word of theirs, in order, one for one ("secu aplic mobile")."""
    return len(ours) == len(theirs) and all(len(o) >= 2 and t.startswith(o) for o, t in zip(ours, theirs))


def close_word(ours, theirs):
    """The same word, an abbreviation of it, or a typo of it ("reasearch")."""
    return ours == theirs or (len(ours) >= 2 and theirs.startswith(ours)) or difflib.SequenceMatcher(None, ours, theirs).ratio() >= 0.8


def score(subject, course):
    """(how well the course fits the subject, from 0 to 1; whether that rests on the abbreviation alone)."""
    name, full = normalize(subject["name"]), normalize(course["fullname"])
    if not name:
        return 0.0, False
    ours_n, theirs_n = numbers(subject["name"]), numbers(course["fullname"])
    if ours_n and theirs_n and ours_n != theirs_n:
        return 0.0, False  # Analiza matematica 1 is not Analiza matematica 2
    if name == full:
        return NAME, False
    abbr = abbreviation(course["shortname"])
    named_by_it = " " not in name and len(abbr) >= 2 and letters(subject["name"]) == abbr
    sheet_uses_it = len(abbr) >= 3 and letters(subject.get("shortName")) == abbr
    ours, theirs = words(subject["name"]), words(course["fullname"])
    # Spelling closeness counts only when every word of ours is in theirs, maybe abbreviated or misspelt:
    # "Programare ..." is not "Proiectare ...", however alike the strings are.
    best = difflib.SequenceMatcher(None, name, full).ratio() if all(any(close_word(o, t) for t in theirs) for o in ours) else 0.0
    if ours and theirs:
        best = max(best, len(set(ours) & set(theirs)) / len(set(ours) | set(theirs)))
        if set(ours) <= set(theirs) or set(theirs) <= set(ours):
            best = max(best, CONTAINED)
        if abbreviated_words(ours, theirs):
            best = max(best, ABBREVIATED_WORDS)
    if (named_by_it or sheet_uses_it) and best < ABBREVIATION:
        return ABBREVIATION, best < MATCH
    return best, False


# --- the catalog


def moodle_call(url, token):
    """A function that calls one web-service function of Campus Virtual with the token (in the POST body)."""
    parts = urllib.parse.urlsplit(url)
    if parts.scheme != "https" or parts.hostname != HOST or parts.port not in (None, 443) or parts.username or parts.password:
        raise Failure(f"MOODLE_URL must be https://{HOST}: the token goes nowhere else (got {parts.scheme}://{parts.hostname})")
    endpoint = f"https://{HOST}/webservice/rest/server.php"

    def call(function, **params):
        body = urllib.parse.urlencode({"wstoken": token, "wsfunction": function, "moodlewsrestformat": "json", **params}).encode()
        try:
            with urllib.request.urlopen(urllib.request.Request(endpoint, data=body), timeout=60) as response:
                return json.load(response)
        except (urllib.error.URLError, TimeoutError, ValueError) as e:
            problem = "not JSON" if isinstance(e, ValueError) else str(e).replace(token, "[token]")
            raise Failure(f"{function} at {endpoint}: {problem}") from None

    return call


def fetch_catalog(prefixes, call):
    """Every course whose shortname starts with one of the prefixes (core_course_search_courses), by id."""
    courses = {}
    for prefix in prefixes:
        if not PREFIX.fullmatch(prefix):
            raise Failure(f"campusVirtual.search: {prefix!r} isn't a shortname prefix like S1-X-FAC")
        page = seen = 0
        while True:
            result = call("core_course_search_courses", criterianame="search", criteriavalue=prefix, page=page, perpage=PER_PAGE)
            if not isinstance(result, dict):
                raise Failure("core_course_search_courses: an unexpected reply")
            if "exception" in result:
                raise Failure(f"core_course_search_courses: {result.get('errorcode', '?')}: {result.get('message', '')}")
            found = result.get("courses", [])
            for course in found:
                if course.get("shortname", "").upper().startswith(prefix.upper() + "-"):
                    courses.setdefault(course["id"], {
                        "id": course["id"],
                        "shortname": course["shortname"],
                        "fullname": html.unescape(course.get("fullname", "")),
                        "prefix": prefix.upper(),
                        "code": programme_year_code(course["shortname"], prefix),
                    })
            page, seen = page + 1, seen + len(found)
            if not found or seen >= result.get("total", 0):
                break
    return list(courses.values())


def programme_year_code(shortname, prefix):
    """The programme-and-year part after the prefix (PREFIX-ALFA1-GF -> ALFA1); None for a course of no one programme."""
    parts = shortname.upper().split("-")
    at = len(prefix.split("-"))
    if len(parts) > at + 1 and re.fullmatch(r"[A-Z]+[1-6]", parts[at]):
        return parts[at]
    return None


# --- matching


def codes_of(programme, codes):
    return [c.upper() for c in codes.get(programme) or [re.sub(r"[^A-Za-z0-9]", "", programme)]]


def match(subject, catalog, codes):
    """('matched', course, score), ('ambiguous', [courses], None) or ('unmatched', None, None)."""
    own_codes = codes_of(subject["programme"], codes)
    wanted = {f"{code}{subject['year']}" for code in own_codes}
    scored = sorted(((*score(subject, c), c) for c in catalog if c["code"] in wanted), key=lambda x: -x[0])
    if scored and scored[0][0] >= MATCH:
        best, by_abbreviation_only, _ = scored[0]
        # Placed by abbreviation alone, against another course that fits by name: no telling which.
        margin = best - MATCH + 1e-9 if by_abbreviation_only else TIE
        tied = [c for s, _, c in scored if best - s < margin]
        return ("matched", tied[0], best) if len(tied) == 1 else ("ambiguous", tied, None)
    # Another programme's course: exactly the name, the same level, and the same year or none.
    levels = {c["prefix"] for c in catalog if c["code"] and re.sub(r"\d$", "", c["code"]) in own_codes}
    name = normalize(subject["name"])
    same = [c for c in catalog if name and normalize(c["fullname"]) == name
            and (not levels or c["prefix"] in levels) and (c["code"] is None or c["code"][-1] == str(subject["year"]))]
    if len(same) == 1:
        return "matched", same[0], NAME
    return ("ambiguous", same, None) if same else ("unmatched", None, None)


def name_key(name):
    """The import's CellText.NameKey, in Python: no diacritics, lower case, dots as spaces, single spaces. Pairs
    the file's entries with the subjects as the import does."""
    decomposed = unicodedata.normalize("NFD", name)
    plain = unicodedata.normalize("NFC", "".join(c for c in decomposed if unicodedata.category(c) != "Mn"))
    return " ".join(plain.lower().replace(".", " ").split())


def subject_key(subject):
    return subject["programme"], int(subject["year"]), name_key(subject["name"])


def link(subjects, catalog, codes, existing):
    """The new courses section (programme -> year -> name -> entry) and what happened to each subject."""
    hand = {}
    for programme, years in existing.items():
        for year, names in years.items():
            for name, entry in names.items():
                if entry.get("hand"):
                    hand[(programme, int(year), name_key(name))] = (name, entry)
    courses, report = {}, {"matched": [], "elsewhere": [], "ambiguous": [], "unmatched": [], "hand": []}
    seen = set()
    for subject in subjects:
        key = subject_key(subject)
        if key in seen:
            continue  # the same subject in a second workbook: one entry, as the import keeps one subject
        seen.add(key)
        if key in hand:
            name, entry = hand.pop(key)
            courses.setdefault(key[0], {}).setdefault(key[1], {})[name] = entry
            report["hand"].append((subject, entry, None))
            continue
        status, found, how_well = match(subject, catalog, codes)
        if status == "matched":
            courses.setdefault(key[0], {}).setdefault(key[1], {})[subject["name"]] = {
                "id": found["id"], "shortname": found["shortname"]}
            own = found["code"] in {f"{c}{key[1]}" for c in codes_of(key[0], codes)}
            report["matched" if own else "elsewhere"].append((subject, found, how_well))
        else:
            report[status].append((subject, found, None))
    # Hand entries for subjects the files no longer have stay too: the import warns about them.
    for (programme, year, _), (name, entry) in hand.items():
        courses.setdefault(programme, {}).setdefault(year, {})[name] = entry
    return courses, report


# --- the overrides file


def q(text):
    """A YAML double-quoted scalar (JSON's escapes are YAML's)."""
    return json.dumps(text, ensure_ascii=False)


def unquote(text):
    text = text.strip()
    if text.startswith('"'):
        try:
            return json.loads(text)
        except ValueError:
            return text.strip('"')
    return text.strip("'")


ENTRY = re.compile(r'\s+("(?:[^"\\]|\\.)*"|[^\s"#][^:#]*?)\s*:\s*\{')
PROGRAMME = re.compile(r'    ("(?:[^"\\]|\\.)*"|[^\s"#][^:#]*?)\s*:\s*$')
YEAR = re.compile(r"      (\d+)\s*:\s*$")


def section_comments(block):
    """The comments inside an old section: {(programme, year, name key): [comments above that entry],
    None: [the others]}. Entries are recognised in the layout render_section writes."""
    comments, pending, programme, year = {None: []}, [], None, None
    for line in block:
        stripped = line.strip()
        if stripped.startswith(BEGIN) or stripped.startswith(END):
            continue
        if stripped.startswith("#"):
            pending.append(stripped)
            continue
        if not stripped:
            continue
        if m := PROGRAMME.fullmatch(line.rstrip("\n")):
            programme, year = unquote(m.group(1)), None
        elif m := YEAR.fullmatch(line.rstrip("\n")):
            year = int(m.group(1))
        elif (m := ENTRY.match(line)) and programme is not None and year is not None and line.startswith("        "):
            comments.setdefault((programme, year, name_key(unquote(m.group(1)))), []).extend(pending)
            pending = []
            continue
        comments[None].extend(pending)
        pending = []
    comments[None].extend(pending)
    return comments


def render_section(search, codes, courses, today, comments=None):
    comments = {k: list(v) for k, v in (comments or {None: []}).items()}
    body = []
    for programme in sorted(courses):
        body.append(f"    {q(programme)}:")
        for year in sorted(courses[programme], key=int):
            body.append(f"      {int(year)}:")
            for name in sorted(courses[programme][year], key=str.casefold):
                entry = courses[programme][year][name]
                body.extend(f"        {c}" for c in comments.pop((programme, int(year), name_key(name)), []))
                fields = []
                if entry.get("id") is not None:
                    fields.append(f"id: {int(entry['id'])}")
                if entry.get("shortname"):
                    fields.append(f"shortname: {q(entry['shortname'])}")
                if entry.get("hand"):
                    fields.append("hand: true")
                body.append(f"        {q(name)}: {{ {', '.join(fields)} }}")
    # Comments of entries that are gone, and those not above an entry, go to the top.
    top = comments.pop(None, []) + [c for rest in comments.values() for c in rest]
    lines = [
        f"{BEGIN}: written by scripts/campus-virtual-courses.py on {today}; entries marked hand: true survive its next run",
        "campusVirtual:",
        *(f"  {c}" for c in top),
        f"  search: [{', '.join(q(p) for p in search)}]",
        "  codes: {" + ", ".join(f"{q(p)}: [{', '.join(q(c) for c in cs)}]" for p, cs in sorted(codes.items())) + "}",
        *(["  courses:", *body] if body else ["  courses: {}"]),
        END,
    ]
    return "\n".join(lines) + "\n"


def find_section(lines):
    """(first line, line after it) of the campusVirtual section, or None. The section runs from its key to the
    next top-level key; blank and comment lines at its end belong to what follows, unless the end marker comes
    after them."""
    starts = [i for i, line in enumerate(lines) if re.match(r"campusVirtual\s*:", line)]
    if len(starts) > 1:
        raise Failure("the overrides have campusVirtual twice")
    if not starts:
        return None
    end = starts[0] + 1
    while end < len(lines) and (not lines[end].strip() or lines[end][0] in " \t#"):
        end += 1
    while end > starts[0] + 1 and (not lines[end - 1].strip() or lines[end - 1].startswith("#")) \
            and not lines[end - 1].startswith(END):
        end -= 1
    return starts[0], end


def rewrite(text, render):
    """The overrides text with its campusVirtual section replaced by render(comments of the old one), or that
    appended when there is none. Marker lines left anywhere else are dropped; every other line is kept."""
    lines = text.splitlines(keepends=True)
    if lines and not lines[-1].endswith("\n"):
        lines[-1] += "\n"
    found = find_section(lines)
    block = lines[found[0]:found[1]] if found else []
    section = render(section_comments(block))

    def keep(i):
        return not (lines[i].startswith(BEGIN) or lines[i].startswith(END))

    if found is None:
        kept = [line for i, line in enumerate(lines) if keep(i)]
        return "".join(kept) + ("\n" if kept else "") + section
    before = [line for i, line in enumerate(lines[:found[0]]) if keep(i)]
    after = [line for i, line in enumerate(lines[found[1]:], start=found[1]) if keep(i)]
    return "".join(before) + section + "".join(after)


def flatten(courses):
    return {(p, int(y), name_key(n)): (n, e.get("id"), bool(e.get("hand")))
            for p, years in courses.items() for y, names in years.items() for n, e in names.items()}


def write_atomically(path, text):
    """Replaces the file in one step (a crash leaves the old or the new one, never half), keeping its mode,
    with the old file as path.bak."""
    folder = os.path.dirname(os.path.abspath(path))
    mode = os.stat(path).st_mode & 0o7777
    shutil.copy2(path, path + ".bak")
    fd, temporary = tempfile.mkstemp(dir=folder, prefix=".campus-virtual-", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="") as f:
            f.write(text)
            f.flush()
            os.fsync(f.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    except BaseException:
        if os.path.exists(temporary):
            os.unlink(temporary)
        raise


# --- the run


def read_env_file(path):
    values = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.removeprefix("export ").split("=", 1)
            values[key.strip()] = value.strip().strip("'\"")
    return values


def credentials(env_file):
    file_values = read_env_file(env_file) if env_file else {}
    token = os.environ.get("MOODLE_TOKEN") or file_values.get("MOODLE_TOKEN")
    url = os.environ.get("MOODLE_URL") or file_values.get("MOODLE_URL") or f"https://{HOST}"
    if not token:
        raise Failure("no MOODLE_TOKEN in the environment" + (f" or in {env_file}" if env_file else " (or give --env-file)"))
    return url, token


def shown(entry):
    """An entry of flatten() as the summary prints it: its course id (or none), and whether it's by hand."""
    _, course_id, by_hand = entry
    return f"{course_id if course_id is not None else 'none'}{' (hand)' if by_hand else ''}"


def label(subject):
    return f"{subject['programme']} y{subject['year']} {subject['name']}"


def main(argv=None, call=None, out=sys.stdout, today=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--subjects", required=True, help="the JSON import-timetable --subjects-json wrote")
    parser.add_argument("--overrides", required=True, help="the private overrides file whose campusVirtual section is rewritten")
    parser.add_argument("--env-file", help="KEY=VALUE file with MOODLE_TOKEN, if not in the environment")
    parser.add_argument("--dry-run", action="store_true", help="print what would change; write nothing")
    try:
        args = parser.parse_args(argv)
    except SystemExit as e:
        return 0 if e.code == 0 else 64
    try:
        with open(args.subjects, encoding="utf-8-sig") as f:
            export = json.load(f)
        with open(args.overrides, "rb") as f:
            raw = f.read()
        # Hand entries, search and codes come from the export: it must be of this very file.
        if export.get("overridesSha256") != hashlib.sha256(raw).hexdigest():
            raise Failure(f"{args.subjects} was exported from another version of {args.overrides}: export again "
                          "(import-timetable --parse-only --subjects-json) so no edit is lost")
        text = raw.decode("utf-8-sig")
        search, codes = export.get("search") or [], export.get("codes") or {}
        if not search:
            raise Failure("the overrides' campusVirtual.search lists no shortname prefix to search the catalog for")
        if call is None:
            call = moodle_call(*credentials(args.env_file))
        catalog = fetch_catalog(search, call)
        existing = export.get("courses") or {}  # the file's entries: same SHA-256
        courses, report = link(export.get("subjects", []), catalog, codes, existing)
        day = today or datetime.date.today().isoformat()
        updated = rewrite(text, lambda comments: render_section(search, codes, courses, day, comments))
    except (OSError, ValueError, KeyError, Failure) as e:
        print(f"campus-virtual-courses failed: {e}", file=sys.stderr)
        return 1

    subjects = sum(len(v) for v in report.values())
    print(f"Catalog: {len(catalog)} courses under {', '.join(search)}.", file=out)
    print(f"Subjects: {subjects}: matched {len(report['matched']) + len(report['elsewhere'])} "
          f"({len(report['elsewhere'])} to another programme's course of the same name), ambiguous {len(report['ambiguous'])}, "
          f"unmatched {len(report['unmatched'])}, kept by hand {len(report['hand'])}.", file=out)
    below = sorted((r for r in report["matched"] if r[2] < NAME), key=lambda r: (r[2], label(r[0])))
    if below:
        print("Matched under 1.0 (check them):", file=out)
        for subject, course, how_well in below:
            print(f"  {how_well:.2f} {label(subject)}: {course['shortname']} {course['fullname']}", file=out)
    if report["elsewhere"]:
        print("Matched to another programme's course:", file=out)
        for subject, course, _ in report["elsewhere"]:
            print(f"  {label(subject)}: {course['shortname']}", file=out)
    if report["ambiguous"]:
        print("Ambiguous (left out; a hand: true entry picks one):", file=out)
        for subject, found, _ in report["ambiguous"]:
            print(f"  {label(subject)}: " + ", ".join(f"{c['shortname']} (id {c['id']})" for c in found), file=out)
    if report["unmatched"]:
        print("Unmatched:", file=out)
        for subject, _, _ in report["unmatched"]:
            print(f"  {label(subject)}", file=out)

    before, after = flatten(existing), flatten(courses)
    added, removed = sorted(after.keys() - before.keys()), sorted(before.keys() - after.keys())
    changed = sorted(k for k in before.keys() & after.keys() if before[k][1:] != after[k][1:])
    print(f"Against the overrides: +{len(added)} -{len(removed)}, {len(changed)} changed.", file=out)
    for k in removed:
        print(f"  - {k[0]} y{k[1]} {before[k][0]}: {shown(before[k])}", file=out)
    for k in changed:
        print(f"  ~ {k[0]} y{k[1]} {after[k][0]}: {shown(before[k])} -> {shown(after[k])}", file=out)
    if args.dry_run:
        print(f"Dry run: {args.overrides} not changed.", file=out)
        return 0
    try:
        write_atomically(args.overrides, updated)
    except OSError as e:
        print(f"campus-virtual-courses failed: {e}", file=sys.stderr)
        return 1
    print(f"Wrote the campusVirtual section of {args.overrides} (the old file: {args.overrides}.bak).", file=out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
