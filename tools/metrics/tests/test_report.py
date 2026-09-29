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


def session(
    root: Path,
    name: str,
    intervals: list[dict[str, Any]],
    events: list[dict[str, Any]] | None = None,
    build: str = "abc1234",
    city: str = "Test",
) -> Path:
    folder = root / name
    write(folder, "session", [{"started": "2026-09-28 10:00:00", "city": city, "build": build,
                               "frame": 0, "active": True}])
    write(folder, "interval", intervals)
    if events:
        write(folder, "event", events)
    return folder


def figures_of(root: Path, skip_minutes: float = 0.0, hours: float | None = None) -> list[dict[str, Any]]:
    con = report.load(root)
    report.prepare(con, skip_minutes, hours)
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


def test_only_what_is_on_board_counts_as_transported(tmp_path: Path) -> None:
    # 20 km with 100 passengers, then 10 km empty from the depot: 2000
    # passenger-km in one train hour. A train removed with 50 passengers on
    # board loses them.
    session(tmp_path, "a", [
        interval(1, "Running", 0, 40, dist=20000.0, passengers=100, load=0),
        interval(1, "Running", 40, 20, dist=10000.0, passengers=0, load=0),
    ], events=[{"f": 10, "type": "removed", "active": True, "normal": False, "cause": "Deadlock",
                "passengers": 50, "load": 0},
               {"f": 20, "type": "removed", "active": True, "normal": True, "cause": "Arrived",
                "passengers": 80, "load": 0}])
    (row,) = figures_of(tmp_path)
    assert row["km_h"] == pytest.approx(30, abs=0.1)
    assert row["pkm_h"] == pytest.approx(2000, abs=1)
    assert row["lost_passengers_100"] == pytest.approx(5000, abs=1)


def test_sessions_without_load_show_nothing_rather_than_zero(tmp_path: Path) -> None:
    # Recorded before passengers were; a zero would read as empty trains.
    session(tmp_path, "a", [interval(1, "Running", 0, 60, dist=1000.0)])
    (row,) = figures_of(tmp_path)
    assert row["pkm_h"] is None
    assert row["lost_passengers_100"] is None


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
    # One circle in the one train hour with the dispatcher on.
    assert off["circles_100"] == 0
    assert on["circles_100"] == pytest.approx(100, abs=0.5)


def test_the_start_of_a_session_can_be_left_out(tmp_path: Path) -> None:
    session(tmp_path, "a", [
        interval(1, "TrainAhead", 0, 30),
        interval(1, "Running", 30, 60),
    ])
    (row,) = figures_of(tmp_path, skip_minutes=20)
    assert row["train_hours"] == pytest.approx(1.0, abs=0.01)
    assert row["standing_min_h"] == 0


def test_only_the_first_hours_of_a_session_count(tmp_path: Path) -> None:
    # Two hours of running, then a stand; the first hour holds only running,
    # and the interval reaching past the hour counts up to it.
    session(tmp_path, "a", [
        interval(1, "Running", 0, 120),
        interval(1, "TrainAhead", 120, 60),
    ])
    (row,) = figures_of(tmp_path, hours=1)
    assert row["train_hours"] == pytest.approx(1.0, abs=0.01)
    assert row["standing_min_h"] == 0


def test_the_time_after_each_switch_can_be_left_out(tmp_path: Path) -> None:
    # Off from minute 0, on from minute 60; 30 minutes to settle after each
    # switch. Off counts minutes 30-60, on 90-120. The stand that begins in
    # the settling time counts from its end; the one wholly in it not at all.
    session(tmp_path, "a", [
        interval(1, "TrainAhead", 0, 20, active=False),
        interval(1, "Running", 20, 40, active=False, dist=4000.0),
        interval(1, "AtSignal", 60, 50, hold="TrackHeld"),
        interval(1, "Running", 110, 10),
    ], events=[{"f": 0, "type": "dispatcher", "active": False},
               {"f": int(60 * MINUTE), "type": "dispatcher", "active": True},
               {"f": int(70 * MINUTE), "type": "circle", "active": True, "trains": [1]}])
    con = report.load(tmp_path)
    report.prepare(con, 0, settle_minutes=30)
    off, on = report.figures(con, ["a"])
    assert off["train_hours"] == pytest.approx(0.5, abs=0.01)
    assert off["standing_min_h"] == 0
    # Of the 40 minutes run, the 30 after the settling time: 3 km in half an hour.
    assert off["km_h"] == pytest.approx(6, abs=0.1)
    assert on["train_hours"] == pytest.approx(0.5, abs=0.01)
    assert on["standing_min_h"] == pytest.approx(40, abs=0.2)
    assert on["circles_100"] == 0


def test_a_line_can_be_left_out(tmp_path: Path) -> None:
    # Train 2 runs on a broken line; it and its removal are left out, under
    # the line's name in either language the game was played in.
    folder = session(tmp_path, "a", [
        interval(1, "Running", 0, 60),
        interval(2, "TrainAhead", 0, 60),
        interval(3, "TrainAhead", 0, 60),
    ], events=[{"f": 10, "type": "removed", "active": True, "normal": False, "cause": "NoRoute",
                "train": 2, "v": 1, "passengers": 0, "load": 500}])
    write(folder, "train", [{"f": 0, "train": 1, "v": 1, "kind": "passenger", "line": "Line 1"},
                            {"f": 0, "train": 2, "v": 1, "kind": "cargo", "line": "Güterzugroute 6"},
                            {"f": 0, "train": 3, "v": 1, "kind": "cargo", "line": "Cargo railway route 6"}])
    con = report.load(tmp_path)
    report.prepare(con, 0, exclude_lines=["Güterzugroute 6", "Cargo railway route 6"])
    (row,) = report.figures(con, ["a"])
    assert row["train_hours"] == pytest.approx(1.0, abs=0.01)
    assert row["standing_min_h"] == 0
    assert row["removed_bad_100"] == 0


def test_a_stand_lasts_across_what_the_train_waits_for(tmp_path: Path) -> None:
    # 40 minutes held, then 30 behind a train: one stand of 70 minutes. A
    # later stand of 5 minutes is short.
    session(tmp_path, "a", [
        interval(1, "Running", 0, 10),
        interval(1, "AtSignal", 10, 40, hold="TrackHeld"),
        interval(1, "TrainAhead", 50, 30),
        interval(1, "Running", 80, 10),
        interval(1, "AtSignal", 90, 5, hold="TrackHeld"),
        interval(1, "Running", 95, 5),
    ])
    (row,) = figures_of(tmp_path)
    assert row["longest_min"] == pytest.approx(70, abs=0.1)
    assert row["long60_pct"] == pytest.approx(100 * 70 / 75, abs=0.1)
    assert row["long30_pct"] == pytest.approx(100 * 70 / 75, abs=0.1)


def test_the_sessions_of_one_build_count_as_one_run(tmp_path: Path) -> None:
    session(tmp_path, "a", [interval(1, "Running", 0, 60)], build="old")
    session(tmp_path, "b", [interval(1, "Running", 0, 30), interval(1, "TrainAhead", 30, 30)], build="new")
    session(tmp_path, "c", [interval(1, "Running", 0, 60)], build="new")
    con = report.load(tmp_path)
    report.prepare(con, 0)
    old, new = report.figures(con, ["old", "new"], by="build")
    assert (old["group"], new["group"]) == ("old", "new")
    assert new["sessions"] == 2
    assert new["train_hours"] == pytest.approx(2.0, abs=0.01)
    assert new["standing_min_h"] == pytest.approx(15, abs=0.1)


def test_held_time_goes_to_the_junction_area_of_the_refused_lane(tmp_path: Path) -> None:
    # Two trains held for lanes 7 and 8, both in junction area 3; one for
    # lane 9, in section 5. The network was read twice; the later read
    # counts.
    folder = session(tmp_path, "a", [
        interval(1, "AtSignal", 0, 20, hold="TrackHeld", blockedLane=7),
        interval(2, "AtSignal", 0, 10, hold="TrackHeld", blockedLane=8),
        interval(3, "AtSignal", 0, 5, hold="NoRoomAhead", blockedLane=9),
    ])
    lane = {"kind": "Switch", "twoWay": False, "length": 20.0, "station": 0, "section": -1, "group": -1,
            "x0": 100.0, "z0": 200.0, "x1": 120.0, "z1": 200.0}
    write(folder, "lane", [
        {**lane, "version": 1, "lane": 7, "area": 99},
        {**lane, "version": 2, "lane": 7, "area": 3},
        {**lane, "version": 2, "lane": 8, "area": 3},
        {**lane, "version": 2, "lane": 9, "kind": "Plain", "area": -1, "section": 5},
    ])
    con = report.load(tmp_path)
    report.prepare(con, 0)
    first, second = report.bottlenecks(con, "a")
    assert (first["place"], second["place"]) == ("Weichenbereich 3", "Abschnitt 5")
    assert first["minutes"] == pytest.approx(30, abs=0.1)
    assert first["trains"] == 2
    assert (first["x"], first["z"]) == (110, 200)


def test_other_cities_are_left_out(tmp_path: Path) -> None:
    session(tmp_path, "a", [interval(1, "Running", 0, 60)], city="Here")
    session(tmp_path, "b", [interval(1, "Running", 0, 60)], city="There")
    con = report.load(tmp_path)
    report.prepare(con, 0, city="Here")
    rows = report.figures(con, ["a", "b"])
    assert [row["group"] for row in rows] == ["a"]


def test_slowing_down_counts_as_failed_when_the_train_stops_after_all(tmp_path: Path) -> None:
    session(tmp_path, "a", [
        interval(1, "Slowed", 0, 5, advice=10.0),
        interval(1, "AtSignal", 5, 5),
        interval(1, "Running", 10, 5),
        interval(1, "Slowed", 15, 5, advice=10.0),
        interval(1, "Running", 20, 5),
    ])
    (row,) = figures_of(tmp_path)
    # Two slowdowns in 25 minutes.
    assert row["slowed_h"] == pytest.approx(2 / (25 / 60), abs=0.1)
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
