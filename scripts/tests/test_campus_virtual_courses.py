"""Tests of scripts/campus-virtual-courses.py against invented catalogs (no network, no real UPT data).

Run: python3 -m unittest discover -s scripts/tests -v
"""

import importlib.util
import io
import json
import os
import pathlib
import tempfile
import unittest
from unittest import mock

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "campus-virtual-courses.py"
_spec = importlib.util.spec_from_file_location("campus_virtual_courses", SCRIPT)
cv = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cv)

TOKEN = "0123456789abcdef0123456789abcdef"

# An invented faculty: prefix S1-X-FAC, programmes ALFA (also under the common code COMUN) and BETA.
CATALOG = [
    {"id": 101, "shortname": "S1-X-FAC-ALFA1-GF", "fullname": "Geometria fractalilor"},
    {"id": 102, "shortname": "S1-X-FAC-ALFA1-O-LSV", "fullname": "Logica senzorilor virtuali"},
    {"id": 103, "shortname": "S1-X-FAC-ALFA1-1X-CC", "fullname": "Cromodinamica cuantica - Seria 1X"},
    {"id": 104, "shortname": "S1-X-FAC-ALFA1-2Y-CC", "fullname": "Cromodinamica cuantica - Seria 2Y"},
    {"id": 105, "shortname": "S1-X-FAC-COMUN1-TAM", "fullname": "Teh.avansate moderne"},
    {"id": 106, "shortname": "S1-X-FAC-ALFA1-LE", "fullname": "Limba esperanto"},
    {"id": 201, "shortname": "S1-X-FAC-BETA2-ISR", "fullname": "Integrarea si testarea senzorilor robotici"},
    {"id": 202, "shortname": "S1-X-FAC-BETA2-O-VS", "fullname": "Vizualizare stiintifica (curs optional)"},
    {"id": 203, "shortname": "S1-X-FAC-PJ", "fullname": "Pedagogia jocului"},
    {"id": 301, "shortname": "S1-Y-FAC-GAMA1-ORB", "fullname": "Fundamente de orbitare &amp; zbor"},
]


def subject(programme, year, name, short_name=None, choice="Fixed"):
    return {"source": "orar", "programme": programme, "year": year, "name": name, "shortName": short_name, "choice": choice}


SUBJECTS = [
    subject("ALFA", 1, "Geometria fractalilor"),
    subject("ALFA", 1, "LSV"),  # the sheet's abbreviation is the name
    subject("ALFA", 1, "Cromodinamica cuantica"),  # one course per series
    subject("ALFA", 1, "Teh. avansate moderne"),  # under the common code
    subject("ALFA", 1, "Esperanto"),  # every word of it in one course's name
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


class Names(unittest.TestCase):
    def test_normalize_drops_case_diacritics_notes_and_the_series(self):
        self.assertEqual(cv.normalize("Algebră şi Geometrie (curs) - Seria 2D"), "algebra si geometrie")
        self.assertEqual(cv.normalize("Web Security &amp; Testing"), "web security testing")

    def test_scores(self):
        gf = CATALOG[0]
        self.assertEqual(cv.score(subject("ALFA", 1, "GEOMETRIA FRACTALILOR"), gf), 1.0)
        self.assertEqual(cv.score(subject("ALFA", 1, "GF"), gf), 1.0)
        self.assertEqual(cv.score(subject("ALFA", 1, "Geometrie", short_name="GF"), gf), 1.0)
        self.assertGreaterEqual(cv.score(subject("ALFA", 1, "Geom. fractalilor"), gf), cv.MATCH)
        self.assertLess(cv.score(subject("ALFA", 1, "Volapuk"), gf), cv.MATCH)

    def test_the_programme_year_code_follows_the_prefix(self):
        self.assertEqual(cv.programme_year_code("S1-X-FAC-ALFA1-O-LSV", "S1-X-FAC"), "ALFA1")
        self.assertIsNone(cv.programme_year_code("S1-X-FAC-PJ", "S1-X-FAC"))
        self.assertIsNone(cv.programme_year_code("S1-X-FAC-F-PJ", "S1-X-FAC"))


class Catalog(unittest.TestCase):
    def test_pages_through_each_prefix_and_keeps_courses_starting_with_it(self):
        calls = []
        catalog = cv.fetch_catalog(["S1-X-FAC", "S1-Y-FAC"], fake_moodle(CATALOG + [
            {"id": 900, "shortname": "OTHER-S1-X-FAC", "fullname": "Not ours"}], per_page=4, calls=calls))

        self.assertEqual(sorted(c["id"] for c in catalog), sorted(c["id"] for c in CATALOG))
        self.assertEqual(calls, [("S1-X-FAC", 0), ("S1-X-FAC", 1), ("S1-X-FAC", 2), ("S1-Y-FAC", 0)])
        orbit = next(c for c in catalog if c["id"] == 301)
        self.assertEqual((orbit["fullname"], orbit["code"]), ("Fundamente de orbitare & zbor", "GAMA1"))

    def test_a_web_service_error_fails_without_the_token(self):
        def call(function, **params):
            return {"exception": "moodle_exception", "errorcode": "invalidtoken", "message": "Invalid token - token not found"}

        with self.assertRaises(cv.Failure) as raised:
            cv.fetch_catalog(["S1-X-FAC"], call)
        self.assertIn("invalidtoken", str(raised.exception))
        self.assertNotIn(TOKEN, str(raised.exception))

    def test_the_token_goes_only_to_an_https_moodle_in_the_post_body(self):
        with self.assertRaises(cv.Failure):
            cv.moodle_call("http://moodle.example", TOKEN)

        sent = {}

        class Reply(io.BytesIO):
            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

        def urlopen(request, timeout):
            sent["url"], sent["body"] = request.full_url, request.data.decode()
            return Reply(b'{"total": 0, "courses": []}')

        with mock.patch.object(cv.urllib.request, "urlopen", urlopen):
            cv.fetch_catalog(["S1-X-FAC"], cv.moodle_call("https://moodle.example/", TOKEN))
        self.assertEqual(sent["url"], "https://moodle.example/webservice/rest/server.php")
        self.assertNotIn(TOKEN, sent["url"])
        self.assertIn(f"wstoken={TOKEN}", sent["body"])


class Matching(unittest.TestCase):
    def setUp(self):
        self.catalog = cv.fetch_catalog(["S1-X-FAC", "S1-Y-FAC"], fake_moodle(CATALOG))
        self.codes = {"ALFA": ["ALFA", "COMUN"]}

    def result(self, name, programme="ALFA", year=1):
        return cv.match(subject(programme, year, name), self.catalog, self.codes)

    def test_each_kind_of_subject(self):
        self.assertEqual(self.result("Geometria fractalilor"), ("matched", self.catalog_course(101)))
        self.assertEqual(self.result("LSV")[1]["id"], 102)
        self.assertEqual(self.result("Teh. avansate moderne")[1]["id"], 105)
        self.assertEqual(self.result("Esperanto")[1]["id"], 106)
        self.assertEqual(self.result("Pedagogia jocului")[1]["id"], 203)
        self.assertEqual(self.result("Vizualizare stiintifica", "BETA", 2)[1]["id"], 202)
        status, found = self.result("Cromodinamica cuantica")
        self.assertEqual((status, sorted(c["id"] for c in found)), ("ambiguous", [103, 104]))
        self.assertEqual(self.result("Volapuk"), ("unmatched", None))

    def test_a_course_of_another_programme_counts_only_under_the_same_name(self):
        # BETA's ISR course is no ALFA course, and "Integrarea senzorilor" is not its name.
        self.assertEqual(self.result("Integrarea si testarea senzorilor robotici")[1]["id"], 201)
        self.assertEqual(self.result("Integrarea senzorilor"), ("unmatched", None))

    def test_without_codes_a_programme_is_its_code_without_punctuation(self):
        catalog = [{"id": 7, "shortname": "S1-X-FAC-ALFAB3-GF", "fullname": "Geometria fractalilor", "code": "ALFAB3"}]
        self.assertEqual(cv.match(subject("ALFA-B", 3, "Geometria fractalilor"), catalog, {})[1]["id"], 7)

    def test_hand_entries_are_kept_as_they_are_even_for_a_subject_the_files_no_longer_have(self):
        existing = {"ALFA": {"1": {
            "cromodinamica CUANTICA": {"id": 104, "shortname": "S1-X-FAC-ALFA1-2Y-CC", "hand": True},
            "Geometria fractalilor": {"hand": True},
            "Retired subject": {"id": 999, "hand": True},
            "Volapuk": {"id": 555, "shortname": "stale"},
        }}}

        courses, report = cv.link(SUBJECTS, self.catalog, self.codes, existing)

        alfa = courses["ALFA"][1]
        self.assertEqual(alfa["cromodinamica CUANTICA"], {"id": 104, "shortname": "S1-X-FAC-ALFA1-2Y-CC", "hand": True})
        self.assertEqual(alfa["Geometria fractalilor"], {"hand": True})
        self.assertEqual(alfa["Retired subject"]["id"], 999)
        self.assertNotIn("Volapuk", alfa)  # a script-made entry is redone
        self.assertEqual(alfa["LSV"], {"id": 102, "shortname": "S1-X-FAC-ALFA1-O-LSV"})
        self.assertEqual(
            {k: len(v) for k, v in report.items()},
            {"matched": 5, "elsewhere": 1, "ambiguous": 0, "unmatched": 1, "hand": 2})

    def catalog_course(self, course_id):
        return next(c for c in self.catalog if c["id"] == course_id)


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


class OverridesFile(unittest.TestCase):
    def test_the_section_replaces_the_hand_written_one_and_keeps_every_other_line(self):
        section = cv.render_section(["S1-X-FAC"], {"ALFA": ["ALFA", "COMUN"]},
                                    {"ALFA": {1: {"Geometria fractalilor": {"id": 101, "shortname": "S1-X-FAC-ALFA1-GF"},
                                                  'Teh. "avansate"': {"hand": True}}}}, "2030-09-01")

        once = cv.replace_section(OVERRIDES, section)

        self.assertEqual(once, OVERRIDES.replace(
            'campusVirtual:\n  search: ["S1-X-FAC"]\n  codes: { ALFA: [ALFA, COMUN] }\n', section))
        self.assertIn('        "Geometria fractalilor": { id: 101, shortname: "S1-X-FAC-ALFA1-GF" }\n', section)
        self.assertIn('        "Teh. \\"avansate\\"": { hand: true }\n', section)
        self.assertTrue(section.startswith(cv.BEGIN) and section.endswith(cv.END + "\n"))
        # A second run replaces the marked section, markers included, and nothing else.
        again = cv.replace_section(once, cv.render_section(["S1-X-FAC"], {}, {}, "2030-09-02"))
        self.assertEqual(again.count("\ncampusVirtual:\n"), 1)
        self.assertEqual(again.count(cv.BEGIN), 1)
        self.assertIn("  courses: {}\n" + cv.END + "\n\n# Corrections per cell.\ncells:", again)
        self.assertTrue(again.startswith(OVERRIDES.split("campusVirtual:")[0]))

    def test_without_a_section_it_is_appended(self):
        text = "semester: { number: 1 }"
        self.assertEqual(cv.replace_section(text, "S\n"), "semester: { number: 1 }\n\nS\n")


class Run(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.overrides = os.path.join(self.dir.name, "overrides.yaml")
        self.subjects = os.path.join(self.dir.name, "subjects.json")
        with open(self.overrides, "w", encoding="utf-8") as f:
            f.write(OVERRIDES)
        with open(self.subjects, "w", encoding="utf-8") as f:
            json.dump({"programmes": ["ALFA", "BETA"], "search": ["S1-X-FAC", "S1-Y-FAC"], "codes": {"ALFA": ["ALFA", "COMUN"]},
                       "courses": {}, "subjects": SUBJECTS}, f)

    def tearDown(self):
        self.dir.cleanup()

    def run_script(self, *extra):
        out = io.StringIO()
        code = cv.main(["--subjects", self.subjects, "--overrides", self.overrides, *extra],
                       call=fake_moodle(CATALOG), out=out, today="2030-09-01")
        return code, out.getvalue()

    def test_it_writes_the_section_and_prints_the_counts_and_the_names_left_out(self):
        code, out = self.run_script()

        self.assertEqual(code, 0)
        self.assertIn("Subjects: 9: matched 7 (1 to another programme's course of the same name), ambiguous 1, unmatched 1, "
                      "kept by hand 0.", out)
        self.assertIn("  ALFA y1 Cromodinamica cuantica: S1-X-FAC-ALFA1-1X-CC (id 103), S1-X-FAC-ALFA1-2Y-CC (id 104)", out)
        self.assertIn("Unmatched:\n  ALFA y1 Volapuk\n", out)
        self.assertIn("Against the overrides: +7 -0, 0 changed.", out)
        with open(self.overrides, encoding="utf-8") as f:
            written = f.read()
        self.assertIn('    "BETA":\n      2:\n        "Integrarea si testarea senzorilor robotici": { id: 201, '
                      'shortname: "S1-X-FAC-BETA2-ISR" }\n', written)
        self.assertNotIn("Volapuk", written)

    def test_a_dry_run_writes_nothing(self):
        code, out = self.run_script("--dry-run")

        self.assertEqual(code, 0)
        self.assertIn("Dry run:", out)
        with open(self.overrides, encoding="utf-8") as f:
            self.assertEqual(f.read(), OVERRIDES)

    def test_the_token_comes_from_the_environment_or_the_env_file_and_is_never_printed(self):
        env_file = os.path.join(self.dir.name, "env")
        with open(env_file, "w", encoding="utf-8") as f:
            f.write(f"# Moodle\nexport MOODLE_URL='https://moodle.example'\nMOODLE_TOKEN=\"{TOKEN}\"\n")
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(cv.credentials(env_file), ("https://moodle.example", TOKEN))
            with self.assertRaises(cv.Failure) as raised:
                cv.credentials(None)
            self.assertNotIn(TOKEN, str(raised.exception))
        with mock.patch.dict(os.environ, {"MOODLE_TOKEN": "from-env"}, clear=True):
            self.assertEqual(cv.credentials(None), ("https://cv.upt.ro", "from-env"))

        def refuse(function, **params):
            return {"exception": "x", "errorcode": "invalidtoken", "message": "Invalid token"}

        err = io.StringIO()
        with mock.patch("sys.stderr", err):
            code = cv.main(["--subjects", self.subjects, "--overrides", self.overrides], call=refuse, out=io.StringIO())
        self.assertEqual(code, 1)
        self.assertIn("invalidtoken", err.getvalue())
        self.assertNotIn(TOKEN, err.getvalue())

    def test_usage_errors_exit_64(self):
        with mock.patch("sys.stderr", io.StringIO()):
            self.assertEqual(cv.main(["--overrides", self.overrides]), 64)


if __name__ == "__main__":
    unittest.main()
