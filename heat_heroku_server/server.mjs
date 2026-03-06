// simple express server for needle + heatmap api
// start with: node server.mjs

import express from "express";
import { fileURLToPath } from "url";
import { dirname, join } from "path";
import { heatmapRouter } from "./server/heatmap-api.js";
import { existsSync, mkdirSync } from "fs";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const app = express();
const PORT = process.env.PORT || 3001;
const NODE_ENV = process.env.NODE_ENV || "development";

// Determine output directories based on environment
const IS_PRODUCTION = NODE_ENV === "production";
const TEMP_DIR = IS_PRODUCTION ? (process.env.TMPDIR || "/tmp") : "./tmp";
const GENERATED_DIR = join(TEMP_DIR, "generated");

// Create temp directory if needed
if (!existsSync(GENERATED_DIR)) {
  mkdirSync(GENERATED_DIR, { recursive: true });
  console.log(`[Server] Created generated directory: ${GENERATED_DIR}`);
}

// middleware
app.use(express.json());

// cors for all requests
app.use((req, res, next) => {
  res.header("Access-Control-Allow-Origin", "*");
  res.header("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  res.header("Access-Control-Allow-Headers", "Content-Type");
  next();
});

// Serve generated heatmaps from temp directory
app.use("/tmp", express.static(GENERATED_DIR));

// Serve default heatmaps from public
app.use("/default-heatmaps", express.static(join(__dirname, "public", "default-heatmaps")));

// api routes (pass environment config to router)
app.use("/api/heatmap", heatmapRouter);

// Store config in app for router access
app.set("generatedDir", GENERATED_DIR);
app.set("nodeEnv", NODE_ENV);

// start server
app.listen(PORT, () => {
  console.log(`\n[Server] Heatmap Server running on http://localhost:${PORT}`);
  console.log(`[Server] Environment: ${NODE_ENV}`);
  console.log(`[Server] Generated files dir: ${GENERATED_DIR}`);
  console.log(`[Server] Heatmap API available at /api/heatmap/*\n`);
});
