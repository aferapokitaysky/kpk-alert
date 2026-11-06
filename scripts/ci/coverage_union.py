#!/usr/bin/env python3
"""Об'єднує покриті рядки з кількох Cobertura-звітів для CI-порога."""

from __future__ import annotations

import argparse
import glob
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


LineMap = dict[str, dict[tuple[str, int], bool]]


def _xml_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _normalize_filename(filename: str, module_name: str) -> str:
    parts = [part for part in filename.replace("\\", "/").split("/") if part not in {"", "."}]
    try:
        module_index = parts.index(module_name)
    except ValueError:
        if "src" in parts:
            return "/".join(parts[parts.index("src") + 1 :])
        return "/".join(parts)
    return "/".join(parts[module_index:])


def _expand(patterns: list[str]) -> list[str]:
    files = sorted({path for pattern in patterns for path in glob.glob(pattern, recursive=True)})
    if not files:
        raise ValueError(f"Не знайдено Cobertura-звітів: {', '.join(patterns)}")
    return files


def read_line_coverage(files: list[str]) -> LineMap:
    coverage: LineMap = {}
    for file_path in files:
        root = ET.parse(file_path).getroot()
        found_modules = False
        for package in root.iter():
            if _xml_name(package.tag) != "package":
                continue
            module_name = package.get("name", "")
            if not module_name.startswith("Watcher.") or module_name.endswith("Tests"):
                continue

            found_modules = True
            module_lines = coverage.setdefault(module_name, {})
            for class_element in package.iter():
                if _xml_name(class_element.tag) != "class":
                    continue
                filename = class_element.get("filename")
                if not filename:
                    raise ValueError(f"У класу немає filename у {file_path}.")
                normalized_filename = _normalize_filename(filename, module_name)
                for line_element in class_element.iter():
                    if _xml_name(line_element.tag) != "line":
                        continue
                    try:
                        line_number = int(line_element.get("number", ""))
                        hits = int(line_element.get("hits", ""))
                    except ValueError as error:
                        raise ValueError(f"Некоректний рядок покриття у {file_path}.") from error
                    if line_number <= 0 or hits < 0:
                        raise ValueError(f"Некоректний рядок покриття у {file_path}.")

                    key = (normalized_filename, line_number)
                    module_lines[key] = module_lines.get(key, False) or hits > 0

        if not found_modules:
            raise ValueError(f"У {file_path} немає production-модулів Watcher.")
    return coverage


def _summary(coverage: LineMap) -> tuple[str, dict[str, float]]:
    rows = [
        "### Об'єднане покриття рядків за тестовими наборами",
        "",
        "| Модуль | Рядки | Покриття |",
        "| --- | ---: | ---: |",
    ]
    coverage_by_module: dict[str, float] = {}
    total_covered = total_lines = 0
    for module_name in sorted(coverage):
        lines = coverage[module_name]
        if not lines:
            raise ValueError(f"Модуль {module_name} не має виконуваних рядків у звітах.")
        covered = sum(lines.values())
        line_count = len(lines)
        rate = covered / line_count
        coverage_by_module[module_name] = rate
        total_covered += covered
        total_lines += line_count
        rows.append(f"| `{module_name}` | {covered}/{line_count} | {rate:.1%} |")

    if total_lines == 0:
        raise ValueError("Звіти не містять виконуваних production-рядків.")
    rows.extend(
        [
            "",
            f"Разом: **{total_covered}/{total_lines} рядків ({total_covered / total_lines:.1%})**.",
        ]
    )
    return "\n".join(rows), coverage_by_module


def _publish(markdown: str) -> None:
    print(markdown)
    print()
    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_path:
        with Path(summary_path).open("a", encoding="utf-8") as summary:
            summary.write(markdown + "\n\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--coverage-glob", action="append", required=True)
    parser.add_argument("--minimum-module-line-coverage", required=True, type=float)
    parser.add_argument("--required-module", action="append", required=True)
    args = parser.parse_args(argv)

    try:
        minimum = args.minimum_module_line_coverage
        if not 0 <= minimum <= 100:
            raise ValueError("Мінімальне покриття модуля має бути від 0 до 100 відсотків.")

        files = _expand(args.coverage_glob)
        coverage = read_line_coverage(files)
        missing_modules = sorted(set(args.required_module) - set(coverage))
        if missing_modules:
            raise ValueError(
                "У звітах відсутні обов'язкові production-модулі: "
                + ", ".join(missing_modules)
                + "."
            )

        markdown, coverage_by_module = _summary(coverage)
        _publish(markdown)
        below_threshold = [
            (module_name, rate)
            for module_name, rate in sorted(coverage_by_module.items())
            if rate * 100 < minimum
        ]
        print(f"Мінімум покриття кожного production-модуля: {minimum:.1f}%.")
        if below_threshold:
            details = ", ".join(
                f"{module_name}: {rate:.1%}" for module_name, rate in below_threshold
            )
            raise ValueError(
                f"Об'єднане покриття production-модулів нижче порога {minimum:.1f}%: {details}."
            )
    except (ET.ParseError, OSError, ValueError) as error:
        print(f"Помилка об'єднаного покриття CI: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
