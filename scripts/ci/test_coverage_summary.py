import tempfile
import unittest
from pathlib import Path

from coverage_summary import read_coverage, read_postgres_results


class CoverageSummaryTests(unittest.TestCase):
    def write_xml(self, content: str) -> str:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "report.xml"
        path.write_text(content, encoding="utf-8")
        return str(path)

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

    def test_counts_passed_and_skipped_postgres_tests_from_trx(self) -> None:
        path = self.write_xml(
            '<TestRun><TestDefinitions>'
            '<UnitTest id="1"><TestMethod className="Watcher.Api.IntegrationTests.PostgresAlertCommandApiTests" /></UnitTest>'
            '<UnitTest id="2"><TestMethod className="Watcher.Api.IntegrationTests.PostgresAlertCommandApiTests" /></UnitTest>'
            '<UnitTest id="3"><TestMethod className="Watcher.Api.IntegrationTests.HealthEndpointTests" /></UnitTest>'
            '</TestDefinitions><Results>'
            '<UnitTestResult testId="1" outcome="Passed" />'
            '<UnitTestResult testId="2" outcome="NotExecuted" />'
            '<UnitTestResult testId="3" outcome="Passed" />'
            '</Results></TestRun>'
        )

        results = read_postgres_results(path)

        self.assertEqual(1, results.passed)
        self.assertEqual(1, results.skipped)
        self.assertEqual(0, results.failed)
        self.assertEqual(2, results.total)

    def test_counts_namespaced_trx_by_test_name_when_definition_has_no_class(self) -> None:
        path = self.write_xml(
            '<TestRun xmlns="urn:trx"><Results>'
            '<UnitTestResult testName="Watcher.Api.IntegrationTests.PostgresAlertCommandApiTests.Create" outcome="Passed" />'
            '</Results></TestRun>'
        )

        self.assertEqual(1, read_postgres_results(path).passed)


if __name__ == "__main__":
    unittest.main()
