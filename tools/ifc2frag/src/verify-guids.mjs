// Dev check: do the IfcGuid values KH-Tool writes to elements.csv match the GlobalIds inside the IFC?
// The report joins the 3D model to the element table on this key, so a low match rate means
// clicks in the table won't find the element in 3D.
//
//   node src/verify-guids.mjs <model.ifc> <elements.csv>

import { readFileSync } from "node:fs";
import path from "node:path";
import * as WebIFC from "web-ifc";

const [, , ifcPath, csvPath] = process.argv;
if (!ifcPath || !csvPath) { console.error("usage: verify-guids <model.ifc> <elements.csv>"); process.exit(2); }

const api = new WebIFC.IfcAPI();
api.SetWasmPath(path.join(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1")), "..", "node_modules", "web-ifc") + path.sep, true);
await api.Init();
const modelId = api.OpenModel(new Uint8Array(readFileSync(ifcPath)));

// Every IfcProduct-derived entity with a GlobalId (walls, doors, spaces, ...).
const ifcGuids = new Set();
for (const type of api.GetIfcEntityList(modelId)) {
  if (!api.IsIfcElement(type) && type !== WebIFC.IFCSPACE) continue;
  const ids = api.GetLineIDsWithType(modelId, type);
  for (let i = 0; i < ids.size(); i++) {
    const line = api.GetLine(modelId, ids.get(i));
    if (line?.GlobalId?.value) ifcGuids.add(line.GlobalId.value);
  }
}
api.CloseModel(modelId);

const rows = readFileSync(csvPath, "utf8").replace(/^﻿/, "").split(/\r?\n/).filter(Boolean);
const header = rows.shift().split(",");
const col = header.indexOf("IfcGuid");
if (col < 0) { console.error("elements.csv has no IfcGuid column"); process.exit(2); }
const catCol = header.indexOf("Category");
const parsed = rows.map((r) => {
  const cells = r.split(",");
  return { guid: (cells[col] ?? "").replace(/"/g, ""), cat: catCol >= 0 ? (cells[catCol] ?? "").replace(/"/g, "") : "" };
}).filter((x) => x.guid);
const csvGuids = parsed.map((x) => x.guid);

const matched = csvGuids.filter((g) => ifcGuids.has(g)).length;
const pct = csvGuids.length ? (100 * matched / csvGuids.length) : 0;

// Which categories miss — "not exported at all" (lines, sketches) is harmless; a category that
// IS in the IFC but under different GUIDs is the bug to chase.
const byCat = {};
for (const x of parsed) {
  const c = (byCat[x.cat || "?"] ??= { rows: 0, missing: 0 });
  c.rows++;
  if (!ifcGuids.has(x.guid)) c.missing++;
}
const missingByCategory = Object.fromEntries(Object.entries(byCat)
  .filter(([, v]) => v.missing > 0).sort((a, b) => b[1].missing - a[1].missing));

console.log(JSON.stringify({
  ifcElements: ifcGuids.size, csvRows: csvGuids.length, matched,
  matchPercent: +pct.toFixed(2),
  missingByCategory,
  csvNotInIfc: csvGuids.filter((g) => !ifcGuids.has(g)).slice(0, 5),
}, null, 2));
process.exit(pct >= 99 ? 0 : 1);
