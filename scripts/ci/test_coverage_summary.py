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
