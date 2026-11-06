import io
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

from coverage_union import main


class CoverageUnionTests(unittest.TestCase):
    def write_report(self, package: str, filename: str, line_entries: str) -> str:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "coverage.cobertura.xml"
        path.write_text(
            "<coverage><packages>"
            f'<package name="{package}"><classes><class name="Example" filename="{filename}">'
            f"<methods><method name=\"Run\"><lines>{line_entries}</lines></method></methods>"
            "</class></classes></package></packages></coverage>",
            encoding="utf-8",
        )
        return str(path)

    def test_unions_line_hits_across_unit_and_postgres_reports(self) -> None:
        unit_report = self.write_report(
            "Watcher.Api",
            r"C:\runner\src\Watcher.Api\Controllers\HealthController.cs",
            '<line number="10" hits="1" /><line number="11" hits="0" />',
        )
        postgres_report = self.write_report(
            "Watcher.Api",
            "src/Watcher.Api/Controllers/HealthController.cs",
            '<line number="10" hits="0" /><line number="11" hits="1" />',
        )
        linux_report = self.write_report(
            "Watcher.Api",
            "/home/runner/work/kgta-alert/src/Watcher.Api/Controllers/HealthController.cs",
            '<line number="10" hits="0" /><line number="11" hits="0" />',
        )
        stdout = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(io.StringIO()):
            exit_code = main([
                "--coverage-glob", unit_report,
                "--coverage-glob", postgres_report,
                "--coverage-glob", linux_report,
                "--minimum-module-line-coverage", "100",
                "--required-module", "Watcher.Api",
            ])

        self.assertEqual(0, exit_code)
        self.assertIn("2/2 | 100.0%", stdout.getvalue())

    def test_requires_each_production_module_to_meet_threshold(self) -> None:
        api_report = self.write_report(
            "Watcher.Api",
            "Watcher.Api/Controllers/HealthController.cs",
            '<line number="10" hits="1" />',
        )
        domain_report = self.write_report(
            "Watcher.Domain",
            "Watcher.Domain/Entities/AlertCommand.cs",
            '<line number="20" hits="1" /><line number="21" hits="0" />',
        )
        stderr = io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(stderr):
            exit_code = main([
                "--coverage-glob", api_report,
                "--coverage-glob", domain_report,
                "--minimum-module-line-coverage", "70",
                "--required-module", "Watcher.Api",
                "--required-module", "Watcher.Domain",
            ])

        self.assertEqual(1, exit_code)
        self.assertIn("Watcher.Domain: 50.0%", stderr.getvalue())

    def test_fails_when_required_module_has_no_report(self) -> None:
        report = self.write_report(
            "Watcher.Api",
            "Watcher.Api/Controllers/HealthController.cs",
            '<line number="10" hits="1" />',
        )
        stderr = io.StringIO()
        with redirect_stdout(io.StringIO()), redirect_stderr(stderr):
            exit_code = main([
                "--coverage-glob", report,
                "--minimum-module-line-coverage", "70",
                "--required-module", "Watcher.Api",
                "--required-module", "Watcher.Infrastructure",
            ])

        self.assertEqual(1, exit_code)
        self.assertIn("Watcher.Infrastructure", stderr.getvalue())

    def test_deduplicates_same_source_line_reported_by_multiple_methods(self) -> None:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        report = Path(directory.name) / "coverage.cobertura.xml"
        report.write_text(
            '<coverage><packages><package name="Watcher.Api"><classes>'
            '<class name="Example" filename="Watcher.Api/Controllers/HealthController.cs">'
            '<methods><method name="First"><lines><line number="10" hits="1" /></lines></method>'
            '<method name="Second"><lines><line number="10" hits="0" /></lines></method></methods>'
            '</class></classes></package></packages></coverage>',
            encoding="utf-8",
        )
        stdout = io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(io.StringIO()):
            exit_code = main([
                "--coverage-glob", str(report),
                "--minimum-module-line-coverage", "100",
                "--required-module", "Watcher.Api",
            ])

        self.assertEqual(0, exit_code)
        self.assertIn("1/1 | 100.0%", stdout.getvalue())


if __name__ == "__main__":
    unittest.main()
