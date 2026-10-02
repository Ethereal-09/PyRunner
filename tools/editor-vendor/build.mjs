import { build } from "esbuild";
import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";

const root = resolve(import.meta.dirname, "../..");
const output = resolve(root, "PyRunner/Editor/wwwroot/codemirror.bundle.js");
await mkdir(dirname(output), { recursive: true });
await build({
  entryPoints: [resolve(import.meta.dirname, "editor-entry.js")],
  outfile: output,
  bundle: true,
  minify: true,
  platform: "browser",
  target: ["chrome109"],
  legalComments: "none",
  sourcemap: false,
});
const generated = await readFile(output, "utf8");
await writeFile(output, generated.replace(/[ \t]+$/gm, ""));
const bytes = await readFile(output);
const sha256 = createHash("sha256").update(bytes).digest("hex").toUpperCase();
await writeFile(resolve(root, "PyRunner/Editor/wwwroot/codemirror.bundle.sha256"), `${sha256}  codemirror.bundle.js\n`);
const manifestPath = resolve(root, "PyRunner/Editor/wwwroot/vendor-manifest.json");
const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
manifest.bundleSha256 = sha256;
await writeFile(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
console.log(`${bytes.length} bytes ${sha256}`);
