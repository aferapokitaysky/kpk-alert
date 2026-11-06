import io
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

from coverage_summary import main, read_coverage, read_postgres_results


class CoverageSummaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/ci.yml").read_text(
            encoding="utf-8"
        )

    def workflow_step(self, name: str) -> str:
        lines = self.workflow.splitlines()
        start = next(
            index for index, line in enumerate(lines) if line == f"      - name: {name}"
        )
        end = next(
            (
                index
                for index in range(start + 1, len(lines))
                if lines[index].startswith("      - ")
            ),
            len(lines),
        )
        return "\n".join(lines[start:end])

    def test_unit_coverage_summary_requires_successful_unit_tests(self) -> None:
        test_step = self.workflow_step("Unit-тести з покриттям")
        summary_step = self.workflow_step("Показати покриття модулів")

        self.assertIn("id: unit-tests", test_step)
        self.assertIn("if: success() && steps.unit-tests.outcome == 'success'", summary_step)

    def test_postgres_coverage_summary_requires_successful_postgres_tests(self) -> None:
        test_step = self.workflow_step("PostgreSQL-тести з покриттям")
        summary_step = self.workflow_step("Перевірити виконання PostgreSQL-тестів і показати покриття")

        self.assertIn("id: postgres-tests", test_step)
        self.assertIn('--filter "TestArea=PostgreSQL"', test_step)
        self.assertIn("if: success() && steps.postgres-tests.outcome == 'success'", summary_step)

    def test_combined_coverage_gate_waits_for_unit_and_postgres_jobs(self) -> None:
        self.assertIn("combined-coverage:", self.workflow)
        self.assertIn("needs: [build, postgres-migrations]", self.workflow)
        self.assertIn("scripts/ci/coverage_union.py", self.workflow)
        self.assertIn("--minimum-module-line-coverage 70", self.workflow)
        self.assertIn("name: test-results-and-coverage", self.workflow)
        self.assertIn("name: postgresql-integration-coverage", self.workflow)

    def write_xml(self, content: str) -> str:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "report.xml"
        path.write_text(content, encoding="utf-8")
        return str(path)

    def write_coverage_report(self) -> str:
        return self.write_xml(
            '<coverage><package name="Watcher.Api" line-rate="0.8" branch-rate="0.5" /></coverage>'
        )

    def test_reads_each_production_module_and_skips_test_assemblies(self) -> None:
        path = self.write_xml(
            '<coverage><packages>'
            '<package name="Watcher.Api" line-rate="0.8" branch-rate="0.5" />'
            '<package name="Watcher.UnitTests" line-rate="1" branch-rate="1" />'
            '</packages></coverage>'
        )

        modules = read_coverage(path)

        self.assertEqual(["Watcher.Api"], [module.name for module in modules])
        self.assertEqual(0.8, modules[0].line_rate)
        self.assertEqual(0.5, modules[0].branch_rate)

    def test_rejects_coverage_without_production_modules(self) -> None:
        path = self.write_xml(
            '<coverage><package name="Watcher.Api.Tests" line-rate="1" branch-rate="1" />'
            '</coverage>'
        )

        with self.assertRaisesRegex(ValueError, "немає production-модулів"):
            read_coverage(path)

    def test_counts_results_from_multiple_postgres_test_classes(self) -> None:
        path = self.write_xml(
            '<TestRun><TestDefinitions>'
            '<UnitTest id="1"><TestMethod className="Watcher.Api.IntegrationTests.CommandReceiptProcessingTests" /></UnitTest>'
            '<UnitTest id="2"><TestMethod className="Watcher.Api.IntegrationTests.CommandDeliveryMaterializerTests" /></UnitTest>'
            '<UnitTest id="3"><TestMethod className="Watcher.Api.IntegrationTests.CommandDeliveryMaterializerTests" /></UnitTest>'
            '</TestDefinitions><Results>'
            '<UnitTestResult testId="1" outcome="Passed" />'
            '<UnitTestResult testId="2" outcome="NotExecuted" />'
            '<UnitTestResult testId="3" outcome="Failed" />'
            '</Results></TestRun>'
        )

        results = read_postgres_results(path)

        self.assertEqual(1, results.passed)
        self.assertEqual(1, results.skipped)
        self.assertEqual(1, results.failed)
        self.assertEqual(3, results.total)

    def test_counts_results_without_test_definitions(self) -> None:
        path = self.write_xml(
            '<TestRun xmlns="urn:trx"><Results>'
            '<UnitTestResult testName="Watcher.Api.IntegrationTests.CommandReceiptProcessingTests.Create" outcome="Passed" />'
            '</Results></TestRun>'
        )

        self.assertEqual(1, read_postgres_results(path).passed)

    def test_require_postgres_accepts_a_filtered_run_with_only_passed_tests(self) -> None:
        coverage_path = self.write_coverage_report()
        trx_path = self.write_xml(
            '<TestRun><Results><UnitTestResult outcome="Passed" />'
            '<UnitTestResult outcome="Passed" /></Results></TestRun>'
        )

        self.assertEqual(0, self.run_report(coverage_path, trx_path))

    def test_require_postgres_rejects_a_run_with_skipped_tests(self) -> None:
        coverage_path = self.write_coverage_report()
        trx_path = self.write_xml(
            '<TestRun><Results><UnitTestResult outcome="Passed" />'
            '<UnitTestResult outcome="NotExecuted" /></Results></TestRun>'
        )

        self.assertEqual(1, self.run_report(coverage_path, trx_path))

    def test_require_postgres_rejects_a_run_with_failed_tests(self) -> None:
        coverage_path = self.write_coverage_report()
        trx_path = self.write_xml(
            '<TestRun><Results><UnitTestResult outcome="Passed" />'
            '<UnitTestResult outcome="Failed" /></Results></TestRun>'
        )

        self.assertEqual(1, self.run_report(coverage_path, trx_path))

    def test_require_postgres_rejects_an_empty_filtered_run(self) -> None:
        coverage_path = self.write_coverage_report()
        trx_path = self.write_xml('<TestRun><Results /></TestRun>')

        self.assertEqual(1, self.run_report(coverage_path, trx_path))

    def test_require_postgres_requires_a_trx_input(self) -> None:
        coverage_path = self.write_coverage_report()
        stderr = io.StringIO()
        with redirect_stderr(stderr):
            exit_code = main([
                "--label", "PostgreSQL",
                "--coverage-glob", coverage_path,
                "--require-postgres",
            ])

        self.assertEqual(1, exit_code)
        self.assertIn("потрібно вказати --trx-glob", stderr.getvalue())

    def test_minimum_line_coverage_passes_at_threshold(self) -> None:
        path = self.write_xml(
            '<coverage lines-covered="75" lines-valid="100">'
            '<package name="Watcher.EdgeAgent.Core" line-rate="0.75" branch-rate="0.5" />'
            '</coverage>'
        )

        self.assertEqual(0, self.run_coverage_threshold(path, 75))

    def test_minimum_line_coverage_rejects_a_report_below_threshold(self) -> None:
        path = self.write_xml(
            '<coverage lines-covered="74" lines-valid="100">'
            '<package name="Watcher.EdgeAgent.Core" line-rate="0.74" branch-rate="0.5" />'
            '</coverage>'
        )

        self.assertEqual(1, self.run_coverage_threshold(path, 75))

    def test_minimum_module_line_coverage_passes_when_every_module_meets_threshold(self) -> None:
        path = self.write_xml(
            '<coverage><packages>'
            '<package name="Watcher.Api" line-rate="0.6" branch-rate="0.5" />'
            '<package name="Watcher.Infrastructure" line-rate="0.8" branch-rate="0.5" />'
            '</packages></coverage>'
        )

        self.assertEqual(0, self.run_module_coverage_threshold(path, 60))

    def test_minimum_module_line_coverage_fails_when_one_module_is_below_threshold(self) -> None:
        path = self.write_xml(
            '<coverage><packages>'
            '<package name="Watcher.Api" line-rate="0.59" branch-rate="0.5" />'
            '<package name="Watcher.Infrastructure" line-rate="0.8" branch-rate="0.5" />'
            '</packages></coverage>'
        )

        stderr = io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(stderr):
            exit_code = main([
                "--label", "Unit",
                "--coverage-glob", path,
                "--minimum-module-line-coverage", "60",
            ])

        self.assertEqual(1, exit_code)
        self.assertIn("Watcher.Api: 59.0%", stderr.getvalue())
        self.assertIn("нижче порога 60.0%", stderr.getvalue())

    def test_rejects_multiple_coverage_reports_for_the_same_module(self) -> None:
        lower_coverage = self.write_xml(
            '<coverage><package name="Watcher.Api" line-rate="0.4" branch-rate="0.5" /></coverage>'
        )
        higher_coverage = self.write_xml(
            '<coverage><package name="Watcher.Api" line-rate="0.9" branch-rate="0.5" /></coverage>'
        )
        stderr = io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(stderr):
            exit_code = main([
                "--label", "Unit",
                "--coverage-glob", lower_coverage,
                "--coverage-glob", higher_coverage,
                "--minimum-module-line-coverage", "60",
            ])

        self.assertEqual(1, exit_code)
        self.assertIn("є у кількох звітах покриття", stderr.getvalue())

    def test_deduplicates_identical_cobertura_report_copies(self) -> None:
        report = Path(self.write_coverage_report())
        duplicate = report.with_name("coverage-copy.xml")
        duplicate.write_bytes(report.read_bytes())
        stdout = io.StringIO()
        stderr = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            exit_code = main([
                "--label", "Unit",
                "--coverage-glob", str(report),
                "--coverage-glob", str(duplicate),
                "--minimum-module-line-coverage", "70",
            ])

        self.assertEqual(0, exit_code, stderr.getvalue())
        self.assertEqual(1, stdout.getvalue().count("`Watcher.Api`"))

    def test_ignores_duplicate_report_when_calculating_total_coverage(self) -> None:
        covered_module = self.write_xml(
            '<coverage lines-covered="9" lines-valid="10">'
            '<package name="Watcher.Api" line-rate="0.9" branch-rate="0.5" />'
            '</coverage>'
        )
        uncovered_module = self.write_xml(
            '<coverage lines-covered="0" lines-valid="100">'
            '<package name="Watcher.Domain" line-rate="0" branch-rate="0" />'
            '</coverage>'
        )
        duplicate = Path(covered_module).with_name("covered-copy.xml")
        duplicate.write_bytes(Path(covered_module).read_bytes())
        stderr = io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(stderr):
            exit_code = main([
                "--label", "Combined",
                "--coverage-glob", covered_module,
                "--coverage-glob", str(duplicate),
                "--coverage-glob", uncovered_module,
                "--minimum-line-coverage", "10",
            ])

        self.assertEqual(1, exit_code)
        self.assertIn("Покриття рядків нижче", stderr.getvalue())

    def run_coverage_threshold(self, path: str, minimum: float) -> int:
        stderr = io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(stderr):
            exit_code = main([
                "--label", "Edge Agent",
                "--coverage-glob", path,
                "--minimum-line-coverage", str(minimum),
            ])
        if exit_code:
            self.assertIn("Покриття рядків нижче", stderr.getvalue())
        return exit_code

    def run_module_coverage_threshold(self, path: str, minimum: float) -> int:
        stderr = io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(stderr):
            exit_code = main([
                "--label", "Unit",
                "--coverage-glob", path,
                "--minimum-module-line-coverage", str(minimum),
            ])
        if exit_code:
            self.assertIn("Покриття production-модулів нижче", stderr.getvalue())
        return exit_code

    def run_report(self, coverage_path: str, trx_path: str) -> int:
        stderr = io.StringIO()
        stdout = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            exit_code = main([
                "--label", "PostgreSQL",
                "--coverage-glob", coverage_path,
                "--trx-glob", trx_path,
                "--require-postgres",
            ])

        if exit_code:
            self.assertIn("Помилка звіту CI", stderr.getvalue())
        return exit_code


if __name__ == "__main__":
    unittest.main()
