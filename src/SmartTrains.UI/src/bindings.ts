// Values and triggers shared with the C# side (src/SmartTrains/UI/TrainsUISystem.cs).
// Field names and enum numbers must match what that class writes.
import { bindLocalValue, bindValue, trigger } from "cs2/api";
import { Name } from "cs2/l10n";

const group = "smartTrains";

/** Numbers as in SmartTrains.Core.Monitor.WaitReason. */
export enum WaitReason {
  None = 0,
  Boarding = 1,
  LatePassengers = 2,
  TrainAhead = 3,
  CrossingTrain = 4,
  OncomingTrain = 5,
  Signal = 6,
  Obstacle = 7,
  RoutePending = 8,
  Deadlock = 9,
  Unknown = 10,
}

export interface EntityRef {
  index: number;
  version: number;
}

/** Another train, named by its line. */
export interface TrainRef extends EntityRef {
  line: Name | null;
}

export interface TrainRow extends EntityRef {
  /** The train model, as the game names it. */
  model: Name | null;
  cargo: boolean;
  /** Through traffic between two outside connections. */
  through: boolean;
  /** On the way back to its depot. */
  returning: boolean;
  line: Name | null;
  /** "#rrggbb", empty without a line. */
  lineColor: string;
  moving: boolean;
  reason: WaitReason;
  /** In-game minutes the train has stood; 0 while it moves. */
  minutes: number;
  /** The train in the way, for the reasons that name one. */
  blocker: TrainRef | null;
  passengers: number;
  passengerCapacity: number;
  load: number;
  loadCapacity: number;
  /** Largest part of the load, by the game's resource name, e.g. "Food"; empty when empty. */
  resource: string;
  from: Name | null;
  to: Name | null;
}

export interface Trains {
  total: number;
  moving: number;
  /** Standing on the line, not at a platform. */
  standing: number;
  atPlatform: number;
  deadlocked: number;
  /** Deadlocked first, then standing longest first, then at a platform, then moving. */
  rows: TrainRow[];
}

const emptyTrains: Trains = { total: 0, moving: 0, standing: 0, atPlatform: 0, deadlocked: 0, rows: [] };

export const trains$ = bindValue<Trains>(group, "trains", emptyTrains);

/** Numbers as in SmartTrains.Core.Monitor.DespawnCause. */
export enum DespawnCause {
  Deadlock = 0,
  NoRoute = 1,
  TargetGone = 2,
  Arrived = 3,
  Depot = 4,
  Other = 5,
}

export interface ChainLink {
  train: number;
  /** Plain text; empty for a train without a line. */
  line: string;
}

/** A train the game removed. Names are plain text from the moment of removal. */
export interface Despawn {
  id: number;
  /** In-game time of day, "HH:mm". */
  time: string;
  cause: DespawnCause;
  train: number;
  line: string;
  cargo: boolean;
  through: boolean;
  returning: boolean;
  /** In-game minutes it had stood before. */
  minutes: number;
  passengers: number;
  /** -1 for passenger trains. */
  loadPercent: number;
  resource: string;
  /** The trains it waited for, in order. */
  chain: ChainLink[];
  /** The last train of the chain waited for this one again: a deadlock circle. */
  backToStart: boolean;
}

export interface Despawns {
  /** Through trains that reached their outside connection since loading. Normal. */
  arrived: number;
  /** Trains taken off the map at their depot since loading. Normal. */
  depot: number;
  /** Removals that need attention, newest first. */
  records: Despawn[];
}

export const despawns$ = bindValue<Despawns>(group, "despawns", { arrived: 0, depot: 0, records: [] });

/**
 * Whether the panel is open. The button and the panel share it; change it
 * through setPanelOpen, because the C# side only sends the train list while
 * the panel is open.
 */
export const panelOpen$ = bindLocalValue(false);

export type Filter = "all" | "standing" | "moving" | "removed";

/** The list's filter; kept while the panel is closed. */
export const filter$ = bindLocalValue<Filter>("all");

export function setPanelOpen(open: boolean) {
  panelOpen$.update(open);
  trigger(group, "setPanelOpen", open);
}

export const actions = {
  /** Selects the train in the game and follows it with the camera. */
  select: (train: EntityRef) => trigger(group, "select", train.index, train.version),
  /** Writes the train's waiting chain and the track ahead to Logs/SmartTrains.log. */
  diagnose: (train: EntityRef) => trigger(group, "diagnose", train.index, train.version),
  /** Moves the camera to where the game removed a train. */
  showDespawn: (id: number) => trigger(group, "showDespawn", id),
};
