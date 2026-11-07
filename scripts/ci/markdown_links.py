#!/usr/bin/env python3
"""Перевіряє цілі локальних посилань у файлах Markdown."""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit


INLINE_LINK = re.compile(r"!?\[[^\]]*\]\(([^)]*)\)")
FENCE_START = re.compile(r"^ {0,3}(`{3,}|~{3,})")
INLINE_CODE = re.compile(r"`+[^`]*`+")
IGNORED_DIRECTORIES = {".git", "node_modules", "bin", "obj"}


def _without_code_blocks(content: str) -> str:
    lines: list[str] = []
    fence_character: str | None = None
    fence_length = 0

    for line in content.splitlines():
        fence = FENCE_START.match(line)
        if fence_character is None:
            if fence:
                fence_character = fence.group(1)[0]
                fence_length = len(fence.group(1))
                lines.append("")
            else:
                lines.append(line)
            continue

        if fence and fence.group(1)[0] == fence_character and len(fence.group(1)) >= fence_length:
            fence_character = None
            fence_length = 0
        lines.append("")

    return "\n".join(lines)


def markdown_files(root: Path) -> list[Path]:
    return sorted(
        path
        for path in root.rglob("*.md")
        if not IGNORED_DIRECTORIES.intersection(path.relative_to(root).parts)
    )


def check_links(root: Path) -> tuple[int, list[str]]:
    root = root.resolve()
    files = markdown_files(root)
    errors: list[str] = []

    for markdown_file in files:
        content = markdown_file.read_text(encoding="utf-8")
        searchable = INLINE_CODE.sub("", _without_code_blocks(content))
        for match in INLINE_LINK.finditer(searchable):
            target = match.group(1).strip()
            if target.startswith("<") and ">" in target:
                target = target[1 : target.index(">")]
            else:
                target = target.split(maxsplit=1)[0] if target else ""

            parsed = urlsplit(target)
            if parsed.scheme or parsed.netloc or not parsed.path:
                continue

            relative_target = Path(unquote(parsed.path).lstrip("/"))
            base = root if parsed.path.startswith("/") else markdown_file.parent
            resolved_target = (base / relative_target).resolve()
            if not resolved_target.is_relative_to(root) or not resolved_target.exists():
                line_number = searchable.count("\n", 0, match.start()) + 1
                display_path = markdown_file.relative_to(root)
                errors.append(f"{display_path}:{line_number}: не знайдено ціль {target}")

    return len(files), errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path("."))
    args = parser.parse_args(argv)

    try:
        root = args.root.resolve(strict=True)
        if not root.is_dir():
            raise ValueError("Корінь перевірки має бути каталогом.")
        file_count, errors = check_links(root)
    except (OSError, ValueError) as error:
        print(f"Помилка перевірки Markdown-посилань: {error}", file=sys.stderr)
        return 1

    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1

    print(f"Перевірено Markdown-файлів: {file_count}. Локальні цілі посилань знайдено.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
