"""Report on the Smart Trains metrics: how trains ran, session by session.

Reads the JSON Lines files the mod writes, compares the sessions by a fixed
set of figures, and lists where trains stood longest. Optionally writes all
records into one SQLite file for further queries. README.md describes the
records and the figures.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from collections.abc import Sequence
from pathlib import Path
from typing import Any

import duckdb

# Simulation frames per in-game minute: TimeSystem.kTicksPerDay / (24 * 60).
# Reported times are game time, as the panel shows them.
FRAMES_PER_GAME_MINUTE = 262144 / (24 * 60)

# The fields of each kind of record, as MetricsSystem writes them. Reading
# with fixed columns keeps the tables the same whatever a session contains:
# a missing field is NULL, a field added later is ignored until it is listed
# here.
WAIT_STATES = [
    "Boarding",
    "LatePassengers",
    "TrainAhead",
    "CrossingTrain",
    "OncomingTrain",
    "Signal",
    "Obstacle",
    "RoutePending",
    "Deadlock",
    "Unknown",
    "AtSignal",
]
TABLES: dict[str, dict[str, str]] = {
    "session": {
        "s": "VARCHAR",
        "started": "VARCHAR",
        "city": "VARCHAR",
        "build": "VARCHAR",
        "frame": "UBIGINT",
        "active": "BOOLEAN",
    },
    "train": {
        "s": "VARCHAR",
        "f": "UBIGINT",
        "train": "INTEGER",
        "v": "INTEGER",
        "kind": "VARCHAR",
        "through": "BOOLEAN",
        "returning": "BOOLEAN",
        "line": "VARCHAR",
        "cars": "INTEGER",
        "length": "DOUBLE",
        "maxSpeed": "DOUBLE",
    },
    "interval": {
        "s": "VARCHAR",
        "train": "INTEGER",
        "v": "INTEGER",
        "state": "VARCHAR",
        "hold": "VARCHAR",
        "active": "BOOLEAN",
        "released": "BOOLEAN",
        "by": "INTEGER",
        "f": "UBIGINT",
        "until": "UBIGINT",
        "sec": "DOUBLE",
        "dist": "DOUBLE",
        "truncated": "BOOLEAN",
        "lane": "INTEGER",
        "x": "DOUBLE",
        "z": "DOUBLE",
        "junction": "BOOLEAN",
        "station": "INTEGER",
        "advice": "DOUBLE",
    },
    "event": {
        "s": "VARCHAR",
        "f": "UBIGINT",
        "type": "VARCHAR",
        "active": "BOOLEAN",
        "train": "INTEGER",
        "v": "INTEGER",
        "x": "DOUBLE",
        "z": "DOUBLE",
        "hold": "VARCHAR",
        "by": "INTEGER",
        "lane": "INTEGER",
        "trains": "INTEGER[]",
        "cause": "VARCHAR",
        "normal": "BOOLEAN",
        "version": "INTEGER",
        "lanes": "INTEGER",
        "sections": "INTEGER",
        "junctionAreas": "INTEGER",
        "singleTrack": "INTEGER",
        "singleTrackKm": "DOUBLE",
        "passingLoops": "INTEGER",
        "stationGroups": "INTEGER",
        "doubleTracks": "INTEGER",
        "ms": "DOUBLE",
    },
    "snapshot": {
        "s": "VARCHAR",
        "f": "UBIGINT",
        "trains": "INTEGER",
        "active": "BOOLEAN",
        "rounds": "INTEGER",
        "roundMs": "DOUBLE",
        "roundMaxMs": "DOUBLE",
        "Running": "INTEGER",
        "Slowed": "INTEGER",
        **{state: "INTEGER" for state in WAIT_STATES},
    },
    "name": {
        "s": "VARCHAR",
        "kind": "VARCHAR",
        "id": "INTEGER",
        "name": "VARCHAR",
    },
}

# The groups of states the figures are built from; README.md, "What the
# figures count", says what each figure counts.
MOVING = ("Running", "Slowed")
BOARDING = ("Boarding", "LatePassengers")
GAME_STOPPED = ("TrainAhead", "CrossingTrain", "OncomingTrain", "Signal", "Obstacle")
BY_TRAIN = ("TrainAhead", "CrossingTrain", "OncomingTrain")
OTHER = ("RoutePending", "Unknown", "Deadlock")


def sql_list(values: Sequence[str]) -> str:
    """Returns the values as an SQL list literal, e.g. ('a', 'b')."""
    return "(" + ", ".join("'" + value.replace("'", "''") + "'" for value in values) + ")"


def load(root: Path) -> duckdb.DuckDBPyConnection:
    """Reads every session under root into an in-memory database.

    Each session is a folder of JSON Lines files, one per table. A line that
    cannot be read, e.g. the last one of a session the game crashed in, is
    skipped.
    """
    con = duckdb.connect()
    for table, columns in TABLES.items():
        definition = ", ".join(f'"{name}" {kind}' for name, kind in columns.items())
        con.execute(f'CREATE TABLE "{table}" ({definition})')
        for path in sorted(root.glob(f"*/{table}.jsonl")):
            if path.stat().st_size == 0:
                continue
            relation = con.read_json(str(path), format="newline_delimited", columns=columns, ignore_errors=True)
            con.register("incoming", relation)
            con.execute(f'INSERT INTO "{table}" SELECT * FROM incoming')
            con.unregister("incoming")
    return con


def prepare(con: duckdb.DuckDBPyConnection, skip_minutes: float) -> None:
    """Adds view iv: intervals with game minutes and neighbours, without the first skip_minutes of each session.

    Leaving out the start of a session keeps jams from before the load, or
    from before the dispatcher was switched on, out of the figures.
    """
    skip_frames = int(skip_minutes * FRAMES_PER_GAME_MINUTE)
    con.execute(f"""
        CREATE OR REPLACE TEMP VIEW iv AS
        SELECT i.*,
               (i.until - i.f) / {FRAMES_PER_GAME_MINUTE} AS gmin,
               lag(i.state) OVER w AS prev,
               lead(i.state) OVER w AS next,
               i.state IN {sql_list(MOVING)} AS moving,
               i.state NOT IN {sql_list(MOVING)} AND i.state NOT IN {sql_list(BOARDING)} AS standing
        FROM interval i JOIN session ss ON ss.s = i.s
        WHERE i.f >= ss.frame + {skip_frames}
        WINDOW w AS (PARTITION BY i.s, i.train, i.v ORDER BY i.f)
    """)
    con.execute(f"""
        CREATE OR REPLACE TEMP VIEW ev AS
        SELECT e.* FROM event e JOIN session ss ON ss.s = e.s
        WHERE e.f >= ss.frame + {skip_frames}
    """)


def sessions(con: duckdb.DuckDBPyConnection) -> list[dict[str, Any]]:
    """Every session, oldest first, with how many trains ran and for how long."""
    return rows(con, f"""
        SELECT ss.s AS session, ss.started, ss.city, ss.build,
               count(DISTINCT (i.train, i.v)) AS trains,
               coalesce((max(i.until) - min(i.f)) / {FRAMES_PER_GAME_MINUTE} / 60, 0) AS game_hours
        FROM session ss LEFT JOIN interval i ON i.s = ss.s
        GROUP BY ALL ORDER BY ss.s
    """)


# The figures, in the order they are shown: key, label, and whether lower is
# better (True), higher is better (False), or neither (None).
FIGURES: list[tuple[str, str, bool | None]] = [
    ("train_hours", "Zug-Spielstunden", None),
    ("moving_pct", "fährt, % der Zeit", False),
    ("slowed_pct", "davon gedrosselt, %", None),
    ("boarding_pct", "Fahrgastwechsel/Laden, %", None),
    ("held_pct", "hält für den Lotsen, %", True),
    ("game_pct", "vom Spiel gestoppt, %", True),
    ("other_pct", "sonst (Route, unbekannt, Deadlock), %", True),
    ("standing_min_h", "Stehen ohne Fahrgastwechsel, min je Zugstunde", True),
    ("stops_h", "Halte außerhalb Fahrgastwechsel je Zugstunde", True),
    ("junction_min_h", "Stehen auf Weichen/Kreuzungen, min je Zugstunde", True),
    ("platform_blocked_min_h", "Stehen am Bahnsteig ohne Fahrgastwechsel, min je Zugstunde", True),
    ("mismatch_min_h", "Modellfehler (Lotse lässt fahren, Zug steht hinter Zug), min je Zugstunde", True),
    ("platform_stops_h", "Bahnsteighalte je Zugstunde", False),
    ("km_h", "km je Zugstunde", False),
    ("slowed_n", "Drosselungen", None),
    ("slowed_stopped_pct", "davon doch angehalten, %", True),
    ("removed_bad", "vom Spiel gelöschte Züge", True),
    ("removed_normal", "planmäßig entfernte Züge", None),
    ("circles", "Wartekreise", True),
    ("releases", "Freigaben nach Zeitlimit", True),
    ("cuts", "gekürzte Erlaubnisse", True),
]


def figures(con: duckdb.DuckDBPyConnection, session_ids: Sequence[str]) -> list[dict[str, Any]]:
    """The figures of each session, split by whether the dispatcher was switched on.

    Returns one dict per session and dispatcher mode, with "session" and
    "active" and one entry per key in FIGURES. Rates are per train hour of
    game time; a mode without any train time is left out.
    """
    ids = sql_list(session_ids)
    return rows(con, f"""
        WITH t AS (
            SELECT s, active,
                   sum(gmin) AS minutes,
                   sum(gmin) FILTER (WHERE moving) AS moving,
                   sum(gmin) FILTER (WHERE state = 'Slowed') AS slowed,
                   sum(gmin) FILTER (WHERE state IN {sql_list(BOARDING)}) AS boarding,
                   sum(gmin) FILTER (WHERE state = 'AtSignal') AS held,
                   sum(gmin) FILTER (WHERE state IN {sql_list(GAME_STOPPED)}) AS game,
                   sum(gmin) FILTER (WHERE state IN {sql_list(OTHER)}) AS other,
                   sum(gmin) FILTER (WHERE standing) AS standing,
                   count(*) FILTER (WHERE standing AND prev IN {sql_list(MOVING)}) AS stops,
                   sum(gmin) FILTER (WHERE standing AND junction) AS junction,
                   sum(gmin) FILTER (WHERE standing AND station <> 0) AS platform_blocked,
                   sum(gmin) FILTER (WHERE state IN {sql_list(BY_TRAIN)} AND active AND NOT released) AS mismatch,
                   count(*) FILTER (WHERE state = 'Boarding' AND prev IS NOT NULL
                                    AND prev NOT IN {sql_list(BOARDING)}) AS platform_stops,
                   sum(dist) AS metres,
                   count(*) FILTER (WHERE state = 'Slowed') AS slowed_n,
                   count(*) FILTER (WHERE state = 'Slowed' AND next IS NOT NULL
                                    AND next NOT IN {sql_list(MOVING)}
                                    AND next NOT IN {sql_list(BOARDING)}) AS slowed_stopped
            FROM iv WHERE s IN {ids} GROUP BY s, active
        ), e AS (
            SELECT s, active,
                   count(*) FILTER (WHERE type = 'removed' AND NOT normal) AS removed_bad,
                   count(*) FILTER (WHERE type = 'removed' AND normal) AS removed_normal,
                   count(*) FILTER (WHERE type = 'circle') AS circles,
                   count(*) FILTER (WHERE type = 'release') AS releases,
                   count(*) FILTER (WHERE type = 'cut') AS cuts
            FROM ev WHERE s IN {ids} GROUP BY s, active
        )
        SELECT t.s AS session, t.active,
               t.minutes / 60 AS train_hours,
               100 * coalesce(t.moving, 0) / t.minutes AS moving_pct,
               100 * coalesce(t.slowed, 0) / t.minutes AS slowed_pct,
               100 * coalesce(t.boarding, 0) / t.minutes AS boarding_pct,
               100 * coalesce(t.held, 0) / t.minutes AS held_pct,
               100 * coalesce(t.game, 0) / t.minutes AS game_pct,
               100 * coalesce(t.other, 0) / t.minutes AS other_pct,
               coalesce(t.standing, 0) / (t.minutes / 60) AS standing_min_h,
               t.stops / (t.minutes / 60) AS stops_h,
               coalesce(t.junction, 0) / (t.minutes / 60) AS junction_min_h,
               coalesce(t.platform_blocked, 0) / (t.minutes / 60) AS platform_blocked_min_h,
               coalesce(t.mismatch, 0) / (t.minutes / 60) AS mismatch_min_h,
               t.platform_stops / (t.minutes / 60) AS platform_stops_h,
               coalesce(t.metres, 0) / 1000 / (t.minutes / 60) AS km_h,
               t.slowed_n,
               CASE WHEN t.slowed_n > 0 THEN 100 * t.slowed_stopped / t.slowed_n END AS slowed_stopped_pct,
               coalesce(e.removed_bad, 0) AS removed_bad,
               coalesce(e.removed_normal, 0) AS removed_normal,
               coalesce(e.circles, 0) AS circles,
               coalesce(e.releases, 0) AS releases,
               coalesce(e.cuts, 0) AS cuts
        FROM t LEFT JOIN e ON e.s = t.s AND e.active IS NOT DISTINCT FROM t.active
        WHERE t.minutes > 0
        ORDER BY t.s, t.active
    """)


def hotspots(
    con: duckdb.DuckDBPyConnection, session_id: str, mismatch_only: bool, limit: int = 10
) -> list[dict[str, Any]]:
    """Where trains stood longest without boarding, by 100 m squares.

    With mismatch_only, only standing behind another train while the
    dispatcher, switched on, let the train run: the places where its picture
    of the tracks does not match the game.
    """
    condition = "standing"
    if mismatch_only:
        condition += f" AND state IN {sql_list(BY_TRAIN)} AND active AND NOT released"
    return rows(con, f"""
        SELECT round(x / 100) * 100 AS x, round(z / 100) * 100 AS z,
               sum(gmin) AS minutes, count(*) AS stops, count(DISTINCT (train, v)) AS trains,
               mode(state) AS state, 100 * avg(junction::INTEGER) AS junction_pct,
               bool_or(station <> 0) AS platform
        FROM iv WHERE s = ? AND {condition}
        GROUP BY ALL ORDER BY minutes DESC LIMIT {limit}
    """, [session_id])


def longest(con: duckdb.DuckDBPyConnection, session_id: str, limit: int = 10) -> list[dict[str, Any]]:
    """The longest single stands without boarding."""
    return rows(con, f"""
        SELECT iv.train, t.line, iv.state, iv.hold, iv.by, iv.gmin AS minutes,
               CAST(round(iv.x) AS INTEGER) AS x, CAST(round(iv.z) AS INTEGER) AS z, iv.junction, iv.active
        FROM iv LEFT JOIN train t ON t.s = iv.s AND t.train = iv.train AND t.v = iv.v
        WHERE iv.s = ? AND iv.standing
        ORDER BY iv.gmin DESC LIMIT {limit}
    """, [session_id])


def rows(con: duckdb.DuckDBPyConnection, query: str, parameters: Sequence[Any] = ()) -> list[dict[str, Any]]:
    """Runs the query and returns its rows as dicts by column name."""
    result = con.execute(query, parameters)
    names = [column[0] for column in result.description]
    return [dict(zip(names, row, strict=True)) for row in result.fetchall()]


def export_sqlite(con: duckdb.DuckDBPyConnection, path: Path) -> None:
    """Writes every table into a new SQLite file at path; lists become JSON text."""
    path.unlink(missing_ok=True)
    lite = sqlite3.connect(path)
    try:
        for table, columns in TABLES.items():
            names = list(columns)
            quoted = ", ".join(f'"{name}"' for name in names)
            lite.execute(f'CREATE TABLE "{table}" ({quoted})')
            records = con.execute(f'SELECT {quoted} FROM "{table}"').fetchall()
            converted = [tuple(json.dumps(value) if isinstance(value, list) else value for value in record)
                         for record in records]
            marks = ", ".join("?" for _ in names)
            lite.executemany(f'INSERT INTO "{table}" VALUES ({marks})', converted)
        lite.commit()
    finally:
        lite.close()


def number(value: Any) -> str:
    """A cell of a text table: numbers with at most one decimal, nothing for NULL."""
    if value is None:
        return ""
    if isinstance(value, bool):
        return "ja" if value else "nein"
    if isinstance(value, float):
        return f"{value:.1f}" if abs(value) < 1000 else f"{value:.0f}"
    return str(value)


def text_table(headers: Sequence[str], body: Sequence[Sequence[Any]]) -> str:
    """Aligns the cells into columns: the first left, the rest right."""
    cells = [list(headers)] + [[number(value) for value in row] for row in body]
    widths = [max(len(row[i]) for row in cells) for i in range(len(headers))]
    lines = []
    for index, row in enumerate(cells):
        parts = [row[0].ljust(widths[0])] + [cell.rjust(width) for cell, width in zip(row[1:], widths[1:], strict=True)]
        lines.append("  ".join(parts).rstrip())
        if index == 0:
            lines.append("  ".join("-" * width for width in widths))
    return "\n".join(lines)


def render(con: duckdb.DuckDBPyConnection, count: int, focus: str | None, skip_minutes: float) -> str:
    """The report as text: sessions, figures of the last count sessions, and places of the focus session."""
    all_sessions = sessions(con)
    if not all_sessions:
        return "Keine Sitzungen gefunden."
    out: list[str] = []
    out.append("Sitzungen")
    out.append(text_table(
        ["Sitzung", "Beginn", "Stadt", "Build", "Züge", "Spielstunden"],
        [[s["session"], s["started"], s["city"], s["build"], s["trains"], s["game_hours"]] for s in all_sessions],
    ))

    chosen = [s["session"] for s in all_sessions][-count:]
    if focus is not None and focus not in chosen:
        chosen.append(focus)
    columns = figures(con, chosen)
    out.append("")
    skipped = f", ohne die ersten {skip_minutes:g} Spielminuten jeder Sitzung" if skip_minutes > 0 else ""
    out.append(f"Kennzahlen je Sitzung und Lotse an/aus{skipped}")
    headers = ["", "besser"] + [f"{c['session']} {'an' if c['active'] else 'aus'}" for c in columns]
    direction = {True: "kleiner", False: "größer", None: ""}
    body = [[label, direction[lower]] + [c[key] for c in columns] for key, label, lower in FIGURES]
    out.append(text_table(headers, body))

    target = focus if focus is not None else chosen[-1]
    out.append("")
    out.append(f"Wo Züge ohne Fahrgastwechsel standen, Sitzung {target} (100-m-Felder)")
    out.append(place_table(hotspots(con, target, mismatch_only=False)))
    out.append("")
    out.append("Wo Züge hinter Zügen standen, obwohl der Lotse sie fahren ließ (Modellfehler)")
    out.append(place_table(hotspots(con, target, mismatch_only=True)))
    out.append("")
    out.append("Längste einzelne Halte ohne Fahrgastwechsel")
    out.append(text_table(
        ["Zug", "Linie", "Zustand", "Lotse", "wartet auf", "Minuten", "x", "z", "Weiche", "Lotse an"],
        [[f"#{r['train']}", r["line"], r["state"], r["hold"], f"#{r['by']}" if r["by"] else "", r["minutes"],
          r["x"], r["z"], r["junction"], r["active"]] for r in longest(con, target)],
    ))
    return "\n".join(out)


def place_table(places: list[dict[str, Any]]) -> str:
    if not places:
        return "(keine)"
    return text_table(
        ["x, z", "Minuten", "Halte", "Züge", "häufigster Zustand", "auf Weiche %", "Bahnsteig"],
        [[f"{p['x']:.0f}, {p['z']:.0f}", p["minutes"], p["stops"], p["trains"], p["state"], p["junction_pct"],
          p["platform"]] for p in places],
    )


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("metrics", type=Path, help="the Metrics folder, with one subfolder per session")
    parser.add_argument("--sessions", type=int, default=3,
                        help="how many of the latest sessions to compare (default 3)")
    parser.add_argument("--session", help="the session to list places for (default: the latest)")
    parser.add_argument("--skip-minutes", type=float, default=0.0,
                        help="game minutes at the start of each session to leave out, e.g. while an old jam clears")
    parser.add_argument("--sqlite", type=Path, help="also write all records into this SQLite file")
    args = parser.parse_args(argv)

    con = load(args.metrics)
    prepare(con, args.skip_minutes)
    print(render(con, args.sessions, args.session, args.skip_minutes))
    if args.sqlite is not None:
        export_sqlite(con, args.sqlite)
        print(f"\nSQLite: {args.sqlite}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
