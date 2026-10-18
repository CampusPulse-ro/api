#!/usr/bin/env python3
"""Link the timetable's subjects to their Campus Virtual (UPT's Moodle) courses (CampusPulse-ro/api#45).

Once per semester, after the timetable files are in:

1. Export the subjects the import reads (any machine that runs the API's build; no database):
       CampusPulse.Api import-timetable --overrides overrides.yaml --parse-only \\
           --subjects-json subjects.json WORKBOOK.xlsx [WORKBOOK.xlsx ...]
2. Match them to the catalog and rewrite the overrides' campusVirtual section:
       python3 scripts/campus-virtual-courses.py --subjects subjects.json --overrides overrides.yaml \\
           [--env-file FILE] [--dry-run]
3. Import as usual: each subject gets its course id and the timetable endpoints a campusVirtualUrl.

The catalog is searched with the read-only core_course_search_courses, for the shortname prefixes in the
overrides' campusVirtual.search. A Moodle shortname names the programme and year (PREFIX-<CODE><YEAR>-...):
a subject is matched among the courses of its programme's codes (campusVirtual.codes, by default the programme
code without punctuation) and year, by name, by its abbreviation or by initials. One it can't place there gets
a course of another programme only under exactly its name (a course shared between programmes). Subjects with
two equally good courses (one per series, say) are ambiguous: left out and listed, as are the unmatched ones.
An entry marked `hand: true` in the overrides is kept as it is, so a hand-made link survives the next run; a
hand entry without an id says the subject has no course.

The token is any student's moodle_mobile_app token: MOODLE_TOKEN (and MOODLE_URL, default https://cv.upt.ro)
from the environment, or else from --env-file (KEY=VALUE lines). It goes only into the POST body of an https
request to MOODLE_URL, and is never printed or written anywhere.

Exit codes: 0 done, 1 failed (catalog, token or files), 64 usage. Standard library only.
"""

import argparse
import datetime
import difflib
import html
import json
import os
import re
import sys
import unicodedata
import urllib.error
import urllib.parse
import urllib.request

MATCH = 0.8  # the lowest score that places a subject on a course of its programme and year
TIE = 0.05  # two courses scoring within this of the best are equally good: ambiguous
PER_PAGE = 100
STOP_WORDS = {"si", "de", "a", "in", "and", "of", "the", "for", "cu", "la", "pe", "din", "to", "al", "ale", "e", "o"}
BEGIN = "# --- campusVirtual"
END = "# --- end campusVirtual"


class Failure(Exception):
    """A problem that ends the run with exit code 1; its message never contains the token."""


# --- names


def normalize(text):
    """Lower case, no diacritics or punctuation, without a parenthesised note or a series suffix."""
    text = html.unescape(text or "")
    text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode().lower()
    text = re.sub(r"\(.*?\)", " ", text)
    text = re.sub(r"\b(seria|series)\b.*$", " ", text)
    text = re.sub(r"\bsemestrul\s*\d+\b", " ", text)
    return " ".join(re.sub(r"[^a-z0-9]+", " ", text).split())


def tokens(text):
    return set(normalize(text).split()) - STOP_WORDS


def initials(text):
    return "".join(word[0] for word in normalize(text).split() if word not in STOP_WORDS).upper()


def letters(text):
    return re.sub(r"[^A-Za-z]", "", unicodedata.normalize("NFKD", text or "").encode("ascii", "ignore").decode()).upper()


def abbreviation(shortname):
    """A shortname's last part without a trailing number: PREFIX-ALFA1-O-GF2 -> GF."""
    return re.sub(r"\d+$", "", shortname.upper().split("-")[-1])


def score(subject, course):
    """How well the course fits the subject: 1 for the same name or abbreviation, down to 0."""
    name, full = normalize(subject["name"]), normalize(course["fullname"])
    abbr = abbreviation(course["shortname"])
    if not name:
        return 0.0
    if name == full or (len(abbr) >= 2 and abbr in (letters(subject["name"]), letters(subject.get("shortName")))):
        return 1.0
    ours, theirs = tokens(subject["name"]), tokens(course["fullname"])
    best = difflib.SequenceMatcher(None, name, full).ratio()
    if ours and theirs:
        best = max(best, len(ours & theirs) / len(ours | theirs))
        if ours <= theirs or theirs <= ours:
            best = max(best, 0.85)
    if len(abbr) >= 2 and initials(subject["name"]) == abbr:
        best = max(best, 0.9)
    return best


# --- the catalog


def moodle_call(url, token):
    """A function that calls one web-service function of the Moodle at url with token (in the POST body)."""
    if not url.startswith("https://"):
        raise Failure(f"MOODLE_URL must be https (the token would travel in clear): {url}")
    endpoint = url.rstrip("/") + "/webservice/rest/server.php"

    def call(function, **params):
        body = urllib.parse.urlencode({"wstoken": token, "wsfunction": function, "moodlewsrestformat": "json", **params}).encode()
        try:
            with urllib.request.urlopen(urllib.request.Request(endpoint, data=body), timeout=60) as response:
                return json.load(response)
        except (urllib.error.URLError, TimeoutError, ValueError) as e:
            raise Failure(f"{function} at {endpoint}: {e}") from None

    return call


def fetch_catalog(prefixes, call):
    """Every course whose shortname starts with one of the prefixes (core_course_search_courses), by id."""
    courses = {}
    for prefix in prefixes:
        page = seen = 0
        while True:
            result = call("core_course_search_courses", criterianame="search", criteriavalue=prefix, page=page, perpage=PER_PAGE)
            if not isinstance(result, dict):
                raise Failure("core_course_search_courses: an unexpected reply")
            if "exception" in result:
                raise Failure(f"core_course_search_courses: {result.get('errorcode', '?')}: {result.get('message', '')}")
            found = result.get("courses", [])
            for course in found:
                if course.get("shortname", "").upper().startswith(prefix.upper()):
                    courses[course["id"]] = {
                        "id": course["id"],
                        "shortname": course["shortname"],
                        "fullname": html.unescape(course.get("fullname", "")),
                        "code": programme_year_code(course["shortname"], prefix),
                    }
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
    return codes.get(programme) or [re.sub(r"[^A-Za-z0-9]", "", programme).upper()]


def match(subject, catalog, codes):
    """('matched', course), ('ambiguous', [courses]) or ('unmatched', None)."""
    wanted = {f"{code.upper()}{subject['year']}" for code in codes_of(subject["programme"], codes)}
    own = [c for c in catalog if c["code"] in wanted]
    scored = sorted(((score(subject, c), c) for c in own), key=lambda x: -x[0])
    if scored and scored[0][0] >= MATCH:
        tied = [c for s, c in scored if scored[0][0] - s < TIE]
        return ("matched", tied[0]) if len(tied) == 1 else ("ambiguous", tied)
    name = normalize(subject["name"])
    same = [c for c in catalog if name and normalize(c["fullname"]) == name]
    if len(same) == 1:
        return "matched", same[0]
    return ("ambiguous", same) if same else ("unmatched", None)


def name_key(name):
    """How the import compares names (CellText.NameKey): case, diacritics, dots and spacing ignored."""
    return " ".join(unicodedata.normalize("NFKD", name).encode("ascii", "ignore").decode().lower().replace(".", " ").split())


def link(subjects, catalog, codes, existing):
    """The new courses section (programme -> year -> name -> entry) and what happened to each subject."""
    hand = {}
    for programme, years in existing.items():
        for year, names in years.items():
            for name, entry in names.items():
                if entry.get("hand"):
                    hand[(programme, int(year), name_key(name))] = (name, entry)
    courses, report = {}, {"matched": [], "elsewhere": [], "ambiguous": [], "unmatched": [], "hand": []}
    for subject in subjects:
        key = (subject["programme"], subject["year"], name_key(subject["name"]))
        if key in hand:
            name, entry = hand.pop(key)
            courses.setdefault(subject["programme"], {}).setdefault(subject["year"], {})[name] = entry
            report["hand"].append((subject, entry))
            continue
        status, found = match(subject, catalog, codes)
        if status == "matched":
            courses.setdefault(subject["programme"], {}).setdefault(subject["year"], {})[subject["name"]] = {
                "id": found["id"], "shortname": found["shortname"]}
            own = found["code"] in {f"{c.upper()}{subject['year']}" for c in codes_of(subject["programme"], codes)}
            report["matched" if own else "elsewhere"].append((subject, found))
        else:
            report[status].append((subject, found))
    # Hand entries for subjects the files no longer have stay too: the import warns about them.
    for (programme, year, _), (name, entry) in hand.items():
        courses.setdefault(programme, {}).setdefault(year, {})[name] = entry
    return courses, report


# --- the overrides file


def q(text):
    """A YAML double-quoted scalar (JSON's escapes are YAML's)."""
    return json.dumps(text, ensure_ascii=False)


def render_section(search, codes, courses, today):
    lines = [
        f"{BEGIN}: written by scripts/campus-virtual-courses.py on {today}; entries marked hand: true survive its next run",
        "campusVirtual:",
        f"  search: [{', '.join(q(p) for p in search)}]",
        "  codes: {" + ", ".join(f"{q(p)}: [{', '.join(q(c) for c in cs)}]" for p, cs in sorted(codes.items())) + "}",
    ]
    if not courses:
        lines.append("  courses: {}")
    else:
        lines.append("  courses:")
        for programme in sorted(courses):
            lines.append(f"    {q(programme)}:")
            for year in sorted(courses[programme], key=int):
                lines.append(f"      {int(year)}:")
                for name in sorted(courses[programme][year], key=str.casefold):
                    entry = courses[programme][year][name]
                    fields = []
                    if entry.get("id") is not None:
                        fields.append(f"id: {int(entry['id'])}")
                    if entry.get("shortname"):
                        fields.append(f"shortname: {q(entry['shortname'])}")
                    if entry.get("hand"):
                        fields.append("hand: true")
                    lines.append(f"        {q(name)}: {{ {', '.join(fields)} }}")
    lines.append(END)
    return "\n".join(lines) + "\n"


def replace_section(text, section):
    """The overrides text with its campusVirtual section (and the marker comments around it) replaced, or the
    section appended when there is none. Everything else, comments included, is kept as it is."""
    lines = text.splitlines(keepends=True)
    starts = [i for i, line in enumerate(lines) if re.match(r"campusVirtual\s*:", line)]
    if len(starts) > 1:
        raise Failure("the overrides have campusVirtual twice")
    if not starts:
        if text and not text.endswith("\n"):
            text += "\n"
        return text + ("\n" if text else "") + section
    begin = end = starts[0]
    if begin > 0 and lines[begin - 1].startswith(BEGIN):
        begin -= 1
    end += 1
    while end < len(lines) and (not lines[end].strip() or lines[end][0] in " \t#"):
        end += 1
    # Blank and comment lines at its end belong to what follows, except the end marker.
    while end > starts[0] + 1 and (not lines[end - 1].strip() or lines[end - 1].startswith("#")) \
            and not lines[end - 1].startswith(END):
        end -= 1
    return "".join(lines[:begin]) + section + "".join(lines[end:])


def flatten(courses):
    return {(p, int(y), name_key(n)): (e.get("id"), bool(e.get("hand")))
            for p, years in courses.items() for y, names in years.items() for n, e in names.items()}


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
    url = os.environ.get("MOODLE_URL") or file_values.get("MOODLE_URL") or "https://cv.upt.ro"
    if not token:
        raise Failure("no MOODLE_TOKEN in the environment" + (f" or in {env_file}" if env_file else " (or give --env-file)"))
    return url, token


def label(subject):
    return f"{subject['programme']} y{subject['year']} {subject['name']}"


def main(argv=None, call=None, out=sys.stdout, today=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--subjects", required=True, help="the JSON import-timetable --subjects-json wrote")
    parser.add_argument("--overrides", required=True, help="the private overrides file whose campusVirtual section is rewritten")
    parser.add_argument("--env-file", help="KEY=VALUE file with MOODLE_TOKEN (and MOODLE_URL), if not in the environment")
    parser.add_argument("--dry-run", action="store_true", help="print what would change; write nothing")
    try:
        args = parser.parse_args(argv)
    except SystemExit as e:
        return 0 if e.code == 0 else 64
    try:
        with open(args.subjects, encoding="utf-8-sig") as f:
            export = json.load(f)
        search, codes = export.get("search") or [], export.get("codes") or {}
        if not search:
            raise Failure("the overrides' campusVirtual.search lists no shortname prefix to search the catalog for")
        if call is None:
            call = moodle_call(*credentials(args.env_file))
        catalog = fetch_catalog(search, call)
        courses, report = link(export.get("subjects", []), catalog, codes, export.get("courses") or {})
        with open(args.overrides, encoding="utf-8") as f:
            text = f.read()
        updated = replace_section(text, render_section(search, codes, courses, today or datetime.date.today().isoformat()))
    except (OSError, ValueError, KeyError, Failure) as e:
        print(f"campus-virtual-courses failed: {e}", file=sys.stderr)
        return 1

    subjects = sum(len(v) for v in report.values())
    print(f"Catalog: {len(catalog)} courses under {', '.join(search)}.", file=out)
    print(f"Subjects: {subjects}: matched {len(report['matched']) + len(report['elsewhere'])} "
          f"({len(report['elsewhere'])} to another programme's course of the same name), ambiguous {len(report['ambiguous'])}, "
          f"unmatched {len(report['unmatched'])}, kept by hand {len(report['hand'])}.", file=out)
    if report["elsewhere"]:
        print("Matched to another programme's course:", file=out)
        for subject, course in report["elsewhere"]:
            print(f"  {label(subject)}: {course['shortname']}", file=out)
    if report["ambiguous"]:
        print("Ambiguous (left out; a hand: true entry picks one):", file=out)
        for subject, found in report["ambiguous"]:
            print(f"  {label(subject)}: " + ", ".join(f"{c['shortname']} (id {c['id']})" for c in found), file=out)
    if report["unmatched"]:
        print("Unmatched:", file=out)
        for subject, _ in report["unmatched"]:
            print(f"  {label(subject)}", file=out)

    before, after = flatten(export.get("courses") or {}), flatten(courses)
    changed = sum(1 for k in before.keys() & after.keys() if before[k] != after[k])
    print(f"Against the overrides: +{len(after.keys() - before.keys())} -{len(before.keys() - after.keys())}, {changed} changed.", file=out)
    if args.dry_run:
        print(f"Dry run: {args.overrides} not changed.", file=out)
        return 0
    with open(args.overrides, "w", encoding="utf-8") as f:
        f.write(updated)
    print(f"Wrote the campusVirtual section of {args.overrides}.", file=out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
