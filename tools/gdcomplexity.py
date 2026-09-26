"""Checks every GDScript function against the repo's cyclomatic complexity and length limits.

Usage: uv run --with "gdtoolkit>=4,<5" tools/gdcomplexity.py [--no-baseline] <file.gd>...

The limits are the ones CA1502 and the house rules hold C# to: complexity at most 8 and at most
100 lines per function. Each file is parsed with gdtoolkit's GDScript parser.

Complexity, counted as close to CA1502's definition for C# as GDScript allows, is 1 plus one for
each of these in the function's body:

- an ``if`` and each ``elif`` (an ``else`` adds nothing);
- a ``for`` and a ``while``;
- each ``match`` branch beyond the first, and each ``when`` guard on a branch (as a C# ``case``
  label and its ``when`` clause each count);
- a ternary ``x if c else y``;
- each ``and``/``&&`` and ``or``/``||`` operator (``not`` adds nothing).

A lambda is not a function of its own: whatever it contains counts toward the function it is
written in, so a lambda with no branches adds nothing. A lambda outside any function (in a
member's initializer) is not counted. A property's ``set`` and ``get`` are functions named
``<property>.set`` and ``<property>.get``; a function of an inner class is ``<Class>.<func>``.

Length is the lines from the ``func`` line to the function's last line of code, the blank and
comment lines between them included; the doc comment above it is not.

Violations print as ``<file>:<line>: <func> complexity <n> > 8`` or ``<file>:<line>: <func>
<n> lines > 100`` and the exit status is 1; with none it is 0.

The baseline (``tools/gdcomplexity-baseline.txt``) lists known violations to let through, one
``<file>:<function>`` a line with the file relative to the repo root, ``#`` starting a comment.
An entry for a file being checked whose function no longer violates, or no longer exists, is an
error itself, so the baseline can only shrink. ``--no-baseline`` reports every violation.
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from pathlib import Path

from gdtoolkit.parser import parser
from lark import Token, Tree

MAX_COMPLEXITY = 8
MAX_LINES = 100
REPO_ROOT = Path(__file__).resolve().parent.parent
BASELINE_PATH = REPO_ROOT / "tools" / "gdcomplexity-baseline.txt"

_BRANCH_RULES = frozenset({"if_branch", "elif_branch", "while_stmt", "for_stmt", "for_stmt_typed"})
_TERNARY_RULES = frozenset({"test_expr", "asless_test_expr"})
_BOOLEAN_RULES = frozenset({"or_test", "asless_or_test", "and_test", "asless_and_test"})
_BOOLEAN_OPERATORS = frozenset({"and", "&&", "or", "||"})
_MATCH_BRANCH_RULES = frozenset({"match_branch", "guarded_match_branch"})
_ACCESSOR_RULES = frozenset({"property_custom_setter", "property_custom_getter"})


@dataclass(frozen=True)
class FunctionMeasure:
    """One function's name, the line its declaration starts on, its complexity and its length."""

    name: str
    line: int
    complexity: int
    lines: int


def complexity_of(node: Tree) -> int:
    """The cyclomatic complexity of a function node, as the module docstring defines it."""
    return 1 + _decisions_in(node)


def _decisions_in(node: Tree) -> int:
    count = 0
    for subtree in node.iter_subtrees():
        count += _decisions_at(subtree)
    return count


def _decisions_at(node: Tree) -> int:
    if node.data in _BRANCH_RULES or node.data == "guarded_match_branch":
        return 1
    if node.data == "match_stmt":
        branches = [child for child in node.children if isinstance(child, Tree) and child.data in _MATCH_BRANCH_RULES]
        return max(0, len(branches) - 1)
    if node.data in _TERNARY_RULES:
        return 1 if _has_token(node, "if") else 0
    if node.data in _BOOLEAN_RULES:
        return sum(1 for child in node.children if isinstance(child, Token) and child.value in _BOOLEAN_OPERATORS)
    return 0


def _has_token(node: Tree, value: str) -> bool:
    return any(isinstance(child, Token) and child.value == value for child in node.children)


def measure_source(code: str) -> list[FunctionMeasure]:
    """Every function in a GDScript source, in the order they are declared."""
    tree = parser.parse(code, gather_metadata=True)
    source_lines = code.splitlines()
    measures: list[FunctionMeasure] = []
    _collect(tree, "", source_lines, measures)
    return measures


def _collect(node: Tree, prefix: str, source_lines: list[str], into: list[FunctionMeasure]) -> None:
    property_name = ""
    for child in node.children:
        if not isinstance(child, Tree):
            continue
        if child.data == "class_var_stmt":
            property_name = _first_name(child)
        elif child.data == "class_def":
            _collect(child, f"{prefix}{_first_name(child)}.", source_lines, into)
        elif child.data == "property_body_def":
            _collect_accessors(child, f"{prefix}{property_name}.", source_lines, into)
        elif child.data in ("func_def", "static_func_def"):
            func = child if child.data == "func_def" else _only_subtree(child, "func_def")
            name = prefix + _first_name(_only_subtree(func, "func_header"))
            into.append(_measure(func, name, child.meta.line, source_lines))


def _collect_accessors(node: Tree, prefix: str, source_lines: list[str], into: list[FunctionMeasure]) -> None:
    for child in node.children:
        if isinstance(child, Tree) and child.data in _ACCESSOR_RULES:
            name = prefix + ("set" if child.data == "property_custom_setter" else "get")
            into.append(_measure(child, name, child.meta.line, source_lines))


def _measure(func: Tree, name: str, line: int, source_lines: list[str]) -> FunctionMeasure:
    last = _last_line(func, line, source_lines)
    return FunctionMeasure(name=name, line=line, complexity=complexity_of(func), lines=last - line + 1)


def _last_line(func: Tree, line: int, source_lines: list[str]) -> int:
    """The function's last line of code: the last line before the next one indented no deeper than
    its declaration, or the end of its last token when that is later (a multi-line string)."""
    header = source_lines[line - 1]
    indent = len(header) - len(header.lstrip())
    last = line
    for number in range(line + 1, len(source_lines) + 1):
        text = source_lines[number - 1]
        stripped = text.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if len(text) - len(text.lstrip()) <= indent:
            break
        last = number
    token_ends = [token.end_line for token in func.scan_values(lambda value: isinstance(value, Token))]
    return max([last, *(end for end in token_ends if end is not None)])


def _first_name(node: Tree) -> str:
    for child in node.children:
        if isinstance(child, Token):
            return str(child.value)
        if isinstance(child, Tree):
            return _first_name(child)
    return "<anonymous>"


def _only_subtree(node: Tree, rule: str) -> Tree:
    for child in node.children:
        if isinstance(child, Tree) and child.data == rule:
            return child
    raise ValueError(f"no {rule} under {node.data} at line {node.meta.line}")


@dataclass(frozen=True)
class Violation:
    """A function over a limit, with the key the baseline names it by."""

    key: str
    message: str


def violations_in(path: Path, display: str, key_path: str) -> list[Violation]:
    """The file's functions over a limit, one violation per limit exceeded."""
    found: list[Violation] = []
    for measure in measure_source(path.read_text(encoding="utf-8")):
        key = f"{key_path}:{measure.name}"
        where = f"{display}:{measure.line}: {measure.name}"
        if measure.complexity > MAX_COMPLEXITY:
            found.append(Violation(key, f"{where} complexity {measure.complexity} > {MAX_COMPLEXITY}"))
        if measure.lines > MAX_LINES:
            found.append(Violation(key, f"{where} {measure.lines} lines > {MAX_LINES}"))
    return found


def read_baseline(path: Path) -> set[str]:
    """The baseline's entries, comments and blank lines dropped."""
    if not path.exists():
        return set()
    entries: set[str] = set()
    for raw in path.read_text(encoding="utf-8").splitlines():
        entry = raw.split("#", 1)[0].strip()
        if entry:
            entries.add(entry)
    return entries


def _key_path(path: Path) -> str:
    resolved = path.resolve()
    try:
        return resolved.relative_to(REPO_ROOT).as_posix()
    except ValueError:
        return resolved.as_posix()


def check(paths: list[Path], baseline: set[str]) -> list[str]:
    """Every problem across the files: violations the baseline does not list, then baseline entries
    for these files that no longer violate."""
    problems: list[str] = []
    checked_files: set[str] = set()
    violating: set[str] = set()
    for path in paths:
        key_path = _key_path(path)
        checked_files.add(key_path)
        for violation in violations_in(path, path.as_posix(), key_path):
            violating.add(violation.key)
            if violation.key not in baseline:
                problems.append(violation.message)
    for entry in sorted(baseline):
        file_part = entry.rsplit(":", 1)[0]
        if file_part in checked_files and entry not in violating:
            problems.append(f"{entry}: in the baseline but within the limits; delete it from {BASELINE_PATH.name}")
    return problems


def main(argv: list[str]) -> int:
    """Checks the files named on the command line; 1 when any problem is found, else 0."""
    arguments = argparse.ArgumentParser(description="GDScript cyclomatic complexity and function length check.")
    arguments.add_argument("files", nargs="+", type=Path)
    arguments.add_argument("--no-baseline", action="store_true", help="report the baseline's violations too")
    options = arguments.parse_args(argv)
    baseline = set() if options.no_baseline else read_baseline(BASELINE_PATH)
    problems = check(options.files, baseline)
    for problem in problems:
        print(problem)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
