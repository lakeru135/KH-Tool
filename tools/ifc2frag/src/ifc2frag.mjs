// ifc2frag — converts the IFC files written by KH-Tool's "Export IFC + Data" action into That Open
// .frag files, then refreshes the report's "latest" folder and prunes old runs.
//
//   node ifc2frag.cjs --manifest <runs\<stamp>\manifest-<pid>.json> [--wait-pid <pid>]
//   node ifc2frag.cjs --one <in.ifc> <out.frag>          (internal: one file, run as a child process)
//
// The orchestrator converts each model in its OWN child process: web-ifc's WASM heap lives outside
// V8's, so a large model can only be given back to the OS by ending the process. A child that runs
// past the timeout is ended (only ever our own node child, never Revit).
//
// --wait-pid: under a scheduled run KH-Tool starts this while Revit is still closing. Converting
// beside a Revit that may hold tens of GB risks both, so wait for that process to exit first.

import { spawn } from "node:child_process";
import {
  appendFileSync, closeSync, copyFileSync, existsSync, mkdirSync, openSync, readdirSync,
  readFileSync, readSync, renameSync, rmSync, statSync, writeFileSync,
} from "node:fs";
import path from "node:path";
import * as FRAGS from "@thatopen/fragments";
import * as WEBIFC from "web-ifc";

const CHILD_TIMEOUT_MS = 60 * 60 * 1000;   // one model
const WAIT_PID_MAX_MS = 4 * 60 * 60 * 1000; // Revit closing
const RUN_FOLDER = /^\d{8}_\d{6}$/;         // runs\<yyyyMMdd_HHmmss>

// ---------------------------------------------------------------- one file (child process)

async function convertOne(inIfc, outFrag) {
  const importer = new FRAGS.IfcImporter();
  // web-ifc-node.wasm ships beside this script (see build.mjs).
  importer.wasm = { path: scriptDir() + path.sep, absolute: true };
  importer.parseOnce = true; // one parse for geometry + properties: less memory, same output

  const fd = openSync(inIfc, "r");
  const chunk = new Uint8Array(1024 * 1024);
  const t0 = Date.now();
  let bytes;
  try {
    bytes = await importer.process({
      readFromCallback: true,
      readCallback: (offset) => chunk.slice(0, readSync(fd, chunk, 0, chunk.length, offset)),
    });
  } finally {
    closeSync(fd);
  }
  const tmp = outFrag + ".tmp";
  writeFileSync(tmp, bytes);
  renameSync(tmp, outFrag); // a failed conversion never leaves a half-written .frag
  bytes = null;

  const idmap = path.join(path.dirname(outFrag), "idmap.csv");
  const mapped = await writeIdMap(inIfc, idmap);
  return {
    seconds: (Date.now() - t0) / 1000, fragBytes: statSync(outFrag).size, idmapRows: mapped,
    peakRssMB: Math.round(process.memoryUsage().rss / 1048576),
  };
}

/**
 * idmap.csv: GlobalId → Revit ElementId for every IfcElement. Revit's exporter writes the ElementId
 * into IfcElement.Tag, and that — not a GUID recomputed in Revit — is what the report joins on:
 * measured on a sample model, recomputed GUIDs matched 76 % of rows while Tag covered every exported
 * building element (railing and curtain sub-parts carry their parent's id, which is what a click wants).
 */
async function writeIdMap(inIfc, outCsv) {
  const api = new WEBIFC.IfcAPI();
  api.SetWasmPath(scriptDir() + path.sep, true);
  await api.Init();
  const fd = openSync(inIfc, "r");
  const chunk = new Uint8Array(1024 * 1024);
  let modelId;
  try {
    modelId = api.OpenModelFromCallback((offset) => chunk.slice(0, readSync(fd, chunk, 0, chunk.length, offset)));
  } finally {
    closeSync(fd);
  }
  const rows = ["GlobalId,ElementId,IfcClass"];
  for (const type of api.GetIfcEntityList(modelId)) {
    if (!api.IsIfcElement(type)) continue;
    const name = api.GetNameFromTypeCode(type);
    const ids = api.GetLineIDsWithType(modelId, type);
    for (let i = 0; i < ids.size(); i++) {
      const line = api.GetLine(modelId, ids.get(i));
      const guid = line?.GlobalId?.value, tag = line?.Tag?.value;
      if (guid && tag && /^\d+$/.test(String(tag))) rows.push(`${guid},${tag},${name}`);
    }
  }
  api.CloseModel(modelId);
  const tmp = outCsv + ".tmp";
  writeFileSync(tmp, rows.join("\n") + "\n");
  renameSync(tmp, outCsv);
  return rows.length - 1;
}

// ---------------------------------------------------------------- orchestrator

async function runManifest(manifestPath, waitPid) {
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8").replace(/^﻿/, ""));
  const runFolder = path.dirname(manifestPath);
  const logPath = path.join(runFolder, "ifc2frag.log");
  const log = (msg) => appendFileSync(logPath, `${new Date().toISOString()} ${msg}\n`);

  log(`start manifest=${manifestPath} models=${manifest.models?.length ?? 0} node=${process.version}`);
  if (waitPid) await waitForExit(waitPid, log);

  const results = [];
  for (const m of manifest.models ?? []) {
    const result = { name: m.name, status: "skipped", error: "", seconds: 0, fragBytes: 0 };
    results.push(result);
    if (m.status !== "exported" || !m.ifc) { result.error = `export status '${m.status}'`; continue; }

    const inIfc = path.join(m.folder, m.ifc);
    const outFrag = path.join(m.folder, m.ifc.replace(/\.ifc$/i, ".frag"));
    if (!existsSync(inIfc)) { result.status = "failed"; result.error = "IFC file not found"; log(`${m.name}: IFC missing`); continue; }

    log(`${m.name}: converting ${(statSync(inIfc).size / 1048576).toFixed(1)} MB`);
    const child = await runChild(inIfc, outFrag);
    Object.assign(result, child);
    if (result.status === "converted") result.frag = path.basename(outFrag);
    log(`${m.name}: ${result.status}${result.error ? " — " + result.error : ""} (${result.seconds}s)`);
  }

  // manifest-<pid>.json → convert-<pid>.json: each Revit of a split run has its own pair.
  const convertName = path.basename(manifestPath).replace(/^manifest/i, "convert");
  writeJson(path.join(runFolder, convertName === path.basename(manifestPath) ? "convert.json" : convertName), {
    schema: 1, runStamp: manifest.runStamp, finished: new Date().toISOString(), results,
  });

  // latest\ and runs\ are shared by every converter of a split run — one at a time.
  const release = await acquireLock(path.join(manifest.outputRoot, ".ifc2frag.lock"), log);
  try {
    try { refreshLatest(manifest, results, log); } catch (e) { log(`latest: FAILED ${e.message}`); }
    try { pruneRuns(manifest, log); } catch (e) { log(`prune: FAILED ${e.message}`); }
  } finally {
    release();
  }
  log("done");
}

/** An exclusive lock file. A lock older than 30 minutes is taken to be left by a converter that
 *  died, and is broken; after 20 minutes of waiting we go ahead rather than lose the update. */
async function acquireLock(lockPath, log) {
  const started = Date.now();
  for (;;) {
    try {
      closeSync(openSync(lockPath, "wx"));
      return () => rmSync(lockPath, { force: true });
    } catch {
      try {
        if (Date.now() - statSync(lockPath).mtimeMs > 30 * 60000) { rmSync(lockPath, { force: true }); log("lock: broke a stale lock"); continue; }
      } catch { continue; } // vanished between the two calls: try again
      if (Date.now() - started > 20 * 60000) { log("lock: still held after 20 min — proceeding"); return () => {}; }
      await new Promise((r) => setTimeout(r, 1000));
    }
  }
}

function runChild(inIfc, outFrag) {
  return new Promise((resolve) => {
    const started = Date.now();
    const child = spawn(process.execPath, [process.argv[1], "--one", inIfc, outFrag], {
      windowsHide: true, stdio: ["ignore", "pipe", "pipe"],
    });
    let out = "", err = "";
    child.stdout.on("data", (d) => (out += d));
    child.stderr.on("data", (d) => (err += d));
    const timer = setTimeout(() => child.kill(), CHILD_TIMEOUT_MS);
    child.on("close", (code) => {
      clearTimeout(timer);
      const seconds = Math.round((Date.now() - started) / 100) / 10;
      const last = out.trim().split(/\r?\n/).pop() ?? "";
      if (code === 0) {
        try { resolve({ status: "converted", ...JSON.parse(last) }); return; } catch { /* fall through */ }
      }
      const timedOut = Date.now() - started >= CHILD_TIMEOUT_MS;
      resolve({
        status: "failed", seconds,
        error: timedOut ? `timed out after ${CHILD_TIMEOUT_MS / 60000} min` : firstLine(err) || `exit code ${code}`,
      });
    });
  });
}

/** Copies each converted model into latest\<Model>\ (swap, so a reader never sees a half-copied
 *  folder) and rewrites latest\index.json. Models that failed this run keep their previous entry. */
function refreshLatest(manifest, results, log) {
  const latest = path.join(manifest.outputRoot, "latest");
  mkdirSync(latest, { recursive: true });
  const indexPath = path.join(latest, "index.json");
  const index = existsSync(indexPath) ? JSON.parse(readFileSync(indexPath, "utf8")) : { schema: 1, models: [] };

  for (const m of manifest.models ?? []) {
    const r = results.find((x) => x.name === m.name);
    if (!r || r.status !== "converted") continue;

    const target = path.join(latest, safeName(m.name));
    const staging = target + ".new", old = target + ".old";
    rmSync(staging, { recursive: true, force: true });
    mkdirSync(staging, { recursive: true });
    for (const f of readdirSync(m.folder)) copyFileSync(path.join(m.folder, f), path.join(staging, f));
    rmSync(old, { recursive: true, force: true });
    if (existsSync(target)) renameSync(target, old);
    renameSync(staging, target);
    rmSync(old, { recursive: true, force: true });

    const entry = {
      name: m.name, folder: safeName(m.name), runStamp: manifest.runStamp,
      frag: r.frag, ifc: m.ifc, idmap: "idmap.csv", elements: m.elementsCsv || "", rooms: m.roomsCsv || "",
      fragBytes: r.fragBytes, updated: new Date().toISOString(),
    };
    index.models = (index.models ?? []).filter((x) => x.name !== m.name).concat(entry)
      .sort((a, b) => a.name.localeCompare(b.name));
  }
  index.updated = new Date().toISOString();
  writeJson(indexPath, index);
  log(`latest: ${index.models.length} model(s) in index`);
}

/** Deletes runs\<yyyyMMdd_HHmmss> folders older than keepRunsDays. Only folders whose NAME is a
 *  run stamp are touched, and never the current run. */
function pruneRuns(manifest, log) {
  const keepDays = Number(manifest.keepRunsDays ?? 14);
  if (!(keepDays > 0)) return;
  const runsDir = path.join(manifest.outputRoot, "runs");
  const cutoff = Date.now() - keepDays * 86400000;
  for (const name of readdirSync(runsDir)) {
    if (!RUN_FOLDER.test(name) || name === manifest.runStamp) continue;
    const d = new Date(+name.slice(0, 4), +name.slice(4, 6) - 1, +name.slice(6, 8));
    if (d.getTime() < cutoff) {
      rmSync(path.join(runsDir, name), { recursive: true, force: true });
      log(`prune: removed runs\\${name}`);
    }
  }
}

function waitForExit(pid, log) {
  const started = Date.now();
  log(`waiting for process ${pid} to exit`);
  return new Promise((resolve) => {
    const tick = () => {
      let alive = true;
      try { process.kill(pid, 0); } catch { alive = false; } // signal 0 only tests existence
      if (!alive) { log(`process ${pid} exited`); resolve(); return; }
      if (Date.now() - started > WAIT_PID_MAX_MS) { log(`process ${pid} still running after 4 h — converting anyway`); resolve(); return; }
      setTimeout(tick, 5000);
    };
    tick();
  });
}

// ---------------------------------------------------------------- helpers

function scriptDir() { return path.dirname(process.argv[1]); }
function firstLine(s) { return (s || "").trim().split(/\r?\n/).find((l) => l && !/DeprecationWarning|--trace-deprecation/.test(l)) ?? ""; }
function safeName(s) { return s.replace(/[<>:"/\\|?*\x00-\x1f]/g, "_").trim() || "model"; }
function writeJson(p, obj) { const tmp = p + ".tmp"; writeFileSync(tmp, JSON.stringify(obj, null, 2)); renameSync(tmp, p); }

async function main(argv) {
  const i = (flag) => argv.indexOf(flag);
  if (i("--one") >= 0) {
    const [inIfc, outFrag] = argv.slice(i("--one") + 1);
    const r = await convertOne(inIfc, outFrag);
    process.stdout.write(JSON.stringify(r) + "\n");
    return 0;
  }
  if (i("--manifest") >= 0) {
    const manifest = argv[i("--manifest") + 1];
    const waitPid = i("--wait-pid") >= 0 ? Number(argv[i("--wait-pid") + 1]) : 0;
    await runManifest(manifest, waitPid);
    return 0;
  }
  process.stderr.write("usage: ifc2frag --manifest <manifest.json> [--wait-pid <pid>] | --one <in.ifc> <out.frag>\n");
  return 2;
}

main(process.argv.slice(2)).then(
  (code) => process.exit(code),
  (e) => { process.stderr.write(String(e?.stack || e) + "\n"); process.exit(1); },
);
