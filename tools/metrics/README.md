# Metrics

The mod records how trains run, so that versions of the mod, and the
dispatcher switched on and off, can be compared by numbers. `report.py` reads
the records and prints the comparison, and where trains stood longest.

The records go to `ModsData/SmartTrains/Metrics` in the game's user folder, on
Windows `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\SmartTrains\Metrics`.
Each session gets a folder of its own, named after the time it began. A
session runs from loading a city until the next load or the end of the game.

## Running the report

From the repository root:

```bash
docker compose -f tools/metrics/compose.yml run --rm report
```

The first run builds the image `smarttrains-metrics`. Options go after
`report`:

| Option | Effect |
|---|---|
| `--sessions N` | Compares the latest N sessions. Default 3. |
| `--by-build` | Compares builds instead of sessions: all sessions of a build count as one run. Single runs vary a lot; a build needs several to be judged. |
| `--city NAME` | Only sessions in this city. |
| `--hours H` | Only the first H game hours of each session, so that sessions of different length compare alike. |
| `--skip-minutes M` | Leaves out the first M game minutes of each session, e.g. while a jam from before the load clears. |
| `--session NAME` | Lists places for this session instead of the latest. |
| `--sqlite FILE` | Also writes all records into a SQLite file in `tools/metrics/out`. |

For example, every build in one city over the first five game hours:

```bash
docker compose -f tools/metrics/compose.yml run --rm report --by-build --city "St. Derwingen" --hours 5
```

`METRICS_DIR` points the report at another Metrics folder.

Lint, type check and tests of the report:

```bash
docker compose -f tools/metrics/compose.yml run --rm check
```

## What the figures count

Times are game time, as the panel shows them. A train hour is one train for
one in-game hour. Each figure is given per session, once for the time the
dispatcher was switched on and once for the time it was off.

A train is always in one state (see [States](#states)). The states group as
follows:

| Group | States |
|---|---|
| moving | `Running`, `Slowed` |
| boarding | `Boarding`, `LatePassengers` |
| held by the dispatcher | `AtSignal` |
| behind a known train | `Queued` |
| stopped by the game | `TrainAhead`, `CrossingTrain`, `OncomingTrain`, `Signal`, `Obstacle` |
| other | `RoutePending`, `Unknown`, `Deadlock` |

A train that is neither moving nor boarding *stands*. The figures:

| Figure | Counts |
|---|---|
| standing, min per train hour | all time standing |
| stops per train hour | changes from moving to standing |
| standing in stands of 30 or 60 min and more, %; longest stand | a stand is a train's standing without a break, whatever it waits for in turn |
| on turnouts and crossings | standing time with a car on a turnout or crossing lane |
| at a platform without boarding | standing time with the front on a platform lane |
| model error | standing behind a train the dispatcher did not know to be there (`TrainAhead`, `CrossingTrain`, `OncomingTrain`) while it is switched on and has not let the train go: it granted the track, but the game stops the train. Closing up on the known train ahead (`Queued`) does not count. |
| platform stops per train hour | changes into `Boarding` from anything else |
| km per train hour | the odometer |
| slowed, and stopped after all | `Slowed` intervals followed by standing |
| removed by the game | `removed` events that are not a train's normal end |

Events are given per 100 train hours. Below the figures, the report lists
for one session where trains stood longest, the bottlenecks (the junction
areas and sections whose track the dispatcher found taken, by the time it
held trains for them), the places of model errors, and the longest stands.

## Records

Every file holds one JSON object per line. Every record has `s`, the session
name. `f` is a simulation frame; 60 frames are one second of train movement,
182 frames one game minute. Trains, lanes and stations are given by entity
index; a train's `v` (entity version) tells apart two trains that got the same
index one after the other.

### session.jsonl

One record per session.

| Field | |
|---|---|
| `started` | Local time the session began. |
| `city` | City name. |
| `build` | Commit the mod was built from; `-dirty` with uncommitted changes. |
| `frame` | Frame at the start. |
| `active` | Whether the dispatcher was switched on at the start. |

### train.jsonl

One record per train, when it is first seen in the session.

| Field | |
|---|---|
| `train`, `v` | The train. |
| `kind` | `passenger` or `cargo`. |
| `through` | Through traffic between two outside connections. |
| `returning` | On the way back to its depot. |
| `line` | Line name, or null. |
| `cars`, `length`, `maxSpeed` | Cars, metres, metres per second. |

### interval.jsonl

One record per stretch of time a train does one thing, written when it ends.
A new interval begins when the state, the dispatcher's hold reason, or whether
the dispatcher is switched on changes.

| Field | |
|---|---|
| `train`, `v` | The train. |
| `state` | See [States](#states). |
| `hold` | For a standing train the dispatcher holds, or would hold if switched on: `TrackHeld`, `NoRoomAhead` or `GivingWay`. Otherwise null. |
| `active` | Dispatcher switched on. |
| `released` | The dispatcher would hold the train but let it go, to break a waiting circle or after holding it too long. |
| `by` | The train in the way, or the one the dispatcher waits for; 0 if none. |
| `f`, `until` | First frame, and the frame the next interval began. |
| `sec` | Seconds of train movement from `f` to `until`. |
| `dist` | Metres run; 0 where the game reset the odometer. |
| `truncated` | The interval did not end by a change of state: the train was removed, or the session ended. |
| `lane` | Lane under the front. |
| `x`, `z` | Position at the start, metres. |
| `junction` | A car stands on a turnout or crossing lane. Only for standing trains. |
| `station` | Station of the lane under the front, 0 off platforms. |
| `advice` | For `Slowed`: the speed the dispatcher slowed the train to, metres per second. |
| `holdLane` | While the dispatcher holds the train: the lane it may not enter; 0 otherwise. |
| `blockedLane` | While the dispatcher holds the train: the lane it found not free; 0 otherwise. |

Fields at the start of an interval (`by`, position, `junction` and so on) are
as they were when it began.

### event.jsonl

| `type` | When | Fields besides `f` and `active` |
|---|---|---|
| `dispatcher` | switched on or off, and at the start of a session | |
| `network` | the track network was read | `version`, `lanes`, `sections`, `junctionAreas`, `singleTrack`, `singleTrackKm`, `passingLoops`, `stationGroups`, `doubleTracks`, `ms` |
| `circle` | a circle of trains waiting for each other formed | `trains`; `train`, `v`, `x`, `z` of the train let go |
| `release` | the dispatcher let a train go after holding it too long | `train`, `v`, `x`, `z`, `hold`, `by`, `lane` (the lane it found taken) |
| `cut` | the dispatcher cut back a train's grant, because another train now stands in the way | `train`, `v`, `x`, `z`, `hold`, `by`, `lane` (where the train now stops) |
| `removed` | the game removed a train | `train`, `v`, `x`, `z`, `cause`, `normal` (the train's normal end) |

A circle counts once, when it forms, and a long hold once until the
dispatcher stops holding the train.

### snapshot.jsonl

Every 1800 frames, half a minute of train movement.

| Field | |
|---|---|
| `trains` | Trains. |
| `active` | Dispatcher switched on. |
| `rounds`, `roundMs`, `roundMaxMs` | Dispatcher rounds since the last snapshot, their mean and longest time in milliseconds. |
| one field per state | Trains in that state. |

### lane.jsonl

Every track lane, each time the network is read.

| Field | |
|---|---|
| `version` | The network read, as in the `network` event. |
| `lane` | Entity index. |
| `kind` | `Plain`, `Switch` or `Crossing` (diamond or level crossing). |
| `twoWay` | Trains may run it both ways. |
| `length` | Metres. |
| `station` | Station of a platform lane, 0 otherwise. |
| `section`, `area` | The section, or for a switch or crossing lane the junction area, the dispatcher puts it in; -1 for the other. Numbers hold for this read only. |
| `group` | The parallel group of its section (passing loop, station tracks, double track), -1 if none. |
| `x0`, `z0`, `x1`, `z1` | Start and end, metres. |

### name.jsonl

| Field | |
|---|---|
| `kind` | `station`. |
| `id` | Entity index. |
| `name` | Name in the game's language. |

## States

| State | The train |
|---|---|
| `Running` | moves. |
| `Slowed` | moves at the dispatcher's speed advice, to reach its hold as the track frees instead of stopping. |
| `Boarding` | stands at a platform before its departure time. |
| `LatePassengers` | stands at a platform after its departure time, waiting for passengers or cargo. |
| `AtSignal` | is held by the dispatcher. |
| `Queued` | is stopped by the train the dispatcher knows to be ahead of it on its granted track: it has closed up as planned. |
| `TrainAhead` | is stopped by a train ahead, or by track ahead another train has reserved. |
| `CrossingTrain` | is stopped by a train crossing its way. |
| `OncomingTrain` | is stopped by a train coming the other way. |
| `Signal` | is stopped by a signal. |
| `Obstacle` | is stopped by something other than a train, e.g. a car on a level crossing. |
| `RoutePending` | waits for the game to compute a new route. |
| `Deadlock` | was found in a deadlock by the game and is about to be removed. |
| `Unknown` | stands, and the game reports no cause. |
