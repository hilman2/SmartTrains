import { useValue } from "cs2/api";
import { LocalizedEntityName, Name } from "cs2/l10n";
import { Button, Panel, Scrollable, Tooltip } from "cs2/ui";
import classNames from "classnames";
import { ReactElement } from "react";
import { actions, Despawn, DespawnCause, Despawns, despawns$, Filter, filter$, panelOpen$, setPanelOpen, TrainRow, Trains, trains$, WaitReason } from "bindings";
import { Translate, useTranslate } from "localization";
import styles from "trains-panel.module.scss";

// Layout note: the game's UI engine (Coherent Gameface) lays out every text
// node as its own flex item, so "{a} · {b}" in JSX renders as separate blocks
// under each other. Texts are therefore built as one string, and every row
// states its flex direction. A game name (LocalizedEntityName) is a component
// of its own and always sits in its own element of a row.
//
// The game's font has no arrows beyond "→"; other symbols render as boxes,
// so the panel says things in words.

/**
 * Wraps an element in the game's tooltip, or leaves it alone without text.
 * The tooltip attaches to its one child through a ref, which the game's
 * types express as a type React's own element type does not satisfy.
 */
const Hint = ({ text, children }: { text: string | null; children: ReactElement }) =>
  text ? <Tooltip tooltip={<div className={styles.tooltip}>{text}</div>}>{children as any}</Tooltip> : children;

const filters: Filter[] = ["all", "standing", "moving", "removed"];

function matches(filter: Filter, row: TrainRow): boolean {
  if (filter === "standing") return !row.moving;
  if (filter === "moving") return row.moving;
  return true;
}

export const TrainsPanel = () => {
  const open = useValue(panelOpen$);
  const trains = useValue(trains$);
  const despawns = useValue(despawns$);
  const filter = useValue(filter$);
  const { t, game } = useTranslate();
  if (!open) return null;

  const rows = trains.rows.filter((row) => matches(filter, row));
  const counts: Record<Filter, number> = {
    all: trains.total,
    standing: trains.total - trains.moving,
    moving: trains.moving,
    removed: despawns.records.length,
  };
  const labels: Record<Filter, string> = {
    all: t("Filter.All", "All"),
    standing: t("Filter.Standing", "Standing"),
    moving: t("Filter.Moving", "Moving"),
    removed: t("Filter.Removed", "Removed"),
  };

  return (
    <Panel
      draggable
      className={styles.panel}
      header={<div className={styles.title}>{t("Panel.Title", "Smart Trains")}</div>}
      onClose={() => setPanelOpen(false)}
    >
      <SummaryLine trains={trains} t={t} />
      <div className={styles.filters}>
        {filters.map((f) => (
          <Button
            key={f}
            variant="flat"
            className={classNames(styles.filter, filter === f && styles.filterActive, f === "removed" && counts.removed > 0 && styles.filterAlarm)}
            selected={filter === f}
            onSelect={() => filter$.update(f)}
          >
            {`${labels[f]} (${counts[f]})`}
          </Button>
        ))}
      </div>
      <Scrollable className={styles.scroll} vertical>
        {filter === "removed" ? (
          <DespawnList despawns={despawns} t={t} game={game} />
        ) : rows.length === 0 ? (
          <div className={styles.empty}>{trains.total === 0 ? t("Empty.City", "There are no trains in this city.") : t("Empty.Filter", "No train in this view.")}</div>
        ) : (
          rows.map((row) => <TrainCard key={`${row.index}.${row.version}`} row={row} t={t} game={game} />)
        )}
      </Scrollable>
    </Panel>
  );
};

const SummaryLine = ({ trains, t }: { trains: Trains; t: Translate }) => {
  const parts = [
    `${trains.moving} ${t("Count.Moving", "moving")}`,
    `${trains.standing} ${t("Count.Standing", "standing")}`,
    `${trains.atPlatform} ${t("Count.AtPlatform", "at a platform")}`,
  ];
  return (
    <div className={styles.summary}>
      <div className={styles.summaryText}>{parts.join(" · ")}</div>
      {trains.deadlocked > 0 && <div className={styles.summaryAlarm}>{`${trains.deadlocked} ${t("Count.Deadlocked", "deadlocked")}`}</div>}
    </div>
  );
};

// ---- Texts shared by live trains and removed ones ----

function cargoText(percent: number, resource: string, t: Translate, game: Translate): string {
  if (percent <= 0) return `${t("Load", "Load")}: ${t("Empty", "empty")}`;
  const what = resource ? ` · ${game(`Resources.TITLE[${resource}]`, resource)}` : "";
  return `${t("Load", "Load")} ${percent} %${what}`;
}

/** What the train is, when it has no line to name it by. */
function kindLabel(train: { cargo: boolean; through: boolean; returning: boolean }, t: Translate): string {
  if (train.through) return t("Kind.Through", "Through traffic");
  const kind = train.cargo ? t("Kind.Cargo", "Freight train") : t("Kind.Passenger", "Passenger train");
  return train.returning ? `${kind} · ${t("Kind.Returning", "to the depot")}` : kind;
}

// ---- One train ----

/** Colour class of the status dot: what the train does, at a glance. */
function statusClass(row: TrainRow): string {
  switch (row.reason) {
    case WaitReason.None:
      return styles.dotMoving;
    case WaitReason.Boarding:
    case WaitReason.LatePassengers:
      return styles.dotPlatform;
    case WaitReason.Deadlock:
      return styles.dotAlarm;
    default:
      return styles.dotStanding;
  }
}

/** English texts of the reasons, shown if the C# side's translations are missing. */
const reasonFallback: Record<WaitReason, string> = {
  [WaitReason.None]: "Moving",
  [WaitReason.Boarding]: "Passengers boarding",
  [WaitReason.LatePassengers]: "Waiting for passengers",
  [WaitReason.TrainAhead]: "Track occupied",
  [WaitReason.CrossingTrain]: "Crossing train",
  [WaitReason.OncomingTrain]: "Oncoming train",
  [WaitReason.Signal]: "Signal",
  [WaitReason.Obstacle]: "Obstacle on the track",
  [WaitReason.RoutePending]: "Looking for a route",
  [WaitReason.Deadlock]: "Deadlocked",
  [WaitReason.Unknown]: "Standing",
};

/** The reason's text, with the cargo wording where passengers make no sense. */
function reasonLabel(row: TrainRow, t: Translate): string {
  if (row.cargo && row.reason === WaitReason.Boarding) return t("Reason.Loading", "Loading");
  if (row.cargo && row.reason === WaitReason.LatePassengers) return t("Reason.LateLoading", "Still loading");
  return t("Reason." + WaitReason[row.reason], reasonFallback[row.reason] ?? WaitReason[row.reason]);
}

function reasonHint(row: TrainRow, t: Translate): string | null {
  if (row.reason === WaitReason.None) return null;
  const text = t("ReasonHint." + WaitReason[row.reason], "");
  return text === "" ? null : text;
}

function statusText(row: TrainRow, t: Translate): string {
  const reason = reasonLabel(row, t);
  if (row.moving || row.minutes < 1) return reason;
  return `${reason} · ${t("Since", "for")} ${Math.round(row.minutes)} min`;
}

function loadText(row: TrainRow, t: Translate, game: Translate): string | null {
  if (row.through) return null;
  if (row.cargo) {
    if (row.loadCapacity <= 0) return null;
    return cargoText(Math.round((100 * row.load) / row.loadCapacity), row.resource, t, game);
  }
  if (row.passengerCapacity <= 0) return null;
  return `${t("Passengers", "Passengers")} ${row.passengers}/${row.passengerCapacity}`;
}

const EntityName = ({ name, fallback, className }: { name: Name | null; fallback: string; className?: string }) => (
  <div className={className}>{name ? <LocalizedEntityName value={name} /> : fallback}</div>
);

const TrainCard = ({ row, t, game }: { row: TrainRow; t: Translate; game: Translate }) => {
  const load = loadText(row, t, game);
  return (
    <div className={classNames(styles.card, row.reason === WaitReason.Deadlock && styles.cardAlarm)}>
      <Button variant="flat" className={styles.cardMain} onSelect={() => actions.select(row)}>
        <div className={styles.lineRow}>
          <div className={styles.swatch} style={{ backgroundColor: row.lineColor || "rgba(255, 255, 255, 0.25)" }} />
          {row.line ? <EntityName name={row.line} fallback="" className={styles.lineName} /> : <div className={styles.lineName}>{kindLabel(row, t)}</div>}
          <div className={styles.trainId}>{`#${row.index}`}</div>
        </div>
        <Hint text={reasonHint(row, t)}>
          <div className={styles.statusRow}>
            <div className={classNames(styles.dot, statusClass(row))} />
            <div className={styles.statusText}>{statusText(row, t)}</div>
          </div>
        </Hint>
        {(row.from || row.to) && (
          <div className={styles.routeRow}>
            <EntityName name={row.from} fallback="–" className={styles.place} />
            <div className={styles.arrow}>→</div>
            <EntityName name={row.to} fallback="–" className={styles.place} />
          </div>
        )}
        {load && <div className={styles.loadRow}>{load}</div>}
      </Button>
      {!row.moving && (
        <div className={styles.actionRow}>
          {row.blocker ? (
            <Button variant="flat" className={styles.blocker} onSelect={() => row.blocker && actions.select(row.blocker)}>
              <div className={styles.blockerText}>{`${t("WaitsFor", "waits for")} #${row.blocker.index}`}</div>
              {row.blocker.line && <EntityName name={row.blocker.line} fallback="" className={styles.blockerLine} />}
            </Button>
          ) : (
            <div className={styles.grow} />
          )}
          <Hint text={t("DiagnoseHint", "Writes this train, every train it waits for, and the track ahead of each to Logs/SmartTrains.log.")}>
            <Button variant="flat" className={styles.diagnose} onSelect={() => actions.diagnose(row)}>
              {t("Diagnose", "Diagnose")}
            </Button>
          </Hint>
        </div>
      )}
    </div>
  );
};

// ---- Removed trains ----

const causeFallback: Record<DespawnCause, string> = {
  [DespawnCause.Deadlock]: "Deadlocked",
  [DespawnCause.NoRoute]: "No route found",
  [DespawnCause.TargetGone]: "Destination gone",
  [DespawnCause.Arrived]: "Arrived",
  [DespawnCause.Depot]: "At the depot",
  [DespawnCause.Other]: "Other",
};

function chainText(record: Despawn, t: Translate): string | null {
  if (record.chain.length === 0) return null;
  const links = record.chain.map((link) => (link.line ? `#${link.train} ${link.line}` : `#${link.train}`));
  if (record.backToStart) links.push(`#${record.train}`);
  return `${t("WaitedFor", "Waited for")} ${links.join(" → ")}`;
}

const DespawnList = ({ despawns, t, game }: { despawns: Despawns; t: Translate; game: Translate }) => {
  const normal = `${t("Removed.Normal", "Removed as planned")}: ${despawns.arrived} ${t("Removed.Arrived", "through trains at their destination")} · ${despawns.depot} ${t("Removed.Depot", "at the depot")}`;
  return (
    <>
      <Hint text={t("Removed.Hint", "The game removes trains without a word when they block each other in a circle or find no route. Every such case since loading is listed here and written to Logs/SmartTrains.log.")}>
        <div className={styles.removedNote}>{normal}</div>
      </Hint>
      {despawns.records.length === 0 ? (
        <div className={styles.empty}>{t("Removed.None", "Since loading, the game has removed no train unplanned.")}</div>
      ) : (
        despawns.records.map((record) => <DespawnCard key={record.id} record={record} t={t} game={game} />)
      )}
    </>
  );
};

const DespawnCard = ({ record, t, game }: { record: Despawn; t: Translate; game: Translate }) => {
  const cause = t("Cause." + DespawnCause[record.cause], causeFallback[record.cause] ?? DespawnCause[record.cause]);
  const stood = record.minutes >= 1 ? ` · ${t("Stood", "stood")} ${Math.round(record.minutes)} min` : "";
  const load = record.cargo
    ? record.loadPercent >= 0 ? cargoText(record.loadPercent, record.resource, t, game) : null
    : record.passengers > 0 ? `${record.passengers} ${t("OnBoard", "passengers on board")}` : null;
  const chain = chainText(record, t);
  return (
    <div className={classNames(styles.card, record.cause === DespawnCause.Deadlock && styles.cardAlarm)}>
      <Button variant="flat" className={styles.cardMain} onSelect={() => actions.showDespawn(record.id)}>
        <div className={styles.lineRow}>
          <div className={classNames(styles.dot, styles.dotAlarm)} />
          <div className={styles.lineName}>{`${cause} · ${record.time}`}</div>
          <div className={styles.trainId}>{`#${record.train}`}</div>
        </div>
        <div className={styles.statusRow}>
          <div className={styles.statusText}>{`${record.line || kindLabel(record, t)}${stood}`}</div>
        </div>
        {chain && <div className={styles.chainRow}>{chain}</div>}
        {load && <div className={styles.loadRow}>{load}</div>}
      </Button>
    </div>
  );
};
