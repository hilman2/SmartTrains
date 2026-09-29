// Loads the built panel (SmartTrains.mjs) against stand-ins for the game's UI
// modules and renders every component Smart Trains registers, in several
// states.
//
// The stand-ins offer exactly the exports the game has (cs2-exports.json,
// read from the game's own UI bundle). Touching anything else fails the run
// with the name of the missing export. The types shipped with the UI
// template list functions the game does not provide; this is where such a
// mismatch shows up, instead of in the game, where it takes down the whole
// interface.
//
//   node tests/ui/smoke.mjs <path to SmartTrains.mjs> [preview.html]
// React and react-dom are resolved from the current directory's node_modules.
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const require = createRequire(join(process.cwd(), "package.json"));
const React = require("react");
const ReactDOM = require("react-dom");
const { renderToString } = require("react-dom/server");

const here = dirname(fileURLToPath(import.meta.url));
const exportsOf = JSON.parse(readFileSync(join(here, "cs2-exports.json"), "utf8"));

let scenario = {};
const localBindings = [];
const triggers = [];

function strictModule(name, impl) {
  const names = exportsOf[name];
  const target = {};
  for (const n of names) {
    target[n] = impl[n] ?? ((..._) => {
      throw new Error(`${name}.${n} exists in the game but the smoke test has no stand-in for it; add one.`);
    });
  }
  return new Proxy(target, {
    get(t, p) {
      if (p in t) return t[p];
      if (typeof p === "symbol" || p === "__esModule" || p === "default" || p === "then") return undefined;
      throw new Error(`The game's ${name} has no export "${String(p)}".`);
    },
  });
}

// The class is passed on so that the preview page shows the panel's own
// layout; every other prop of the game's components is dropped.
const element = (tag) => (props) => React.createElement(tag, { className: props.className }, props.header, props.children);

// The game's names come in three shapes (types/l10n.d.ts, NameType); render
// whichever text each carries, and fail on anything else, as the game would
// show nothing useful for it.
function nameText(value) {
  if (!value || typeof value !== "object") throw new Error(`LocalizedEntityName got ${JSON.stringify(value)} instead of a name.`);
  switch (value.__Type) {
    case "names.CustomName":
      return value.name;
    case "names.LocalizedName":
    case "names.FormattedName":
      return value.nameId;
    default:
      throw new Error(`LocalizedEntityName got an unknown name type ${JSON.stringify(value.__Type)}.`);
  }
}

globalThis.window = globalThis;
window.React = React;
window.ReactDOM = ReactDOM;
window["cs2/api"] = strictModule("cs2/api", {
  bindValue: (group, name, fallback) => ({
    get value() {
      const key = `${group}.${name}`;
      return key in scenario ? scenario[key] : fallback;
    },
  }),
  bindLocalValue: (initial) => {
    const binding = { initial, value: initial, update(v) { this.value = v; } };
    localBindings.push(binding);
    return binding;
  },
  useValue: (binding) => binding.value,
  trigger: (...args) => triggers.push(args),
});
window["cs2/l10n"] = strictModule("cs2/l10n", {
  useLocalization: () => ({ translate: (id, fallback) => fallback }),
  LocalizedEntityName: ({ value }) => React.createElement("span", null, nameText(value)),
});
window["cs2/ui"] = strictModule("cs2/ui", {
  Button: element("button"),
  FloatingButton: element("button"),
  Panel: element("section"),
  Scrollable: element("div"),
  Tooltip: element("span"),
});
window["cs2/modding"] = strictModule("cs2/modding", {});

const bundle = process.argv[2];
const module = await import(pathToFileURL(bundle).href);
const appended = [];
module.default({ append: (target, component) => appended.push({ target, component }) });
if (appended.length === 0) throw new Error("Smart Trains registered no components.");

const custom = (name) => ({ __Type: "names.CustomName", name });
const formatted = (nameId) => ({ __Type: "names.FormattedName", nameId, nameArgs: { NUMBER: "3" } });
const base = {
  version: 1, model: formatted("Assets.NAME[Train01]"), cargo: false, through: false, returning: false,
  line: formatted("Transport.LINE_NAME"), lineColor: "#3dbf6b", moving: true, reason: 0, minutes: 0, blocker: null,
  passengers: 120, passengerCapacity: 400, load: 0, loadCapacity: 0, resource: "",
  from: custom("Central Station"), to: custom("Harbour"),
  dispatch: { rank: 20, holding: false, reason: 0, waitingFor: null, advice: 0 },
};
const held = { rank: 34.6, holding: true, reason: 1, waitingFor: { index: 12, version: 1, line: custom("Line 2") }, advice: 0 };
const rows = [
  { ...base, index: 11, moving: false, reason: 9, minutes: 3.2, blocker: { index: 12, version: 1, line: custom("Line 2") } },
  { ...base, index: 12, moving: false, reason: 3, minutes: 17.6, blocker: { index: 13, version: 1, line: null } },
  { ...base, index: 13, moving: false, reason: 4, minutes: 0.4, blocker: { index: 11, version: 1, line: custom("Line 1") } },
  { ...base, index: 14, moving: false, reason: 1, minutes: 2 },
  { ...base, index: 15, cargo: true, moving: false, reason: 2, minutes: 12, passengerCapacity: 0, load: 38, loadCapacity: 60, resource: "Wood", line: custom("Freight A"), lineColor: "#f5b83d" },
  { ...base, index: 16, cargo: true, through: true, line: null, lineColor: "", from: custom("Rail Outside Connection"), to: formatted("Assets.NAME[OutsideConnection]"), load: 10, loadCapacity: 20 },
  { ...base, index: 17, returning: true, line: null, lineColor: "", from: null, to: custom("Train Depot") },
  { ...base, index: 18, moving: false, reason: 10, minutes: 1, from: null, to: null, line: custom("A very long line name that does not fit into the panel at all") },
  { ...base, index: 19, cargo: true, moving: false, reason: 1, passengerCapacity: 0, load: 0, loadCapacity: 60 },
  { ...base, index: 20, moving: false, reason: 8, minutes: 0 },
  { ...base, index: 21, moving: false, reason: 11, minutes: 4, blocker: held.waitingFor, dispatch: held },
  { ...base, index: 22, moving: true, reason: 0, dispatch: { ...held, reason: 3, waitingFor: null } },
  { ...base, index: 24, moving: true, reason: 0, dispatch: { ...held, advice: 11.2 } },
  { ...base, index: 25, moving: true, reason: 0, dispatch: { rank: 20, holding: false, reason: 0, waitingFor: null, advice: 6.5 } },
  { ...base, index: 23, dispatch: null },
];
const trains = { total: rows.length, moving: 5, standing: 5, atPlatform: 3, deadlocked: 1, dispatcherActive: false, holding: 2, metricsActive: true, rows };
const despawn = {
  id: 1, time: "09:39", cause: 0, train: 124360, line: "Güterzugroute 5", cargo: true, through: false, returning: false,
  minutes: 35.4, passengers: 0, loadPercent: 23, resource: "Timber", backToStart: true,
  chain: [{ train: 124355, line: "Güterzugroute 3" }, { train: 50575, line: "" }],
};
const despawns = {
  arrived: 12, depot: 3,
  records: [
    despawn,
    { ...despawn, id: 2, cause: 1, train: 663383, line: "", cargo: false, through: true, minutes: 0, passengers: 84, loadPercent: -1, chain: [], backToStart: false },
    { ...despawn, id: 3, cause: 5, line: "", returning: true, loadPercent: 0, resource: "", chain: [{ train: 7, line: "Bahnlinie 3" }], backToStart: false },
    { ...despawn, id: 4, cause: 2, passengers: 0, loadPercent: -1, cargo: false },
  ],
};
const scenarios = {
  "empty city": {},
  "all trains": { "smartTrains.trains": trains },
  "dispatcher on": { "smartTrains.trains": { ...trains, dispatcherActive: true } },
  "standing filter": { filter: "standing", "smartTrains.trains": trains },
  "moving filter": { filter: "moving", "smartTrains.trains": trains },
  "moving filter, none moving": { filter: "moving", "smartTrains.trains": { ...trains, moving: 0, rows: rows.filter((r) => !r.moving) } },
  "removed, none": { filter: "removed", "smartTrains.trains": trains },
  "removed": { filter: "removed", "smartTrains.trains": trains, "smartTrains.despawns": despawns },
  "every reason": {
    "smartTrains.trains": { ...trains, rows: Array.from({ length: 12 }, (_, reason) => ({ ...base, index: 100 + reason, moving: reason === 0, reason, minutes: 5 })) },
  },
};

let failures = 0;
const preview = [];
for (const [label, data] of Object.entries(scenarios)) {
  for (const open of [false, true]) {
    scenario = data;
    // Boolean local values say whether the panel is open; the string one is
    // the filter, chosen by the scenario's "filter" entry.
    for (const b of localBindings) b.value = typeof b.initial === "boolean" ? open : (data.filter ?? b.initial);
    for (const { target, component } of appended) {
      try {
        const html = renderToString(React.createElement(component));
        if (open && target === "Game") preview.push(`<h3>${label}</h3><div class="stage">${html}</div>`);
      } catch (e) {
        failures++;
        console.error(`FAIL ${target}, ${label}, panel ${open ? "open" : "closed"}: ${e.message}`);
      }
    }
  }
}
if (failures > 0) {
  console.error(`${failures} render(s) failed.`);
  process.exit(1);
}

// With a second argument, write the open panel of every scenario to an HTML
// page for a look in a normal browser. Layout there differs from the game's
// engine, but texts and colours can be checked.
if (process.argv[3]) {
  const { writeFileSync } = await import("node:fs");
  const css = readFileSync(join(dirname(bundle), "SmartTrains.css"), "utf8");
  const page = `<!doctype html><meta charset="utf-8"><style>${css}</style>
<style>html{font-size:1px}body{font-size:16rem;background:#1b1f26;color:#e8e8e8;font-family:sans-serif}
.stage{position:relative;height:900px;margin-bottom:20px}section{position:absolute}
button{display:flex;flex-direction:column;background:none;border:0;color:inherit;text-align:left;font:inherit}</style>
${preview.join("\n")}`;
  writeFileSync(process.argv[3], page);
}
console.log(`Smoke test passed: ${appended.length} component(s), ${Object.keys(scenarios).length} scenarios.`);
