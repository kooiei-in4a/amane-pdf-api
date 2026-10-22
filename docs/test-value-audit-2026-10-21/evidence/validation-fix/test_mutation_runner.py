#!/usr/bin/env python3
"""変異の検出判定が、無関係な失敗を受け入れないことを検証する。"""
import copy
import json
import runpy
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
VALIDATION = runpy.run_path(str(HERE / 'run-mutations.py'))
IMPROVEMENTS = runpy.run_path(str(HERE.parent / 'improvements/run-mutations.py'))['namespace']
CHECK = VALIDATION['validate_run']


def records(kind):
    return json.loads((HERE.parent / kind / 'results.json').read_text())['mutations']


class MutationVerdictTests(unittest.TestCase):
    def check(self, row, namespace, check_cases=False):
        # Historical records have no parameter names. Latest records must also satisfy the case mapping.
        cases = namespace['CASE_OUTCOMES'].get(row['name']) if check_cases else None
        return CHECK(row, sorted({r['method'] for r in row['results']}), namespace['EXPECTATIONS'][row['name']], cases)

    def test_latest_records_match_assertions_parameter_cases_and_controls(self):
        for kind, namespace in [('validation', VALIDATION), ('improvements', IMPROVEMENTS)]:
            path = HERE.parent / 'review-fixes' / f'results-{kind}.json'
            report = json.loads(path.read_text())
            rows = report['mutations']
            self.assertEqual(7, len(rows))
            self.assertEqual({m[0] for m in namespace['MUTATIONS']}, {r['name'] for r in rows})
            for row in rows:
                with self.subTest(group=kind, mutation=row['name']):
                    self.check(row, namespace, check_cases=True)
            self.assertEqual(2, len(report['controls']))
            self.assertEqual({'control-before', 'control-after'}, {r['name'] for r in report['controls']})
            for row in report['controls']:
                with self.subTest(group=kind, control=row['name']):
                    CHECK(row, namespace['ALL'])
                    self.assertEqual(namespace['CONTROL_CASES'], len(row['results']))

    def test_wrong_missing_or_duplicate_parameter_names_are_rejected(self):
        path = HERE.parent / 'review-fixes/results-improvements.json'
        original = next(r for r in json.loads(path.read_text())['mutations'] if r['name'].startswith('overlay-'))
        for kind in ('swapped', 'missing', 'duplicate'):
            row = copy.deepcopy(original)
            passed = next(r for r in row['results'] if r['outcome'] == 'Passed')
            failed = next(r for r in row['results'] if r['outcome'] == 'Failed')
            if kind == 'swapped':
                passed['case_name'], failed['case_name'] = failed['case_name'], passed['case_name']
            elif kind == 'missing':
                del passed['case_name']
            else:
                failed['case_name'] = passed['case_name']
            with self.subTest(kind=kind), self.assertRaises(AssertionError):
                self.check(row, IMPROVEMENTS, check_cases=True)

    def test_recorded_validation_failures_match_their_assertions(self):
        for row in records('validation-fix'):
            with self.subTest(mutation=row['name']):
                self.check(row, VALIDATION)

    def test_recorded_improvement_failures_match_except_retired_fallback_oracle(self):
        for row in records('improvements'):
            with self.subTest(mutation=row['name']):
                if row['name'] == 'compress-raw-cleanup-removed':
                    # The old 4 -> 0 header failure must not satisfy the new explicit cleanup assertion.
                    with self.assertRaises(AssertionError):
                        self.check(row, IMPROVEMENTS)
                else:
                    self.check(row, IMPROVEMENTS)

    def test_environment_failures_are_not_killed_mutations(self):
        row = next(r for r in records('improvements') if r['name'].startswith('overlay-'))
        for failure in row['results']:
            if failure['outcome'] == 'Failed':
                failure['failure'] = 'Assert.Fail failed. Child was not reaped.'
        with self.assertRaises(AssertionError):
            self.check(row, IMPROVEMENTS)

    def test_one_matching_failure_cannot_hide_an_unrelated_failure(self):
        row = next(r for r in records('improvements') if r['name'].startswith('overlay-'))
        next(r for r in row['results'] if r['outcome'] == 'Failed')['failure'] = 'Child was not reaped.'
        with self.assertRaises(AssertionError):
            self.check(row, IMPROVEMENTS)

    def test_unaffected_parameter_rows_must_stay_passed(self):
        row = next(r for r in records('improvements') if r['name'].startswith('overlay-'))
        failed = next(r for r in row['results'] if r['outcome'] == 'Failed')
        passed = next(r for r in row['results'] if r['outcome'] == 'Passed')
        passed.update(failed)
        with self.assertRaises(AssertionError):
            self.check(row, IMPROVEMENTS)

    def test_survivors_empty_discovery_and_runner_errors_are_rejected(self):
        original = records('validation-fix')[0]
        for kind in ('survivor', 'empty', 'aborted', 'error', 'run-error', 'exit-code', 'wrong-assert'):
            row = copy.deepcopy(original)
            methods = [row['results'][0]['method']]
            if kind == 'survivor':
                row['exit_code'] = 0
                row['results'][0]['outcome'] = 'Passed'
            elif kind == 'empty':
                row['results'] = []
            elif kind in ('aborted', 'error'):
                row['counters'][kind] = '1'
            elif kind == 'run-error':
                row['run_errors'] = ['testhost crashed']
            elif kind == 'exit-code':
                row['exit_code'] = 2
            else:
                row['results'][0]['assertion'] = 'Assert.AreEqual(unrelated, value)'
            with self.subTest(kind=kind), self.assertRaises(AssertionError):
                CHECK(row, methods, VALIDATION['EXPECTATIONS'][row['name']])

    def test_normal_controls_reject_failures(self):
        row = records('validation-fix')[0]
        row['exit_code'] = 0
        with self.assertRaises(AssertionError):
            CHECK(row, [row['results'][0]['method']])


if __name__ == '__main__':
    unittest.main()
