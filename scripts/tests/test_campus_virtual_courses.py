"""Tests of scripts/campus-virtual-courses.py against invented catalogs (no network, no real UPT data).

Run: python3 -m unittest discover -s scripts/tests -v
"""

import hashlib
import importlib.util
import io
import json
import os
import pathlib
import stat
import tempfile
import unittest
import urllib.error
from unittest import mock

HERE = pathlib.Path(__file__).resolve().parent
SCRIPT = HERE.parent / "campus-virtual-courses.py"
FIXTURE = HERE / "fixtures" / "campus-virtual-section.yaml"
_spec = importlib.util.spec_from_file_location("campus_virtual_courses", SCRIPT)
cv = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cv)

TOKEN = "0123456789abcdef0123456789abcdef"

# An invented faculty: levels S1-X-FAC and S1-Y-FAC, programmes ALFA (also under the common code COMUN) and BETA.
CATALOG = [
    {"id": 101, "shortname": "S1-X-FAC-ALFA1-GF", "fullname": "Geometria fractalilor"},
    {"id": 102, "shortname": "S1-X-FAC-ALFA1-O-LSV", "fullname": "Logica senzorilor virtuali"},
    {"id": 103, "shortname": "S1-X-FAC-ALFA1-1X-CC", "fullname": "Cromodinamica cuantica - Seria 1X"},
    {"id": 104, "shortname": "S1-X-FAC-ALFA1-2Y-CC", "fullname": "Cromodinamica cuantica - Seria 2Y"},
    {"id": 105, "shortname": "S1-X-FAC-COMUN1-TAM", "fullname": "Teh.avansate moderne"},
    {"id": 106, "shortname": "S1-X-FAC-ALFA1-LE", "fullname": "Limba esperanto"},
    {"id": 107, "shortname": "S1-X-FAC-ALFA1-SAM", "fullname": "Securitatea aplicatiilor mobile"},
    {"id": 201, "shortname": "S1-X-FAC-BETA2-ISR", "fullname": "Integrarea si testarea senzorilor robotici"},
    {"id": 202, "shortname": "S1-X-FAC-BETA2-O-VS", "fullname": "Vizualizare stiintifica (curs optional)"},
    {"id": 203, "shortname": "S1-X-FAC-PJ", "fullname": "Pedagogia jocului"},
    {"id": 301, "shortname": "S1-Y-FAC-GAMA1-ORB", "fullname": "Fundamente de orbitare &amp; zbor"},
]


def subject(programme, year, name, short_name=None, choice="Fixed", source="orar"):
    return {"source": source, "programme": programme, "year": year, "name": name, "shortName": short_name, "choice": choice}


SUBJECTS = [
    subject("ALFA", 1, "Geometria fractalilor"),
    subject("ALFA", 1, "LSV"),  # the sheet's abbreviation is the name
    subject("ALFA", 1, "Cromodinamica cuantica"),  # one course per series
    subject("ALFA", 1, "Teh. avansate moderne"),  # under the common code
    subject("ALFA", 1, "Esperanto"),  # every word of it in one course's name
    subject("ALFA", 1, "Secu. aplic. mobile"),  # abbreviated words
    subject("ALFA", 1, "Pedagogia jocului", choice="Optional"),  # a course of no one programme, same name
    subject("ALFA", 1, "Volapuk"),  # nowhere
    subject("BETA", 2, "Vizualizare stiintifica", choice="Optional"),
    subject("BETA", 2, "Integrarea si testarea senzorilor robotici"),
]


def fake_moodle(catalog, per_page=None, calls=None):
    """A core_course_search_courses that pages through the courses whose shortname contains the search text."""

    def call(function, criterianame, criteriavalue, page, perpage):
        assert function == "core_course_search_courses" and criterianame == "search"
        size = per_page or perpage
        hits = [c for c in catalog if criteriavalue.upper() in c["shortname"].upper()]
        if calls is not None:
            calls.append((criteriavalue, page))
        return {"total": len(hits), "courses": hits[page * size:(page + 1) * size], "warnings": []}

    return call


def catalog_of(*courses):
    return cv.fetch_catalog(["S1-X-FAC", "S1-Y-FAC"], fake_moodle(list(courses)))


class Names(unittest.TestCase):
    def test_normalize_drops_case_diacritics_notes_and_the_series(self):
        self.assertEqual(cv.normalize("Algebră şi Geometrie (curs) - Seria 2D"), "algebra si geometrie")
        self.assertEqual(cv.normalize("Web Security &amp; Testing"), "web security testing")

    def test_numbers_are_digits_and_roman_numerals(self):
        self.assertEqual(cv.numbers("Fizica II"), {2})
        self.assertEqual(cv.numbers("Analiza matematica 1 - seria 2"), {1})
        self.assertEqual(cv.numbers("Geometria fractalilor"), set())

    def test_scores(self):
        gf = CATALOG[0]
        self.assertEqual(cv.score(subject("ALFA", 1, "GEOMETRIA FRACTALILOR"), gf), (cv.NAME, False))
        self.assertEqual(cv.score(subject("ALFA", 1, "GF"), gf), (cv.ABBREVIATION, True))
        self.assertEqual(cv.score(subject("ALFA", 1, "Geom. fractalilor"), gf)[0], cv.ABBREVIATED_WORDS)
        self.assertLess(cv.score(subject("ALFA", 1, "Volapuk"), gf)[0], cv.MATCH)

    def test_the_name_key_is_the_imports(self):
        self.assertEqual(cv.name_key("  Ştiinţa  Datelor.Mari "), "stiinta datelor mari")

    def test_the_programme_year_code_follows_the_prefix(self):
        self.assertEqual(cv.programme_year_code("S1-X-FAC-ALFA1-O-LSV", "S1-X-FAC"), "ALFA1")
        self.assertIsNone(cv.programme_year_code("S1-X-FAC-PJ", "S1-X-FAC"))
        self.assertIsNone(cv.programme_year_code("S1-X-FAC-F-PJ", "S1-X-FAC"))


class Catalog(unittest.TestCase):
    def test_pages_through_each_prefix_and_keeps_courses_starting_with_it(self):
        calls = []
        catalog = cv.fetch_catalog(["S1-X-FAC", "S1-Y-FAC"], fake_moodle(CATALOG + [
            {"id": 900, "shortname": "OTHER-S1-X-FAC", "fullname": "Not ours"},
            {"id": 901, "shortname": "S1-X-FACX-ALFA1-GF", "fullname": "Not this level"}], per_page=4, calls=calls))

        self.assertEqual(sorted(c["id"] for c in catalog), sorted(c["id"] for c in CATALOG))
        self.assertEqual(calls, [("S1-X-FAC", 0), ("S1-X-FAC", 1), ("S1-X-FAC", 2), ("S1-Y-FAC", 0)])
        orbit = next(c for c in catalog if c["id"] == 301)
        self.assertEqual((orbit["fullname"], orbit["code"], orbit["prefix"]), ("Fundamente de orbitare & zbor", "GAMA1", "S1-Y-FAC"))

    def test_a_prefix_with_a_stray_dash_is_refused(self):
        with self.assertRaises(cv.Failure):
            cv.fetch_catalog(["S1-X-FAC-"], fake_moodle(CATALOG))

    def test_a_web_service_error_fails_without_the_token(self):
        def call(function, **params):
            return {"exception": "moodle_exception", "errorcode": "invalidtoken", "message": "Invalid token - token not found"}

        with self.assertRaises(cv.Failure) as raised:
            cv.fetch_catalog(["S1-X-FAC"], call)
        self.assertIn("invalidtoken", str(raised.exception))

    def test_the_token_goes_only_to_campus_virtual_in_the_post_body(self):
        for url in ("http://cv.upt.ro", "https://moodle.example", "https://cv.upt.ro.example", "https://user@cv.upt.ro",
                    "https://cv.upt.ro:8443"):
            with self.subTest(url=url), self.assertRaises(cv.Failure) as raised:
                cv.moodle_call(url, TOKEN)
            self.assertNotIn(TOKEN, str(raised.exception))

        sent = {}

        def urlopen(request, timeout):
            sent["url"], sent["body"] = request.full_url, request.data.decode()
            return Reply(b'{"total": 0, "courses": []}')

        with mock.patch.object(cv.urllib.request, "urlopen", urlopen):
            cv.fetch_catalog(["S1-X-FAC"], cv.moodle_call("https://cv.upt.ro/", TOKEN))
        self.assertEqual(sent["url"], "https://cv.upt.ro/webservice/rest/server.php")
        self.assertIn(f"wstoken={TOKEN}", sent["body"])


class Reply(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


class Matching(unittest.TestCase):
    def setUp(self):
        self.catalog = catalog_of(*CATALOG)
        self.codes = {"ALFA": ["ALFA", "COMUN"]}

    def result(self, name, programme="ALFA", year=1, catalog=None, short_name=None):
        return cv.match(subject(programme, year, name, short_name), catalog or self.catalog, self.codes)

    def course_id(self, *args, **kwargs):
        status, found, _ = self.result(*args, **kwargs)
        self.assertEqual(status, "matched", args)
        return found["id"]

    def test_each_kind_of_subject(self):
        self.assertEqual(self.result("Geometria fractalilor"), ("matched", next(c for c in self.catalog if c["id"] == 101), cv.NAME))
        self.assertEqual(self.result("LSV")[2], cv.ABBREVIATION)
        self.assertEqual(self.course_id("LSV"), 102)
        self.assertEqual(self.course_id("Teh. avansate moderne"), 105)
        self.assertEqual(self.course_id("Esperanto"), 106)
        self.assertEqual(self.result("Secu. aplic. mobile")[1:], (next(c for c in self.catalog if c["id"] == 107), cv.ABBREVIATED_WORDS))
        self.assertEqual(self.course_id("Pedagogia jocului"), 203)
        self.assertEqual(self.course_id("Vizualizare stiintifica", "BETA", 2), 202)
        self.assertEqual(self.course_id("Reasearch topics", catalog=catalog_of(
            {"id": 7, "shortname": "S1-X-FAC-ALFA1-RT", "fullname": "Research Topics"})), 7)
        status, found, _ = self.result("Cromodinamica cuantica")
        self.assertEqual((status, sorted(c["id"] for c in found)), ("ambiguous", [103, 104]))
        self.assertEqual(self.result("Volapuk"), ("unmatched", None, None))

    def test_a_name_with_another_number_never_matches(self):
        catalog = catalog_of({"id": 1, "shortname": "S1-X-FAC-ALFA1-AM2", "fullname": "Analiza matematica 2"},
                             {"id": 2, "shortname": "S1-X-FAC-ALFA1-FII", "fullname": "Fizica II"})
        self.assertEqual(self.result("Analiza matematica 1", catalog=catalog)[0], "unmatched")
        self.assertEqual(self.result("Fizica I", catalog=catalog)[0], "unmatched")
        self.assertEqual(self.course_id("Fizica 2", catalog=catalog), 2)

    def test_a_similar_spelling_of_other_words_is_no_match(self):
        catalog = catalog_of({"id": 1, "shortname": "S1-X-FAC-ALFA1-POO", "fullname": "Proiectare orientata pe obiecte"})
        self.assertEqual(self.result("Programare orientata pe obiecte", catalog=catalog)[0], "unmatched")

    def test_initials_alone_are_no_match(self):
        catalog = catalog_of({"id": 1, "shortname": "S1-X-FAC-ALFA1-SO", "fullname": "Sisteme de operare"})
        self.assertEqual(self.result("Securitatea organizatiilor", catalog=catalog)[0], "unmatched")

    def test_an_abbreviation_is_below_a_name_and_against_a_name_match_is_ambiguous(self):
        signals = {"id": 1, "shortname": "S1-X-FAC-ALFA1-PRS", "fullname": "Prelucrarea semnalelor"}
        systems = {"id": 2, "shortname": "S1-X-FAC-ALFA1-SIS", "fullname": "Programarea sistemelor"}
        real_time = {"id": 3, "shortname": "S1-X-FAC-ALFA1-PSTR", "fullname": "Programarea sistemelor de timp real"}
        two_letters = {"id": 4, "shortname": "S1-X-FAC-ALFA1-PS", "fullname": "Prelucrarea semnalelor"}
        # A two-letter abbreviation of the sheet's counts for nothing; three letters lose to the course's exact name.
        self.assertEqual(self.result("Programarea sistemelor", short_name="PS", catalog=catalog_of(two_letters))[0], "unmatched")
        self.assertEqual(self.course_id("Programarea sistemelor", short_name="PRS", catalog=catalog_of(signals, systems)), 2)
        # Against a course that fits by name only under 1.0, the abbreviation doesn't win: ambiguous.
        status, found, _ = self.result("Programarea sistemelor", short_name="PRS", catalog=catalog_of(signals, real_time))
        self.assertEqual((status, sorted(c["id"] for c in found)), ("ambiguous", [1, 3]))

    def test_another_programmes_course_is_of_the_same_level_and_year_or_of_none(self):
        def other(code, prefix="S1-X-FAC"):
            return {"id": 9, "shortname": f"{prefix}-{code}-TJ", "fullname": "Teoria jocurilor"}

        own = {"id": 1, "shortname": "S1-X-FAC-ALFA1-GF", "fullname": "Geometria fractalilor"}
        self.assertEqual(self.course_id("Teoria jocurilor", catalog=catalog_of(own, other("BETA1"))), 9)
        self.assertEqual(self.result("Teoria jocurilor", catalog=catalog_of(own, other("BETA2")))[0], "unmatched")
        self.assertEqual(self.result("Teoria jocurilor", catalog=catalog_of(own, other("BETA1", "S1-Y-FAC")))[0], "unmatched")
        self.assertEqual(self.course_id("Teoria jocurilor", catalog=catalog_of(
            own, {"id": 9, "shortname": "S1-X-FAC-TJ", "fullname": "Teoria jocurilor"})), 9)

    def test_without_codes_a_programme_is_its_code_without_punctuation(self):
        catalog = [{"id": 7, "shortname": "S1-X-FAC-ALFAB3-GF", "fullname": "Geometria fractalilor", "code": "ALFAB3", "prefix": "S1-X-FAC"}]
        self.assertEqual(cv.match(subject("ALFA-B", 3, "Geometria fractalilor"), catalog, {})[1]["id"], 7)

    def test_hand_entries_are_kept_as_they_are_even_for_a_subject_the_files_no_longer_have(self):
        existing = {"ALFA": {"1": {
            "cromodinamica CUANTICA": {"id": 104, "shortname": "S1-X-FAC-ALFA1-2Y-CC", "hand": True},
            "Geometria fractalilor": {"hand": True},
            "Retired subject": {"id": 999, "hand": True},
            "Volapuk": {"id": 555, "shortname": "stale"},
        }}}
        # The same subject in a second workbook: still one entry, and the hand one.
        subjects = SUBJECTS + [subject("ALFA", 1, "Geometria Fractalilor", source="other")]

        courses, report = cv.link(subjects, self.catalog, self.codes, existing)

        alfa = courses["ALFA"][1]
        self.assertEqual(alfa["cromodinamica CUANTICA"], {"id": 104, "shortname": "S1-X-FAC-ALFA1-2Y-CC", "hand": True})
        self.assertEqual(alfa["Geometria fractalilor"], {"hand": True})
        self.assertNotIn("Geometria Fractalilor", alfa)
        self.assertEqual(alfa["Retired subject"]["id"], 999)
        self.assertNotIn("Volapuk", alfa)  # a script-made entry is redone
        self.assertEqual(alfa["LSV"], {"id": 102, "shortname": "S1-X-FAC-ALFA1-O-LSV"})
        self.assertEqual({k: len(v) for k, v in report.items()},
                         {"matched": 6, "elsewhere": 1, "ambiguous": 0, "unmatched": 1, "hand": 2})


OVERRIDES = """semester: { academicYear: "2030-2031", number: 1 }
programmes:
  ALFA: "Programul Alfa"

# The schedule of the year.
campusVirtual:
  search: ["S1-X-FAC"]
  codes: { ALFA: [ALFA, COMUN] }

# Corrections per cell.
cells:
  "An 1!C19": { text: "Skipped", skip: true }
"""

COURSES = {"ALFA": {1: {"Geometria fractalilor": {"id": 101, "shortname": "S1-X-FAC-ALFA1-GF"},
                        'Teh. "avansate"': {"hand": True}}}}


def render(courses=None, comments=None, day="2030-09-01"):
    return lambda found: cv.render_section(["S1-X-FAC"], {"ALFA": ["ALFA", "COMUN"]}, COURSES if courses is None else courses,
                                           day, found if comments is None else comments)


class OverridesFile(unittest.TestCase):
    def test_the_section_replaces_the_hand_written_one_and_keeps_every_other_line(self):
        once = cv.rewrite(OVERRIDES, render())

        section = render(comments={None: []})(None)
        self.assertEqual(once, OVERRIDES.replace('campusVirtual:\n  search: ["S1-X-FAC"]\n  codes: { ALFA: [ALFA, COMUN] }\n', section))
        self.assertIn('        "Geometria fractalilor": { id: 101, shortname: "S1-X-FAC-ALFA1-GF" }\n', section)
        self.assertIn('        "Teh. \\"avansate\\"": { hand: true }\n', section)
        # A second run replaces the marked section, markers included, and nothing else.
        again = cv.rewrite(once, render(courses={}, day="2030-09-02"))
        self.assertEqual((again.count("\ncampusVirtual:\n"), again.count(cv.BEGIN), again.count(cv.END)), (1, 1, 1))
        self.assertIn("  courses: {}\n" + cv.END + "\n\n# Corrections per cell.\ncells:", again)
        self.assertTrue(again.startswith(OVERRIDES.split("campusVirtual:")[0]))

    def test_comments_inside_the_section_are_kept(self):
        written = cv.rewrite(OVERRIDES, render())
        edited = written.replace('        "Teh. \\"avansate\\""', '        # Linked by hand: no course of its own.\n        "Teh. \\"avansate\\""') \
            .replace("  search:", "  # Search both levels next year.\n  search:") \
            .replace('        "Geometria fractalilor"', '        # Gone next run.\n        "Geometria fractalilor"')

        again = cv.rewrite(edited, render(courses={"ALFA": {1: {'Teh. "avansate"': {"hand": True}}}}))

        self.assertIn('      1:\n        # Linked by hand: no course of its own.\n        "Teh. \\"avansate\\"": { hand: true }\n', again)
        self.assertIn("campusVirtual:\n  # Search both levels next year.\n  # Gone next run.\n  search:", again)

    def test_stale_markers_anywhere_are_dropped(self):
        text = OVERRIDES.replace("# The schedule", cv.BEGIN + ": an old run\n\n# The schedule") + cv.END + "\n"

        written = cv.rewrite(text, render())

        self.assertEqual((written.count(cv.BEGIN), written.count(cv.END)), (1, 1))
        self.assertIn("# The schedule of the year.\n" + cv.BEGIN, written)

    def test_a_section_without_its_end_marker_or_at_the_end_of_the_file(self):
        no_end = cv.rewrite(OVERRIDES, render()).replace(cv.END + "\n", "")
        self.assertEqual(cv.rewrite(no_end, render()), cv.rewrite(OVERRIDES, render()))

        at_end = 'semester: { number: 1 }\ncampusVirtual:\n  search: ["S1-X-FAC"]\n  # mine\n  codes: {}'
        written = cv.rewrite(at_end, render())
        self.assertTrue(written.startswith("semester: { number: 1 }\n" + cv.BEGIN))
        self.assertTrue(written.endswith(cv.END + "\n"))
        self.assertIn("campusVirtual:\n  # mine\n  search:", written)

    def test_without_a_section_it_is_appended(self):
        self.assertEqual(cv.rewrite("semester: { number: 1 }", lambda found: "S\n"), "semester: { number: 1 }\n\nS\n")

    def test_two_sections_are_refused(self):
        with self.assertRaises(cv.Failure):
            cv.rewrite(OVERRIDES + "campusVirtual: {}\n", render())

    def test_the_shared_fixture_is_what_the_script_writes(self):
        # TimetableParserTests (C#) parses this same file with ImportOverrides.Parse: the round trip.
        section = cv.render_section(["S1-X-FAC"], {"ALFA": ["ALFA", "ALFAX"]}, {"ALFA": {1: {
            "GEOMETRIA fractalilor": {"id": 4101, "shortname": "S1-X-FAC-ALFA1-GF"},
            "Pedagogia jocului": {"id": 4102, "shortname": "S1-X-FAC-PJ"},
            "Logica senzorilor virtuali": {"hand": True},
            "Nimic de acest fel": {"id": 4103, "shortname": "S1-X-FAC-ALFA1-N"}}}}, "2030-09-01",
            {None: ["# Invented: the test fixture of TimetableParserTests and test_campus_virtual_courses.py."],
             ("ALFA", 1, "logica senzorilor virtuali"): ["# By hand: no course of its own."]})
        self.assertEqual(FIXTURE.read_text(encoding="utf-8"), section,
                         "the script writes the section differently now: regenerate scripts/tests/fixtures/campus-virtual-section.yaml")


class Run(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.overrides = os.path.join(self.dir.name, "overrides.yaml")
        self.subjects = os.path.join(self.dir.name, "subjects.json")
        with open(self.overrides, "w", encoding="utf-8", newline="") as f:
            f.write(OVERRIDES)
        os.chmod(self.overrides, 0o600)
        self.export()

    def export(self, courses=None, sha=None):
        with open(self.overrides, "rb") as f:
            digest = hashlib.sha256(f.read()).hexdigest()
        with open(self.subjects, "w", encoding="utf-8") as f:
            json.dump({"overridesSha256": sha or digest, "programmes": ["ALFA", "BETA"], "search": ["S1-X-FAC", "S1-Y-FAC"],
                       "codes": {"ALFA": ["ALFA", "COMUN"]}, "courses": courses or {}, "subjects": SUBJECTS}, f)

    def tearDown(self):
        self.dir.cleanup()

    def run_script(self, *extra, call=None):
        out, err = io.StringIO(), io.StringIO()
        with mock.patch("sys.stderr", err):
            code = cv.main(["--subjects", self.subjects, "--overrides", self.overrides, *extra],
                           call=call or fake_moodle(CATALOG), out=out, today="2030-09-01")
        return code, out.getvalue(), err.getvalue()

    def read(self, path=None):
        with open(path or self.overrides, encoding="utf-8") as f:
            return f.read()

    def test_it_writes_the_section_and_prints_the_counts_and_the_names_left_out(self):
        mode = stat.S_IMODE(os.stat(self.overrides).st_mode)  # 0o600 here; Windows keeps only read-only

        code, out, _ = self.run_script()

        self.assertEqual(code, 0)
        self.assertIn("Subjects: 10: matched 8 (1 to another programme's course of the same name), ambiguous 1, unmatched 1, "
                      "kept by hand 0.", out)
        self.assertIn("Matched under 1.0 (check them):\n  0.85 ALFA y1 Esperanto: S1-X-FAC-ALFA1-LE Limba esperanto\n"
                      "  0.90 ALFA y1 Secu. aplic. mobile: S1-X-FAC-ALFA1-SAM Securitatea aplicatiilor mobile\n"
                      "  0.95 ALFA y1 LSV: S1-X-FAC-ALFA1-O-LSV Logica senzorilor virtuali\n", out)
        self.assertIn("  ALFA y1 Cromodinamica cuantica: S1-X-FAC-ALFA1-1X-CC (id 103), S1-X-FAC-ALFA1-2Y-CC (id 104)", out)
        self.assertIn("Unmatched:\n  ALFA y1 Volapuk\n", out)
        self.assertIn("Against the overrides: +8 -0, 0 changed.", out)
        written = self.read()
        self.assertIn('    "BETA":\n      2:\n        "Integrarea si testarea senzorilor robotici": { id: 201, '
                      'shortname: "S1-X-FAC-BETA2-ISR" }\n', written)
        self.assertNotIn("Volapuk", written)
        # Replaced in one step: the old file is kept beside it, and the mode is the old one.
        self.assertEqual(self.read(self.overrides + ".bak"), OVERRIDES)
        self.assertEqual(stat.S_IMODE(os.stat(self.overrides).st_mode), mode)
        self.assertEqual(sorted(os.listdir(self.dir.name)), ["overrides.yaml", "overrides.yaml.bak", "subjects.json"])

    def test_the_summary_names_what_changed_and_what_went(self):
        self.export(courses={"ALFA": {"1": {"Geometria fractalilor": {"id": 999, "shortname": "S1-X-FAC-OLD"},
                                            "Retired": {"id": 5, "shortname": "S1-X-FAC-R"}}}})

        _, out, _ = self.run_script("--dry-run")

        self.assertIn("Against the overrides: +7 -1, 1 changed.\n  - ALFA y1 Retired: 5\n"
                      "  ~ ALFA y1 Geometria fractalilor: 999 -> 101\n", out)

    def test_a_dry_run_writes_nothing(self):
        code, out, _ = self.run_script("--dry-run")

        self.assertEqual(code, 0)
        self.assertIn("Dry run:", out)
        self.assertEqual(self.read(), OVERRIDES)
        self.assertFalse(os.path.exists(self.overrides + ".bak"))

    def test_an_export_of_another_version_of_the_overrides_is_refused(self):
        self.export(sha="0" * 64)

        code, _, err = self.run_script()

        self.assertEqual(code, 1)
        self.assertIn("was exported from another version", err)
        self.assertEqual(self.read(), OVERRIDES)

    def test_the_token_comes_from_the_environment_or_the_env_file(self):
        env_file = os.path.join(self.dir.name, "env")
        with open(env_file, "w", encoding="utf-8") as f:
            f.write(f"# Moodle\nexport MOODLE_URL='https://cv.upt.ro'\nMOODLE_TOKEN=\"{TOKEN}\"\n")
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(cv.credentials(env_file), ("https://cv.upt.ro", TOKEN))
            with self.assertRaises(cv.Failure) as raised:
                cv.credentials(None)
            self.assertNotIn(TOKEN, str(raised.exception))
        with mock.patch.dict(os.environ, {"MOODLE_TOKEN": "from-env"}, clear=True):
            self.assertEqual(cv.credentials(None), ("https://cv.upt.ro", "from-env"))

    def test_the_token_is_never_printed_whatever_fails(self):
        def raising(error):
            def urlopen(request, timeout):
                raise error
            return urlopen

        failures = {
            "http error": raising(urllib.error.HTTPError("https://cv.upt.ro/webservice/rest/server.php", 503, f"down {TOKEN}", {}, None)),
            "network": raising(urllib.error.URLError(f"no route ({TOKEN})")),
            "timeout": raising(TimeoutError(f"timed out {TOKEN}")),
            "not json": lambda request, timeout: Reply(f"<html>{TOKEN}</html>".encode()),
            "ws error": lambda request, timeout: Reply(json.dumps(
                {"exception": "x", "errorcode": "invalidtoken", "message": "Invalid token"}).encode()),
        }
        for what, urlopen in failures.items():
            with self.subTest(what), mock.patch.dict(os.environ, {"MOODLE_TOKEN": TOKEN}, clear=True), \
                    mock.patch.object(cv.urllib.request, "urlopen", urlopen):
                out, err = io.StringIO(), io.StringIO()
                with mock.patch("sys.stderr", err):
                    code = cv.main(["--subjects", self.subjects, "--overrides", self.overrides], out=out)
                self.assertEqual(code, 1)
                self.assertIn("campus-virtual-courses failed", err.getvalue())
                self.assertNotIn(TOKEN, err.getvalue() + out.getvalue())
        with mock.patch.dict(os.environ, {"MOODLE_TOKEN": TOKEN, "MOODLE_URL": "https://moodle.example"}, clear=True):
            err = io.StringIO()
            with mock.patch("sys.stderr", err):
                self.assertEqual(cv.main(["--subjects", self.subjects, "--overrides", self.overrides], out=io.StringIO()), 1)
            self.assertIn("MOODLE_URL must be https://cv.upt.ro", err.getvalue())
            self.assertNotIn(TOKEN, err.getvalue())

    def test_usage_errors_exit_64(self):
        with mock.patch("sys.stderr", io.StringIO()):
            self.assertEqual(cv.main(["--overrides", self.overrides]), 64)


if __name__ == "__main__":
    unittest.main()
