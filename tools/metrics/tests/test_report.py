from __future__ import annotations

import json
import sqlite3
from pathlib import Path
from typing import Any

import pytest

import report

# One game minute in simulation frames, rounded; enough for the figures here.
MINUTE = report.FRAMES_PER_GAME_MINUTE


def write(folder: Path, table: str, records: list[dict[str, Any]]) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    with (folder / f"{table}.jsonl").open("a", encoding="utf-8") as file:
        for record in records:
            file.write(json.dumps({"s": folder.name, **record}) + "\n")


def interval(train: int, state: str, start: float, minutes: float, **extra: Any) -> dict[str, Any]:
    """An interval of train from game minute start, lasting minutes."""
    f = int(start * MINUTE)
    return {
        "train": train, "v": 1, "state": state, "hold": None, "active": True, "released": False, "by": 0,
        "f": f, "until": f + int(minutes * MINUTE), "sec": 0.0, "dist": 0.0, "truncated": False,
        "lane": 1, "x": 0.0, "z": 0.0, "junction": False, "station": 0, "advice": 0.0,
        **extra,
    }


def session(root: Path, name: str, intervals: list[dict[str, Any]], events: list[dict[str, Any]] | None = None) -> Path:
    folder = root / name
    write(folder, "session", [{"started": "2026-09-28 10:00:00", "city": "Test", "build": "abc1234",
                               "frame": 0, "active": True}])
    write(folder, "interval", intervals)
    if events:
        write(folder, "event", events)
    return folder


def figures_of(root: Path, skip_minutes: float = 0.0) -> list[dict[str, Any]]:
    con = report.load(root)
    report.prepare(con, skip_minutes)
    return report.figures(con, [s["session"] for s in report.sessions(con)])


def test_figures_add_up_what_trains_did(tmp_path: Path) -> None:
    # Train 1: runs 60 min (30 km), stops behind a train on a turnout for
    # 30 min, runs 30 min. Train 2: boards 60 min, held by the dispatcher
    # 20 min, runs 40 min. Four train hours in all.
    session(tmp_path, "a", [
        interval(1, "Running", 0, 60, dist=30000.0),
        interval(1, "TrainAhead", 60, 30, junction=True, by=2),
        interval(1, "Running", 90, 30),
        interval(2, "Boarding", 0, 60, station=9),
        interval(2, "AtSignal", 60, 20, hold="TrackHeld", station=9),
        interval(2, "Running", 80, 40),
    ])
    (row,) = figures_of(tmp_path)
    assert row["train_hours"] == pytest.approx(4.0, abs=0.01)
    assert row["standing_min_h"] == pytest.approx(50 / 4, abs=0.1)
    # Only train 1 came to a stop from running; train 2 was held on leaving
    # the platform.
    assert row["stops_h"] == pytest.approx(1 / 4, abs=0.01)
    assert row["junction_min_h"] == pytest.approx(30 / 4, abs=0.1)
    assert row["platform_blocked_min_h"] == pytest.approx(20 / 4, abs=0.1)
    # Train 1 stood behind train 2 while the dispatcher was on and let it run.
    assert row["mismatch_min_h"] == pytest.approx(30 / 4, abs=0.1)
    assert row["held_pct"] == pytest.approx(100 * 20 / 240, abs=0.1)
    assert row["km_h"] == pytest.approx(30 / 4, abs=0.1)


def test_a_train_the_dispatcher_let_go_is_no_mismatch(tmp_path: Path) -> None:
    session(tmp_path, "a", [
        interval(1, "Running", 0, 10),
        interval(1, "TrainAhead", 10, 10, released=True),
        interval(1, "Running", 20, 10),
    ])
    (row,) = figures_of(tmp_path)
    assert row["mismatch_min_h"] == 0


def test_closing_up_on_the_train_ahead_is_standing_but_no_mismatch(tmp_path: Path) -> None:
    session(tmp_path, "a", [
        interval(1, "Running", 0, 40),
        interval(1, "Queued", 40, 20, by=2),
    ])
    (row,) = figures_of(tmp_path)
    assert row["mismatch_min_h"] == 0
    assert row["game_pct"] == 0
    assert row["queued_pct"] == pytest.approx(100 * 20 / 60, abs=0.1)
    assert row["standing_min_h"] == pytest.approx(20, abs=0.1)


def test_modes_are_reported_apart(tmp_path: Path) -> None:
    session(tmp_path, "a", [
        interval(1, "Running", 0, 60, active=False),
        interval(1, "TrainAhead", 60, 60, active=False),
        interval(1, "Running", 120, 60),
    ], events=[{"f": int(130 * MINUTE), "type": "circle", "active": True, "train": 1, "v": 1, "trains": [1, 2]}])
    off, on = figures_of(tmp_path)
    assert (off["active"], on["active"]) == (False, True)
    assert off["standing_min_h"] == pytest.approx(30, abs=0.1)
    assert on["standing_min_h"] == 0
    assert (off["circles"], on["circles"]) == (0, 1)


def test_the_start_of_a_session_can_be_left_out(tmp_path: Path) -> None:
    session(tmp_path, "a", [
        interval(1, "TrainAhead", 0, 30),
        interval(1, "Running", 30, 60),
    ])
    (row,) = figures_of(tmp_path, skip_minutes=20)
    assert row["train_hours"] == pytest.approx(1.0, abs=0.01)
    assert row["standing_min_h"] == 0


def test_slowing_down_counts_as_failed_when_the_train_stops_after_all(tmp_path: Path) -> None:
    session(tmp_path, "a", [
        interval(1, "Slowed", 0, 5, advice=10.0),
        interval(1, "AtSignal", 5, 5),
        interval(1, "Running", 10, 5),
        interval(1, "Slowed", 15, 5, advice=10.0),
        interval(1, "Running", 20, 5),
    ])
    (row,) = figures_of(tmp_path)
    assert row["slowed_n"] == 2
    assert row["slowed_stopped_pct"] == pytest.approx(50)


def test_a_line_broken_off_by_a_crash_is_skipped(tmp_path: Path) -> None:
    folder = session(tmp_path, "a", [interval(1, "Running", 0, 60)])
    with (folder / "interval.jsonl").open("a", encoding="utf-8") as file:
        file.write('{"s":"a","train":2,"state":"Runn')
    (row,) = figures_of(tmp_path)
    assert row["train_hours"] == pytest.approx(1.0, abs=0.01)


def test_fields_unknown_to_the_report_are_ignored(tmp_path: Path) -> None:
    # A newer mod may write fields the report does not know yet.
    session(tmp_path, "a", [interval(1, "Running", 0, 60, newField="x")])
    (row,) = figures_of(tmp_path)
    assert row["train_hours"] == pytest.approx(1.0, abs=0.01)


def test_the_report_names_sessions_and_places(tmp_path: Path) -> None:
    session(tmp_path, "a", [interval(1, "Running", 0, 60)])
    session(tmp_path, "b", [
        interval(1, "Running", 0, 10),
        interval(1, "TrainAhead", 10, 50, x=1234.0, z=-560.0, junction=True),
    ])
    con = report.load(tmp_path)
    report.prepare(con, 0)
    text = report.render(con, count=3, focus=None, skip_minutes=0)
    assert "b aus" not in text and "b an" in text
    # The stand is listed at its 100 m square.
    assert "1200, -600" in text


def test_sqlite_holds_every_record(tmp_path: Path) -> None:
    session(tmp_path / "m", "a", [interval(1, "Running", 0, 60), interval(2, "Boarding", 0, 60)],
            events=[{"f": 1, "type": "circle", "active": True, "trains": [1, 2]}])
    con = report.load(tmp_path / "m")
    target = tmp_path / "out.sqlite"
    report.export_sqlite(con, target)
    lite = sqlite3.connect(target)
    try:
        assert lite.execute("SELECT count(*) FROM interval").fetchone() == (2,)
        assert lite.execute("SELECT trains FROM event").fetchone() == ("[1, 2]",)
    finally:
        lite.close()
