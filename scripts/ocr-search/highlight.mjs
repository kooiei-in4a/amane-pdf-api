// Manual check of where PDF.js draws search highlights on an OCR output (Issue #116). Not run in CI.
// Run after changing OcrTextLayer's placement constants and before a release:
//   npm ci --ignore-scripts --omit=optional && npx playwright-core install chromium firefox
//   node highlight.mjs <ocr.pdf> <expected.json> [out_dir]
// expected.json maps each search term to its ink box on the first page in 300 dpi display pixels
// ([left, top, right, bottom]); use null when the position is unknown. Prints the IoU per browser and saves
// screenshots (highlight plus the expected box outlined in red) to out_dir. Exits 1 if a Chromium IoU is
// below --min (default 0.6). Firefox results are for visual review: its fallback font widens Latin and digits.
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { chromium, firefox } from "playwright-core";

const args = process.argv.slice(2);
const minIndex = args.indexOf("--min");
const minimum = minIndex >= 0 ? Number(args.splice(minIndex, 2)[1]) : 0.6;
const [pdfPath, expectedPath, outDir] = args;
const expected = JSON.parse(fs.readFileSync(expectedPath, "utf8"));
const here = path.dirname(new URL(import.meta.url).pathname);
const pdfjsRoot = path.join(here, "node_modules", "pdfjs-dist");
const types = { ".mjs": "text/javascript", ".css": "text/css", ".html": "text/html", ".bcmap": "application/octet-stream",
  ".svg": "image/svg+xml", ".wasm": "application/wasm" };

const server = http.createServer((request, response) => {
  const url = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
  let file = null;
  if (url === "/viewer.html") file = path.join(here, "viewer.html");
  else if (url === "/pdf") file = pdfPath;
  else if (url.startsWith("/pdfjs/")) {
    file = path.resolve(pdfjsRoot, "." + url.slice("/pdfjs".length));
    if (!file.startsWith(pdfjsRoot + path.sep)) file = null;
  }
  if (!file || !fs.existsSync(file) || !fs.statSync(file).isFile()) { response.writeHead(404); response.end(); return; }
  response.writeHead(200, { "Content-Type": url === "/pdf" ? "application/pdf" : types[path.extname(file)] ?? "application/octet-stream" });
  fs.createReadStream(file).pipe(response);
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const base = `http://127.0.0.1:${server.address().port}`;

const iou = (a, b) => {
  const w = Math.max(0, Math.min(a[2], b[2]) - Math.max(a[0], b[0])), h = Math.max(0, Math.min(a[3], b[3]) - Math.max(a[1], b[1]));
  const union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - w * h;
  return union > 0 ? w * h / union : 0;
};

let failed = false;
for (const [name, launcher] of [["chromium", chromium], ["firefox", firefox]]) {
  const browser = await launcher.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1400, height: 1000 }, deviceScaleFactor: 2, locale: "ja-JP" });
  await page.goto(`${base}/viewer.html`);
  await page.waitForSelector("body[data-ready='1']", { timeout: 60000 });
  for (const [term, box] of Object.entries(expected)) {
    const drawn = await page.evaluate(query => window.ocrCheck.highlight(query), term);
    const score = drawn && box ? iou(drawn, box) : null;
    if (name === "chromium" && box && (score ?? 0) < minimum) failed = true;
    console.log(JSON.stringify({ browser: name, term, iou: score === null ? null : +score.toFixed(2),
      highlight: drawn?.map(Math.round) ?? null }));
    if (outDir && (drawn || box)) {
      fs.mkdirSync(outDir, { recursive: true });
      const [x, y, width, height] = await page.evaluate(b => window.ocrCheck.outline(b), box ?? drawn);
      await page.screenshot({ path: path.join(outDir, `${name}-${term.replace(/[^\p{L}\p{N}]+/gu, "_")}.png`),
        clip: { x: Math.max(0, x - 40), y: Math.max(0, y - 40), width: width + 80, height: height + 80 } });
    }
  }
  await browser.close();
}
server.close();
process.exit(failed ? 1 : 0);
