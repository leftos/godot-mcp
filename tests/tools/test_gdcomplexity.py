"""Tests for tools/gdcomplexity.py: each construct counted once, lengths, and the baseline."""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from types import ModuleType

import pytest

TOOL_PATH = Path(__file__).resolve().parents[2] / "tools" / "gdcomplexity.py"


def _load_tool() -> ModuleType:
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("gdcomplexity", TOOL_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


gd = _load_tool()


def _complexity(body: str, header: str = "func f(a, b, c):") -> int:
    measures = gd.measure_source(f"extends Node\n\n\n{header}\n{body}")
    assert len(measures) == 1
    return measures[0].complexity


@pytest.mark.parametrize(
    ("body", "expected"),
    [
        pytest.param("\tpass\n", 1, id="straight line"),
        pytest.param("\tif a:\n\t\tpass\n", 2, id="if"),
        pytest.param("\tif a:\n\t\tpass\n\telse:\n\t\tpass\n", 2, id="else adds nothing"),
        pytest.param("\tif a:\n\t\tpass\n\telif b:\n\t\tpass\n\telif c:\n\t\tpass\n", 4, id="each elif"),
        pytest.param("\tfor x in a:\n\t\tpass\n", 2, id="for"),
        pytest.param("\tfor x: int in a:\n\t\tpass\n", 2, id="typed for"),
        pytest.param("\twhile a:\n\t\tpass\n", 2, id="while"),
        pytest.param("\tmatch a:\n\t\t1:\n\t\t\tpass\n", 1, id="match with one branch"),
        pytest.param("\tmatch a:\n\t\t1, 2:\n\t\t\tpass\n\t\t3:\n\t\t\tpass\n\t\t_:\n\t\t\tpass\n", 3, id="match branches beyond the first"),
        pytest.param("\tmatch a:\n\t\tvar x when x > 1:\n\t\t\tpass\n\t\t_:\n\t\t\tpass\n", 3, id="when guard"),
        pytest.param("\treturn b if a else c\n", 2, id="ternary"),
        pytest.param("\treturn b if a else c if b else a\n", 3, id="nested ternary"),
        pytest.param("\treturn a and b\n", 2, id="and"),
        pytest.param("\treturn a && b\n", 2, id="&&"),
        pytest.param("\treturn a or b\n", 2, id="or"),
        pytest.param("\treturn a || b\n", 2, id="||"),
        pytest.param("\treturn a and b or c and not a\n", 4, id="mixed boolean operators"),
        pytest.param("\treturn not a\n", 1, id="not adds nothing"),
        pytest.param("\tif a and b:\n\t\treturn c if a else b\n", 4, id="condition operators and ternary"),
        pytest.param("\tvar g := func(): return a\n\tg.call()\n", 1, id="lambda without branches"),
        pytest.param("\tvar g := func(x):\n\t\tif x:\n\t\t\treturn a or b\n\tg.call(1)\n", 3, id="lambda counts in its function"),
    ],
)
def test_counts_each_construct_once(body: str, expected: int) -> None:
    assert _complexity(body) == expected


def test_static_function_is_measured() -> None:
    assert _complexity("\treturn a or b\n", header="static func f(a, b):") == 2


def test_names_inner_class_functions_and_property_accessors() -> None:
    code = "\n".join(
        [
            "extends Node",
            "var p: int:",
            "\tset(value):",
            "\t\tp = value if value > 0 else 0",
            "\tget:",
            "\t\treturn p",
            "",
            "",
            "class Inner:",
            "\tfunc g():",
            "\t\tpass",
            "",
        ]
    )
    measures = {measure.name: measure for measure in gd.measure_source(code)}
    assert set(measures) == {"p.set", "p.get", "Inner.g"}
    assert measures["p.set"].complexity == 2
    assert measures["Inner.g"].line == 10


def test_lambda_outside_a_function_is_not_counted() -> None:
    code = "extends Node\nvar g := func(x): return x if x else 0\n\n\nfunc f():\n\tpass\n"
    assert [(measure.name, measure.complexity) for measure in gd.measure_source(code)] == [("f", 1)]


def test_length_runs_from_func_line_to_last_code_line() -> None:
    code = "\n".join(
        [
            "extends Node",
            "",
            "## A doc comment, not counted.",
            "func f():",
            "\tvar x := 1",
            "",
            "\t# a comment inside",
            "\treturn x",
            "",
            "",
            "## The next function's doc comment.",
            "func g(): return 1",
            "",
        ]
    )
    lengths = {measure.name: (measure.line, measure.lines) for measure in gd.measure_source(code)}
    assert lengths == {"f": (4, 5), "g": (12, 1)}


def test_length_includes_a_multiline_string_that_leaves_the_indent() -> None:
    code = 'extends Node\n\n\nfunc f():\n\treturn """one\ntwo\nthree"""\n'
    assert gd.measure_source(code)[0].lines == 4


def _write(tmp_path: Path, name: str, code: str) -> Path:
    path = tmp_path / name
    path.write_text(code, encoding="utf-8", newline="\n")
    return path


def _branchy(name: str, branches: int) -> str:
    lines = [f"func {name}(a):"]
    for index in range(branches):
        lines += [f"\tif a == {index}:", "\t\tpass"]
    return "\n".join(lines) + "\n"


def _long(name: str, statements: int) -> str:
    return "\n".join([f"func {name}():", *["\tpass"] * statements]) + "\n"


def test_reports_complexity_and_length_violations(tmp_path: Path) -> None:
    path = _write(tmp_path, "a.gd", "extends Node\n\n\n" + _branchy("busy", 8) + "\n\n" + _branchy("fine", 7) + "\n\n" + _long("long", 100))
    problems = gd.check([path], set())
    assert problems == [
        f"{path.as_posix()}:4: busy complexity 9 > 8",
        f"{path.as_posix()}:40: long 101 lines > 100",
    ]


def test_baseline_lets_a_listed_violation_through(tmp_path: Path) -> None:
    path = _write(tmp_path, "a.gd", "extends Node\n\n\n" + _branchy("busy", 8))
    assert gd.check([path], {f"{path.resolve().as_posix()}:busy"}) == []


def test_baseline_entry_that_no_longer_violates_is_refused(tmp_path: Path) -> None:
    path = _write(tmp_path, "a.gd", "extends Node\n\n\n" + _branchy("fine", 7))
    entry = f"{path.resolve().as_posix()}:fine"
    assert gd.check([path], {entry}) == [f"{entry}: in the baseline but within the limits; delete it from gdcomplexity-baseline.txt"]


def test_baseline_entry_for_a_missing_function_is_refused(tmp_path: Path) -> None:
    path = _write(tmp_path, "a.gd", "extends Node\n\n\n" + _branchy("fine", 1))
    entry = f"{path.resolve().as_posix()}:gone"
    assert len(gd.check([path], {entry})) == 1


def test_baseline_entry_for_a_file_not_checked_is_ignored(tmp_path: Path) -> None:
    path = _write(tmp_path, "a.gd", "extends Node\n\n\n" + _branchy("fine", 1))
    assert gd.check([path], {"bridge/other.gd:busy"}) == []


def test_read_baseline_drops_comments_and_blank_lines(tmp_path: Path) -> None:
    path = _write(tmp_path, "baseline.txt", "# header\n\nbridge/a.gd:f  # fix later\n  bridge/b.gd:g\n")
    assert gd.read_baseline(path) == {"bridge/a.gd:f", "bridge/b.gd:g"}


def test_repo_baseline_names_functions_in_the_repo_root_form() -> None:
    for entry in gd.read_baseline(gd.BASELINE_PATH):
        file_part, _, function = entry.rpartition(":")
        assert (gd.REPO_ROOT / file_part).is_file(), entry
        assert function, entry


def test_main_exits_1_on_a_violation_and_0_without(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    busy = _write(tmp_path, "busy.gd", "extends Node\n\n\n" + _branchy("busy", 8))
    fine = _write(tmp_path, "fine.gd", "extends Node\n\n\n" + _branchy("fine", 7))
    assert gd.main([str(busy)]) == 1
    assert "busy complexity 9 > 8" in capsys.readouterr().out
    assert gd.main([str(fine)]) == 0
