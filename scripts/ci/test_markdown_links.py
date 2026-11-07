import tempfile
import unittest
from pathlib import Path

from markdown_links import check_links


class MarkdownLinksTests(unittest.TestCase):
    def test_resolves_relative_links_from_each_markdown_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            (root / "docs" / "adr").mkdir(parents=True)
            target = root / "docs" / "audit.md"
            target.write_text("# Аудит\n", encoding="utf-8")
            (root / "docs" / "adr" / "README.md").write_text(
                "[Аудит](../audit.md)\n", encoding="utf-8"
            )

            file_count, errors = check_links(root)

        self.assertEqual(2, file_count)
        self.assertEqual([], errors)

    def test_reports_missing_relative_targets(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            (root / "docs").mkdir()
            (root / "docs" / "README.md").write_text(
                "[Відсутній документ](adr/missing.md)\n", encoding="utf-8"
            )

            _, errors = check_links(root)

        self.assertEqual(["docs/README.md:1: не знайдено ціль adr/missing.md"], errors)

    def test_ignores_external_fragment_and_code_example_links(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            (root / "README.md").write_text(
                "[Зовнішній](https://example.test/page) [Якір](#section)\n"
                "`[Код](not-a-real-file.md)`\n"
                "```md\n[Приклад](also-not-a-real-file.md)\n```\n",
                encoding="utf-8",
            )

            _, errors = check_links(root)

        self.assertEqual([], errors)


if __name__ == "__main__":
    unittest.main()
