#!/usr/bin/env python3
"""Збирає coverage-модулі та результат PostgreSQL-набору для CI summary."""

from __future__ import annotations

import argparse
import glob
import os
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class ModuleCoverage:
    name: str
    line_rate: float
    branch_rate: float


@dataclass(frozen=True)
class PostgresTestResults:
    passed: int
    skipped: int
    failed: int

    @property
    def total(self) -> int:
        return self.passed + self.skipped + self.failed


def _xml_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _rate(value: str | None, field: str, file_path: str) -> float:
    try:
        rate = float(value) if value is not None else -1
    except ValueError as error:
        raise ValueError(f"Некоректне поле {field} у {file_path}.") from error
    if not 0 <= rate <= 1:
        raise ValueError(f"Поле {field} поза діапазоном 0..1 у {file_path}.")
    return rate


def read_coverage(file_path: str) -> list[ModuleCoverage]:
    root = ET.parse(file_path).getroot()
    modules: dict[str, ModuleCoverage] = {}
    for package in root.iter():
        if _xml_name(package.tag) != "package":
            continue
        name = package.get("name", "")
        if not name.startswith("Watcher.") or name.endswith("Tests"):
            continue
        modules[name] = ModuleCoverage(
            name=name,
            line_rate=_rate(package.get("line-rate"), "line-rate", file_path),
            branch_rate=_rate(package.get("branch-rate"), "branch-rate", file_path),
        )

    if not modules:
        raise ValueError(f"У {file_path} немає production-модулів Watcher.")
    return sorted(modules.values(), key=lambda module: module.name)


def read_postgres_results(file_path: str) -> PostgresTestResults:
    root = ET.parse(file_path).getroot()
    class_by_id: dict[str, str] = {}
    for unit_test in root.iter():
        if _xml_name(unit_test.tag) != "UnitTest":
            continue
        test_id = unit_test.get("id")
        if test_id is None:
            continue
        test_method = next(
            (child for child in unit_test.iter() if _xml_name(child.tag) == "TestMethod"),
            None,
        )
        if test_method is not None:
            class_by_id[test_id] = test_method.get("className", "")

    passed = skipped = failed = 0
    for result in root.iter():
        if _xml_name(result.tag) != "UnitTestResult":
            continue
        class_name = class_by_id.get(result.get("testId", ""), "")
        test_name = result.get("testName", "")
        is_postgres_test = class_name.endswith(".PostgresAlertCommandApiTests") or (
            class_name == "PostgresAlertCommandApiTests"
        ) or "PostgresAlertCommandApiTests" in test_name
        if not is_postgres_test:
            continue

        outcome = result.get("outcome", "").lower()
        if outcome == "passed":
            passed += 1
        elif outcome in {"notexecuted", "skipped"}:
            skipped += 1
        else:
            failed += 1

    return PostgresTestResults(passed=passed, skipped=skipped, failed=failed)


def _expand(patterns: list[str], kind: str) -> list[str]:
    files = sorted({path for pattern in patterns for path in glob.glob(pattern, recursive=True)})
    if not files:
        raise ValueError(f"Не знайдено файлів {kind}: {', '.join(patterns)}")
    return files


def _coverage_markdown(label: str, files: list[str]) -> str:
    coverage_by_name: dict[str, ModuleCoverage] = {}
    for file_path in files:
        for module in read_coverage(file_path):
            coverage_by_name[module.name] = module

    rows = [
        f"### Покриття рядків і гілок: {label}",
        "",
        "| Модуль | Рядки | Гілки |",
        "| --- | ---: | ---: |",
    ]
    rows.extend(
        f"| `{module.name}` | {module.line_rate:.1%} | {module.branch_rate:.1%} |"
        for module in sorted(coverage_by_name.values(), key=lambda value: value.name)
    )
    return "\n".join(rows)


def _postgres_markdown(label: str, results: PostgresTestResults) -> str:
    return "\n".join(
        [
            f"### Виконання PostgreSQL-тестів: {label}",
            "",
            f"Пройшло: **{results.passed}** · пропущено: **{results.skipped}** · "
            f"помилок: **{results.failed}** · разом: **{results.total}**.",
        ]
    )


def _publish(markdown: str) -> None:
    print(markdown)
    print()
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with Path(summary_path).open("a", encoding="utf-8") as summary:
            summary.write(markdown + "\n\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--label", required=True)
    parser.add_argument("--coverage-glob", action="append", required=True)
    parser.add_argument("--trx-glob", action="append", default=[])
    parser.add_argument("--require-postgres", action="store_true")
    args = parser.parse_args(argv)

    try:
        coverage_files = _expand(args.coverage_glob, "Cobertura")
        _publish(_coverage_markdown(args.label, coverage_files))

        if args.trx_glob:
            trx_files = _expand(args.trx_glob, "TRX")
            results = PostgresTestResults(passed=0, skipped=0, failed=0)
            for file_path in trx_files:
                current = read_postgres_results(file_path)
                results = PostgresTestResults(
                    passed=results.passed + current.passed,
                    skipped=results.skipped + current.skipped,
                    failed=results.failed + current.failed,
                )
            _publish(_postgres_markdown(args.label, results))
            print(
                f"PostgreSQL tests: passed={results.passed}, skipped={results.skipped}, "
                f"failed={results.failed}, total={results.total}"
            )
            if args.require_postgres and (results.total == 0 or results.passed == 0):
                raise ValueError("TRX не підтверджує виконання жодного PostgreSQL-тесту.")
            if args.require_postgres and (results.skipped > 0 or results.failed > 0):
                raise ValueError("PostgreSQL-набір має пропущені або невдалі тести.")
    except (ET.ParseError, OSError, ValueError) as error:
        print(f"Помилка звіту CI: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
