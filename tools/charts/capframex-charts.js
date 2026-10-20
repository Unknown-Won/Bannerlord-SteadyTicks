// Builds comparison charts (HTML/SVG) from two CapFrameX capture files, for the Nexus page and README.
// Usage: node capframex-charts.js <with.json> <without.json> <outDir>
// Then screenshot each HTML at 1200x620, for example with headless Chrome (see render.ps1).
// Numbers follow CapFrameX: all runs of a capture are pooled; average FPS from presents, percentiles from display changes.
const fs = require("fs"), path = require("path");
const [withFile, withoutFile, outDir] = process.argv.slice(2);

function load(file) {
  const j = JSON.parse(fs.readFileSync(file, "utf8").replace(/^﻿/, ""));
  // Runs are placed back to back; the time axis is the running sum of display frame times.
  let presents = [], display = [], times = [], offset = 0;
  for (const run of j.Runs) {
    const cd = run.CaptureData;
    presents = presents.concat(cd.MsBetweenPresents);
    for (const ms of cd.MsBetweenDisplayChange) {
      if (ms > 0) {
        offset += ms / 1000;
        display.push(ms);
        times.push(offset);
      }
    }
  }
  const sorted = [...display].sort((a, b) => a - b), n = sorted.length;
  // Frame time at quantile q, linearly interpolated (as CapFrameX does).
  const at = q => { const x = q * (n - 1), i = Math.floor(x); return sorted[i] + (sorted[Math.min(n - 1, i + 1)] - sorted[i]) * (x - i); };
  return { display, times, at, avg: 1000 * presents.length / presents.reduce((a, b) => a + b, 0), seconds: offset, runs: j.Runs.length };
}

const A = load(withFile), B = load(withoutFile);
const WITH = "#0066ff", WITHOUT = "#8a94a3", W = 1200, H = 620;
const SUB = `CapFrameX, ${A.runs} runs of ${Math.round(A.seconds / A.runs)} s each per setup, same save. Ryzen 9 5900X, RTX 4090, large mod list.`;
const pct = (a, b) => { const v = (a / b - 1) * 100; return (v >= 0 ? "+" : "") + v.toFixed(Math.abs(v) < 10 ? 1 : 0) + "%"; };
const f1 = v => v.toFixed(1);
const legend = `<rect x="40" y="100" width="16" height="16" fill="${WITH}"/><text x="62" y="114" font-size="16">With Steady Ticks</text>
<rect x="220" y="100" width="16" height="16" fill="${WITHOUT}"/><text x="242" y="114" font-size="16">Without Steady Ticks</text>`;
const header = (title, note) => `<text x="40" y="52" font-size="28" font-weight="700">${title}</text>
<text x="40" y="82" font-size="15" fill="#666">${SUB} ${note}</text>`;
const write = (name, svg) => fs.writeFileSync(path.join(outDir, name),
  `<!doctype html><html><head><meta charset="utf-8"><style>body{margin:0;background:#fafafa}text{font-family:'Segoe UI',Arial,sans-serif;fill:#1e1e1e}</style></head>` +
  `<body><svg width="${W}" height="${H}" xmlns="http://www.w3.org/2000/svg">${svg}</svg></body></html>`);

// 1. Average FPS and lows (higher is better).
{
  const metrics = [
    ["Average FPS", A.avg, B.avg],
    ["P5 FPS", 1000 / A.at(0.95), 1000 / B.at(0.95)],
    ["1% percentile FPS", 1000 / A.at(0.99), 1000 / B.at(0.99)],
    ["0.2% percentile FPS", 1000 / A.at(0.998), 1000 / B.at(0.998)],
    ["0.1% percentile FPS", 1000 / A.at(0.999), 1000 / B.at(0.999)],
  ];
  const left = 250, maxW = 760, max = Math.max(A.avg, B.avg) * 1.1;
  let y = 145, s = header("FPS: average and lows", "Higher is better.") + legend;
  for (const [name, a, b] of metrics) {
    s += `<text x="40" y="${y + 38}" font-size="17" font-weight="600">${name}</text>`;
    s += `<rect x="${left}" y="${y}" width="${maxW * a / max}" height="26" fill="${WITH}"/>`;
    s += `<text x="${left + maxW * a / max + 10}" y="${y + 19}" font-size="16" font-weight="700">${f1(a)}  (${pct(a, b)})</text>`;
    s += `<rect x="${left}" y="${y + 30}" width="${maxW * b / max}" height="26" fill="${WITHOUT}"/>`;
    s += `<text x="${left + maxW * b / max + 10}" y="${y + 49}" font-size="16">${f1(b)}</text>`;
    y += 92;
  }
  write("1-summary.html", s);
}

// 2. Stutters: number of frames slower than each threshold (lower is better).
{
  const limits = [50, 100, 250, 500];
  const count = (D, t) => D.display.filter(x => x > t).length;
  const slowTime = D => D.display.filter(x => x > 50).reduce((a, b) => a + b, 0) / 1000;
  const ca = limits.map(t => count(A, t)), cb = limits.map(t => count(B, t));
  const max = Math.max(...ca, ...cb) * 1.15, bottom = 500, hMax = 320, bw = 90;
  let s = header("Stutters: frames slower than ...", "Lower is better.") + legend;
  limits.forEach((t, i) => {
    const x = 120 + i * 260, ha = hMax * ca[i] / max, hb = hMax * cb[i] / max;
    s += `<rect x="${x}" y="${bottom - ha}" width="${bw}" height="${ha}" fill="${WITH}"/>`;
    s += `<text x="${x + bw / 2}" y="${bottom - ha - 10}" font-size="18" font-weight="700" text-anchor="middle">${ca[i]}</text>`;
    s += `<rect x="${x + bw + 8}" y="${bottom - hb}" width="${bw}" height="${hb}" fill="${WITHOUT}"/>`;
    s += `<text x="${x + bw * 1.5 + 8}" y="${bottom - hb - 10}" font-size="18" text-anchor="middle">${cb[i]}</text>`;
    s += `<text x="${x + bw + 4}" y="${bottom + 30}" font-size="18" font-weight="600" text-anchor="middle">over ${t} ms</text>`;
    s += `<text x="${x + bw + 4}" y="${bottom + 54}" font-size="16" font-weight="600" text-anchor="middle" style="fill:#2e7d32">${pct(ca[i], cb[i])}</text>`;
  });
  s += `<line x1="100" y1="${bottom}" x2="1140" y2="${bottom}" stroke="#bbb"/>`;
  s += `<text x="40" y="150" font-size="16">Time spent in frames over 50 ms: <tspan font-weight="700">${f1(slowTime(A))} s</tspan> with, <tspan font-weight="700">${f1(slowTime(B))} s</tspan> without (${pct(slowTime(A), slowTime(B))})</text>`;
  write("2-stutters.html", s);
}

// 3. Frame times over the whole capture, one panel each on the same scale (lower and flatter is better).
{
  const yMax = 1000, left = 110, w = 1050, h = 190;
  let s = header("Frame times over the whole capture", "Each spike is a stutter. Scale capped at 1 s.");
  [[A, "With Steady Ticks", WITH], [B, "Without Steady Ticks", WITHOUT]].forEach(([D, name, color], k) => {
    const top = 130 + k * 240;
    s += `<text x="40" y="${top - 10}" font-size="17" font-weight="600">${name}</text>`;
    s += `<rect x="${left}" y="${top}" width="${w}" height="${h}" fill="#fff" stroke="#ddd"/>`;
    for (const v of [0, 250, 500, 750, 1000]) {
      const yy = top + h - h * v / yMax;
      s += `<line x1="${left}" y1="${yy}" x2="${left + w}" y2="${yy}" stroke="#eee"/><text x="${left - 8}" y="${yy + 5}" font-size="12" text-anchor="end" style="fill:#666">${v} ms</text>`;
    }
    // One point per pixel column: the slowest frame in that slice, so no spike is lost.
    const cols = new Array(w).fill(0);
    D.times.forEach((t, i) => { const c = Math.min(w - 1, Math.floor(t / D.seconds * w)); cols[c] = Math.max(cols[c], D.display[i]); });
    const d = cols.map((v, c) => (c ? "L" : "M") + (left + c) + "," + (top + h - h * Math.min(v, yMax) / yMax).toFixed(1)).join("");
    s += `<path d="${d}" fill="none" stroke="${color}" stroke-width="1.6"/>`;
    for (let sec = 0; sec <= D.seconds; sec += 30) {
      s += `<text x="${left + w * sec / D.seconds}" y="${top + h + 18}" font-size="12" text-anchor="middle" style="fill:#666">${sec} s</text>`;
    }
  });
  write("3-frametimes.html", s);
}

// 4. The slowest frames: FPS at each percentile from the slowest 10% to the slowest 0.1% (higher is better).
{
  const left = 120, top = 140, w = 1000, h = 380, yMax = 80;
  const X = q => left + w * (Math.log10(0.1) - Math.log10(q)) / 2, Y = v => top + h - h * Math.min(v, yMax) / yMax;
  let s = header("The slowest frames", "FPS at each percentile. Higher is better.") + legend;
  s += `<rect x="${left}" y="${top}" width="${w}" height="${h}" fill="#fff" stroke="#ddd"/>`;
  for (let v = 0; v <= yMax; v += 20) {
    s += `<line x1="${left}" y1="${Y(v)}" x2="${left + w}" y2="${Y(v)}" stroke="#eee"/><text x="${left - 10}" y="${Y(v) + 5}" font-size="13" text-anchor="end" style="fill:#666">${v} FPS</text>`;
  }
  for (const [q, label] of [[0.1, "10%"], [0.05, "5%"], [0.02, "2%"], [0.01, "1%"], [0.005, "0.5%"], [0.002, "0.2%"], [0.001, "0.1%"]]) {
    s += `<line x1="${X(q)}" y1="${top}" x2="${X(q)}" y2="${top + h}" stroke="#eee"/><text x="${X(q)}" y="${top + h + 22}" font-size="13" text-anchor="middle" style="fill:#666">${label}</text>`;
  }
  s += `<text x="${left + w / 2}" y="${top + h + 48}" font-size="14" text-anchor="middle" style="fill:#444">slowest share of frames</text>`;
  for (const [D, color] of [[B, WITHOUT], [A, WITH]]) {
    let d = "";
    for (let e = -1, i = 0; e >= -3.0001; e -= 0.02, i++) { const q = Math.pow(10, e); d += (i ? "L" : "M") + X(q).toFixed(1) + "," + Y(1000 / D.at(1 - q)).toFixed(1); }
    s += `<path d="${d}" fill="none" stroke="${color}" stroke-width="3"/>`;
  }
  write("4-percentiles.html", s);
}
console.log("charts written to " + outDir);
