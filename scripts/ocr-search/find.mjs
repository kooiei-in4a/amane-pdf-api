// Counts matches with PDF.js's own viewer search (PDFFindController), as a viewer user would find them.
// usage: node find.mjs <pdf> <terms.json>  ->  {"term": matches, ...} on stdout
// PDFFindController lives in the viewer bundle, which expects a few browser globals at import time.
import fs from "node:fs";

const style = new Proxy({}, { get: (target, key) => key === "setProperty" ? () => {} : target[key] });
globalThis.document ??= { documentElement: { style }, createElement: () => ({ style }), addEventListener() {}, removeEventListener() {} };
globalThis.window ??= globalThis;
globalThis.requestAnimationFrame ??= callback => setTimeout(callback, 0);
globalThis.matchMedia ??= () => ({ matches: false, addEventListener() {}, removeEventListener() {} });
const pdfjsLib = await import("pdfjs-dist/legacy/build/pdf.mjs");
globalThis.pdfjsLib = pdfjsLib;
const { EventBus, PDFFindController } = await import("pdfjs-dist/web/pdf_viewer.mjs");

const [pdfPath, termsPath] = process.argv.slice(2);
const terms = JSON.parse(fs.readFileSync(termsPath, "utf8"));
const doc = await pdfjsLib.getDocument({ data: new Uint8Array(fs.readFileSync(pdfPath)), isEvalSupported: false }).promise;

async function count(query) {
  // A fresh controller per query: no event reports a search that finds nothing, so wait until every page
  // has its match list instead.
  const eventBus = new EventBus();
  const linkService = { pagesCount: doc.numPages, page: 1, rotation: 0, goToPage() {}, scrollPageIntoView() {} };
  const finder = new PDFFindController({ linkService, eventBus });
  finder.setDocument(doc);
  eventBus.dispatch("find", { source: null, type: "", query, caseSensitive: false, entireWord: false,
    highlightAll: true, findPrevious: false, matchDiacritics: false });
  const deadline = Date.now() + 30000;
  while (finder.pageMatches.length < doc.numPages || finder.pageMatches.some(matches => !matches)) {
    if (Date.now() > deadline) throw new Error("PDF.js search timed out");
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  return finder.pageMatches.reduce((total, matches) => total + matches.length, 0);
}

const result = {};
for (const query of terms) result[query] = await count(query);
process.stdout.write(JSON.stringify(result) + "\n");
process.exit(0);
