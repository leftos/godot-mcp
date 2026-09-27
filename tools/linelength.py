"""Reports every line longer than the repo's limit in the files it is given.

Usage: uv run python tools/linelength.py <file>...

The limit is 150 characters, the ``max_line_length = 150`` of ``.editorconfig``. CSharpier wraps C#
code at that width (``.csharpierrc``) but never a comment, so this check catches the comments it
lets through. Prek passes it the C# files a commit touches.

Each file is read as UTF-8 (a byte-order mark is not counted), and a line's length is its
characters without its line ending, ``\\r\\n`` or ``\\n``.

Each long line prints as ``<file>:<line>: <n> characters (limit 150)`` and the exit status is 1;
with none it is 0. A file that cannot be read or is not UTF-8 prints as ``<file>: <reason>`` and
fails the run too.
"""

from __future__ import annotations

import sys
from pathlib import Path

LIMIT = 150


def long_lines(text: str) -> list[tuple[int, int]]:
    """Returns the 1-based number and length of each line of ``text`` longer than ``LIMIT``."""
    found = []
    for number, line in enumerate(text.split("\n"), start=1):
        length = len(line.removesuffix("\r"))
        if length > LIMIT:
            found.append((number, length))
    return found


def check_file(path: str) -> list[str]:
    """Returns one problem line for each long line of the file at ``path``, or one for a file it cannot read."""
    try:
        text = Path(path).read_bytes().decode("utf-8-sig")
    except (OSError, UnicodeDecodeError) as error:
        return [f"{path}: cannot be read as UTF-8 ({error})"]
    return [f"{path}:{number}: {length} characters (limit {LIMIT})" for number, length in long_lines(text)]


def main(argv: list[str]) -> int:
    """Checks each file named in ``argv``, prints every problem, and returns 1 if there was one, else 0."""
    problems = [problem for path in argv for problem in check_file(path)]
    for problem in problems:
        print(problem)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
