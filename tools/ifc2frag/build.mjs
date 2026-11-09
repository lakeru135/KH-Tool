// Bundles src/ifc2frag.mjs into dist/ so KH-Tool can ship it without node_modules:
//   dist/ifc2frag.cjs        the converter (fragments + three + web-ifc bundled in)
//   dist/web-ifc-node.wasm   loaded at run time from the script's own folder
//   dist/licenses/*          third-party licence texts (fragments/three: MIT, web-ifc: MPL-2.0)
//   dist/versions.json       what was bundled — the report viewer must use the same fragments version
// node.exe is NOT part of dist; install/fetch-node.ps1 supplies a pinned, hash-checked copy.

import { build } from "esbuild";
import { copyFileSync, mkdirSync, readFileSync, rmSync, writeFileSync, existsSync } from "node:fs";
import path from "node:path";

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1"));
const dist = path.join(here, "dist");

rmSync(dist, { recursive: true, force: true });
mkdirSync(path.join(dist, "licenses"), { recursive: true });

await build({
  entryPoints: [path.join(here, "src", "ifc2frag.mjs")],
  outfile: path.join(dist, "ifc2frag.cjs"),
  bundle: true,
  platform: "node",
  format: "cjs",
  target: "node24",
  minify: true,
  legalComments: "none",
  logLevel: "warning",
});

// Not require.resolve(`${name}/package.json`): web-ifc and three don't export it.
const pkgDir = (name) => path.join(here, "node_modules", ...name.split("/"));
copyFileSync(path.join(pkgDir("web-ifc"), "web-ifc-node.wasm"), path.join(dist, "web-ifc-node.wasm"));

const versions = {};
for (const name of ["@thatopen/fragments", "three", "web-ifc"]) {
  const dir = pkgDir(name);
  const pkg = JSON.parse(readFileSync(path.join(dir, "package.json"), "utf8"));
  versions[name] = pkg.version;
  const target = path.join(dist, "licenses", `${name.replace("/", "-").replace("@", "")}.LICENSE.txt`);
  const lic = ["LICENSE", "LICENSE.md", "LICENSE.txt"].map((f) => path.join(dir, f)).find(existsSync);
  if (lic) copyFileSync(lic, target);
  // @thatopen/fragments ships no licence file, only "license": "MIT" in package.json.
  else writeFileSync(target, `${name} ${pkg.version}\nLicence: ${pkg.license}\nSource: ${pkg.repository?.url ?? pkg.homepage ?? "npm"}\n`);
}
writeFileSync(path.join(dist, "versions.json"), JSON.stringify({ built: new Date().toISOString(), ...versions }, null, 2));
console.log("dist ready:", versions);
