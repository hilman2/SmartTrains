# Smart Trains

A mod for Cities: Skylines II. A central dispatcher lets trains go only
when the track up to the next safe place to wait is free, so they no
longer stop in junctions or block each other into deadlocks. A panel
lists every train, whether it runs or stands and why, what it carries,
and where it comes from and goes to.

Players install it from Paradox Mods; the mod page there describes what
it does and how to use it. This repository holds the source.

## Layout

| Folder | |
|---|---|
| `src/SmartTrains.Core` | The dispatcher and everything else that needs no game code: track layout, waiting reasons, metrics records. Built and tested on its own. |
| `src/SmartTrains` | The game side: reads trains and track, holds and slows trains, the panel's data, metrics. Compiles the core in. |
| `src/SmartTrains.UI` | The panel. |
| `tests` | Tests of the core, and a smoke test of the panel. |
| `tools/metrics` | The report on recorded metrics; see its [README](tools/metrics/README.md). |

## Building

The panel, in Docker:

```bash
docker compose -f build/docker/compose.yml run --rm ui
```

The mod, with the game's modding toolchain installed (Options, Modding):

```bash
dotnet build src/SmartTrains/SmartTrains.csproj -c Release
```

This copies the build into the game's Mods folder. While the game runs it
holds the mod open; `-p:DeployMod=false` builds without copying.

The tests, in Docker:

```bash
docker compose -f tests/docker/compose.yml run --rm tests
```

## Publishing

The mod page's text and images are set in
`src/SmartTrains/Properties/PublishConfiguration.xml`, as in the
toolchain's mod template. The publish profiles next to it run the
toolchain's publisher:

```bash
dotnet publish src/SmartTrains/SmartTrains.csproj -p:PublishProfile=PublishNewMod
```

`PublishNewMod` publishes the mod the first time. After that, the mod's id
goes into `ModId`, which updating needs: `PublishNewVersion` uploads a
new version, and `UpdatePublishedConfiguration` changes only the page.
