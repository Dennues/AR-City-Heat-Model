// heatmap api that calls python script to generate heatmaps
// POST /api/heatmap/generate - generate heatmap with coordinates
// GET /api/heatmap/status - get generation status
// GET /api/heatmap/list-existing - list existing heatmaps (defaults + generated)

import { spawn } from "child_process";
import { join, dirname } from "path";
import { existsSync, readFileSync, mkdirSync, renameSync, readdirSync } from "fs";
import express from "express";
import { fileURLToPath } from "url";
import { randomUUID } from "crypto";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const router = express.Router();

// cors headers
router.use((req, res, next) => {
  res.header("Access-Control-Allow-Origin", "*");
  res.header("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  res.header("Access-Control-Allow-Headers", "Content-Type");
  if (req.method === "OPTIONS") {
    return res.sendStatus(200);
  }
  next();
});

// Get configuration from app
router.use((req, res, next) => {
  const app = req.app;
  req.generatedDir = app.get("generatedDir");
  req.nodeEnv = app.get("nodeEnv");
  next();
});

// Paths
const PROJECT_ROOT = join(__dirname, "..");
const resolveHeatmapScript = () => {
  const envPath = process.env.HEATMAP_SCRIPT;
  const candidates = [];
  if (envPath) candidates.push(envPath);

  candidates.push(join(PROJECT_ROOT, "Heatmaps", "col_map_polygons.py"));
  candidates.push(join(PROJECT_ROOT, "M02 Semester", "Heatmaps", "col_map_polygons.py"));
  candidates.push(join(PROJECT_ROOT, "scripts", "col_map_polygons.py"));
  candidates.push(join(PROJECT_ROOT, "server", "col_map_polygons.py"));

  for (const p of candidates) {
    if (p && existsSync(p)) {
      return p;
    }
  }
  return null;
};
const DEFAULT_HEATMAPS_DIR = join(PROJECT_ROOT, "public", "default-heatmaps");

// Ensure default heatmaps directory exists
if (!existsSync(DEFAULT_HEATMAPS_DIR)) {
  mkdirSync(DEFAULT_HEATMAPS_DIR, { recursive: true });
  console.log(`[heatmap-api] Created default heatmaps dir: ${DEFAULT_HEATMAPS_DIR}`);
}

// server data directory (place csvs)
const SERVER_DATA_DIR = join(PROJECT_ROOT, "data");

// map known timeId values to server-local CSV files inside the data folder
// add or edit entries for new CSVs
const TIMEID_TO_CSV = {
  // example mappings, update filenames to match the files one placed in server/data/
  "6am": join(SERVER_DATA_DIR, "temp1Juni2025_6uhr.csv"),
  "4pm": join(SERVER_DATA_DIR, "temp1Juni2025_16uhr.csv"),
};

let requestStatuses = {};

setInterval(() => {
  const oneHourAgo = Date.now() - 3600000;
  for (const [requestId, status] of Object.entries(requestStatuses)) {
    if (status.lastRun && new Date(status.lastRun).getTime() < oneHourAgo) {
      delete requestStatuses[requestId];
    }
  }
}, 600000);

// get file from either temp or defaults
const getHeatmapFile = (filename, generatedDir) => {
  const tempPath = join(generatedDir, filename);
  if (existsSync(tempPath)) {
    return { path: tempPath, url: `/tmp/${filename}`, source: "temp" };
  }
  const defaultPath = join(DEFAULT_HEATMAPS_DIR, filename);
  if (existsSync(defaultPath)) {
    return { path: defaultPath, url: `/default-heatmaps/${filename}`, source: "default" };
  }
  return { path: null, url: null, source: null };
};

router.post("/generate", async (req, res) => {
  try {
    const requestId = randomUUID();
    console.log(`[heatmap-api] POST /generate [${requestId}] - Body:`, JSON.stringify(req.body));
    const { latMin, latMax, lonMin, lonMax, colorMode, enableGapFill, scaleMode, vmin, vmax, cropCoords, upscaleFactor, tempCsv, timeId } = req.body;
    if (!latMin || !latMax || !lonMin || !lonMax) {
      console.log(`[heatmap-api] Validation failed [${requestId}] - missing coords:`, { latMin, latMax, lonMin, lonMax });
      return res.status(400).json({
        error: "Misses coordinates: need latMin, latMax, lonMin, lonMax",
      });
    }

    requestStatuses[requestId] = {
      status: "running",
      message: "Generiere Heatmap ...",
      lastRun: new Date().toISOString(),
      error: null,
    };

    const scriptPath = resolveHeatmapScript();
    if (!scriptPath) {
      console.error("[Heatmap] Python script not found in any candidate locations. Searched common paths.");
      lastGenerationStatus = {
        status: "error",
        message: "heatmap script not found on server",
        lastRun: new Date().toISOString(),
        error: "heatmap script not found",
      };
      return res.status(500).json({ error: "Heatmap generation script not found on server. Set HEATMAP_SCRIPT or add the script to the repository." });
    }
    console.log(`[Heatmap] Using heatmap script: ${scriptPath}`);

    const modePrefix = colorMode === "smooth" ? "heatmap-smooth" : "heatmap-polygon";
    const timeTag = timeId || "default";
    const outPng = join(req.generatedDir, `${modePrefix}-${timeTag}-${requestId}.png`);
    const outGeoJson = join(req.generatedDir, `${modePrefix}-${timeTag}-${requestId}.geojson`);
    const benchesOut = join(req.generatedDir, `benches-${timeTag}-${requestId}.geojson`);
    
    // use cache osm
    let featuresCache = join(req.generatedDir, "osm-features-cache.pkl");
    if (!existsSync(featuresCache)) {
      const publicCache = join(DEFAULT_HEATMAPS_DIR, "osm-features-cache.pkl");
      if (existsSync(publicCache)) {
        featuresCache = publicCache;
      } else {
        const rootCache = join(PROJECT_ROOT, "osm-features-cache.pkl");
        if (existsSync(rootCache)) {
          featuresCache = rootCache;
        } else {
          const dataCache = join(PROJECT_ROOT, "data", "osm-features-cache.pkl");
          if (existsSync(dataCache)) {
            featuresCache = dataCache;
          }
        }
      }
    }
    
    const args = [
      scriptPath,
      "--lat-min", latMin.toString(),
      "--lat-max", latMax.toString(),
      "--lon-min", lonMin.toString(),
      "--lon-max", lonMax.toString(),
      "--png-out", outPng,
      "--geojson-out", outGeoJson,
      "--benches-out", benchesOut,
      "--minimal",
      "--features-cache-in", featuresCache,
      "--features-cache-out", featuresCache,
    ];

    console.log(`[Heatmap] Received: colorMode=${colorMode}, scaleMode=${scaleMode}, vmin=${vmin}, vmax=${vmax}`);

    if (colorMode === "polygon") {
      args.push("--with-coloring");
    } else if (colorMode === "smooth") {
      args.push("--smooth-coloring");
      args.push("--no-split");
    }

    if (enableGapFill) {
      args.push("--fill-gaps");
    }

    if (scaleMode) {
      args.push("--scale-mode", String(scaleMode));
      console.log(`[Heatmap] Passing scale-mode: ${scaleMode}`);
    }
    if (typeof vmin === "number" && !Number.isNaN(vmin)) {
      args.push("--vmin", String(vmin));
      console.log(`[Heatmap] Passing vmin: ${vmin}`);
    }
    if (typeof vmax === "number" && !Number.isNaN(vmax)) {
      args.push("--vmax", String(vmax));
      console.log(`[Heatmap] Passing vmax: ${vmax}`);
    }

    if (cropCoords) {
      console.log(`[Heatmap] (info) cropCoords provided but ignored in API path; using borderless image`);
    }
    if (typeof upscaleFactor === "number" && upscaleFactor > 0) {
      console.log(`[Heatmap] (info) upscaleFactor provided but ignored in API path; using 1:1 size`);
    }

    // prefer server-side csvs mapped by timeId when available
    let chosenCsv = null;
    if (timeId && TIMEID_TO_CSV[timeId]) {
      const candidate = TIMEID_TO_CSV[timeId];
      if (existsSync(candidate)) {
        chosenCsv = candidate;
        console.log(`[Heatmap] Using server-side CSV for timeId='${timeId}': ${candidate}`);
      } else {
        console.warn(`[Heatmap] Mapped CSV for timeId='${timeId}' not found: ${candidate}`);
      }
    }

    if (!chosenCsv && tempCsv) {
      chosenCsv = tempCsv.toString();
      console.log(`[Heatmap] Using client-provided temp-csv: ${chosenCsv}`);
    }

    if (chosenCsv) {
      args.push("--temp-csv", chosenCsv);
    }

    console.log(`[Heatmap] Final args: ${args.join(" ")}`);

    return new Promise((resolve) => {
      const proc = spawn("python", args, { shell: false, stdio: "pipe" });

      let responded = false;

      let stdout = "";
      let stderr = "";

      proc.stdout?.on("data", (data) => {
        stdout += data.toString();
        console.log(`[Heatmap] ${data}`);
      });

      proc.stderr?.on("data", (data) => {
        stderr += data.toString();
        console.error(`[Heatmap Error] ${data}`);
      });

      proc.on("close", (code) => {
        if (code === 0) {
          try {
            const files = readdirSync(req.generatedDir);
            const pngFile = files.find(f => f.endsWith('.png') && !f.includes('-6am') && !f.includes('-4pm') && f !== 'heatmap-smooth.png' && f !== 'heatmap-polygon.png');
            if (pngFile) {
              const oldPath = join(req.generatedDir, pngFile);
              const newPath = join(req.generatedDir, `${modePrefix}-${timeTag}.png`);
              if (oldPath !== newPath) {
                renameSync(oldPath, newPath);
                console.log(`[Heatmap] renamed: ${pngFile} -> ${modePrefix}-${timeTag}.png`);
              }
            }
          } catch (err) {
            console.error(`[Heatmap] rename error: ${err.message}`);
          }

          requestStatuses[requestId] = {
            status: "success",
            message: "heatmap generated",
            lastRun: new Date().toISOString(),
            error: null,
          };
          if (!responded) {
            responded = true;
            res.json({
            requestId,
            success: true,
            message: "heatmap generated",
            colorMode: colorMode,
            timeId: timeTag,
            pngUrl: `/tmp/${modePrefix}-${timeTag}-${requestId}.png?t=${Date.now()}`,
            geojsonUrl: `/tmp/${modePrefix}-${timeTag}-${requestId}.geojson?t=${Date.now()}`,
            benchesUrl: `/tmp/benches-${timeTag}-${requestId}.geojson?t=${Date.now()}`,
            stdout,
            });
          }
        } else {
          requestStatuses[requestId] = {
            status: "error",
            message: `python script exited with code ${code}`,
            lastRun: new Date().toISOString(),
            error: stderr || stdout,
          };
          if (!responded) {
            responded = true;
            res.status(500).json({
              error: `Python script failed with code ${code}`,
              stderr,
              stdout,
            });
          }
        }
        resolve();
      });

      proc.on("error", (err) => {
        requestStatuses[requestId] = {
          status: "error",
          message: `spawn error: ${err.message}`,
          lastRun: new Date().toISOString(),
          error: err.message,
        };
        if (!responded) {
          responded = true;
          res.status(500).json({ error: err.message });
        }
        resolve();
      });
    });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

router.get("/status/:requestId", (req, res) => {
  const { requestId } = req.params;
  const status = requestStatuses[requestId];
  if (!status) {
    return res.status(404).json({ error: `Request ${requestId} not found` });
  }
  res.json({ requestId, ...status });
});

// List all available heatmaps (both generated and defaults)
router.get("/list-existing", (req, res) => {
  const heatmaps = { generated: [], defaults: [] };
  
  try {
    // List generated heatmaps
    if (existsSync(req.generatedDir)) {
      const files = readdirSync(req.generatedDir);
      const pngFiles = files.filter(f => f.endsWith('.png'));
      pngFiles.forEach(f => {
        const match = f.match(/^(heatmap-(?:smooth|polygon))-(.+?)(?:-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\.png$/);
        if (match) {
          const mode = match[1] === "heatmap-smooth" ? "smooth" : "polygon";
          const timeId = match[2];
          const uuidPart = f.match(/-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.png$/)?.[0] || ".png";
          heatmaps.generated.push({
            mode,
            timeId,
            pngUrl: `/tmp/${f}`,
            geojsonUrl: `/tmp/${f.replace('.png', '.geojson')}`,
            benchesUrl: `/tmp/benches-${timeId}${uuidPart}`,
          });
        }
      });
    }
    
    // List default heatmaps
    if (existsSync(DEFAULT_HEATMAPS_DIR)) {
      const files = readdirSync(DEFAULT_HEATMAPS_DIR);
      const pngFiles = files.filter(f => f.endsWith('.png'));
      pngFiles.forEach(f => {
        const match = f.match(/^(heatmap-(?:smooth|polygon))-(.+?)(?:-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})?\.png$/);
        if (match) {
          const mode = match[1] === "heatmap-smooth" ? "smooth" : "polygon";
          const timeId = match[2];
          heatmaps.defaults.push({
            mode,
            timeId,
            pngUrl: `/default-heatmaps/${f}`,
            geojsonUrl: `/default-heatmaps/${f.replace('.png', '.geojson')}`,
            benchesUrl: `/default-heatmaps/benches-${timeId}.geojson`,
          });
        }
      });
    }
  } catch (err) {
    console.error(`[heatmap-api] Error listing heatmaps: ${err.message}`);
  }
  
  res.json(heatmaps);
});

export { router as heatmapRouter };
