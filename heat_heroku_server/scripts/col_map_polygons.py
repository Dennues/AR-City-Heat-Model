"""Script for division of map into coloured polygons based on osm"""
import os
import json
import pickle
import requests
import numpy as np
import argparse
from datetime import datetime
from pathlib import Path
from PIL import Image
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from matplotlib.colors import Normalize, TwoSlopeNorm, LinearSegmentedColormap
from matplotlib.cm import ScalarMappable
import csv
from shapely.geometry import Point, Polygon, MultiPolygon, LineString, mapping
from shapely.ops import unary_union, split as split_geom
from collections import defaultdict
import xml.etree.ElementTree as ET
from tqdm import tqdm
from concurrent.futures import ThreadPoolExecutor, as_completed
from scipy.interpolate import griddata
from pyproj import Transformer

# bbox
# can be overridden via CLI args
LAT_MIN, LAT_MAX = 52.436, 52.465
LON_MIN, LON_MAX = 13.2479, 13.3093
OSM_FILE = "berlin_subset.osm"
OUTPUT_PNG = "berlin_osm_temperature_heatmap.png"

# weather api (replaced by CSV input)
# BRIGHTSKY_URL = "https://api.brightsky.dev/weather"
DATE_TIME = "2025-05-20T14:00:00"
TIMEZONE = "Europe/Berlin"

# temperature sampling grid (not used when CSV provided)
# TEMP_GRID_SIZE = 20  # N x N grid points

# visual settings
COLORMAP_COLORS = [
    "#0b2e5c",  # dark blue (cold)
    "#8ad5ff",  # light blue
    "#ffff00",  # yellow
    "#ff0000",  # red (hot)
]
# default to filled polygons, override via cli flags
DRAW_OUTLINES_ONLY = False
DPI = 150
FIGSIZE = (16, 12)
TEMP_RANGE_PADDING = 0.5   # smaller padding to spread data across full colormap
SCALE_MODE = "tight"   # 'tight' / 'auto' / 'percentile'
CENTER_AT_MEAN = True   # balanced with twoslopenorm
BACKGROUND_ALPHA = 0.12   # background layer transparency
SHOW_SAMPLE_POINTS = False   # overlay sampled temperature points (off for overlays)
SAMPLE_POINT_SIZE = 12
SAMPLE_POINT_FACE = 'white'
SAMPLE_POINT_EDGE = 'black'
SAMPLE_POINT_ALPHA = 0.9
SHOW_INTERP_BACKGROUND = False
BG_GRID_RES = 600
FILL_GAPS_WITH_GRID = True     # create polygons in gaps (enabled by default)
# coarser grid so cells are large enough to stay with area filtering
GAP_GRID_SIZE = 5  # very coarse grid for large rectangles when subdividing
# increase max polygon area threshold to reduce splitting frequency
MAX_POLY_AREA_DEG2 = 100e-4  # higher threshold for even larger polygons
# min area threshold for gap cells to avoid over-gridding small regions
MIN_GAP_AREA_DEG2 = 1e-9  # very low to capture tiny gaps and avoid white pixels
SPLIT_LARGE_POLYGONS = True  # set to False to disable polygon splitting fully
SHOW_SAMPLE_LABELS = False   # annotation with temperature (off for overlays)
LABEL_EVERY = 1
LABEL_FMT = "{:.1f}°"
LABEL_FONT_SIZE = 6
# COMPARE_WITH_OPENMETEO = False
# DATA_PROVIDER = "open-meteo"    # 'open-meteo' /'brightsky'

POLY_VALUE_MODE = "interpolated"    # 'interpolated' / 'nearest-sample'
POLY_GRID_RES = 500 

# feature colors (grayscale base map)
GREYSCALE_COLORS = {
    'building': 0.3,
    'park': 0.7,
    'road': 0.5,
    'water': 0.8,
    'background': 0.95
}


def get_custom_colormap():
    """Dark Blue -> Light Blue -> Yellow -> Orange -> Red colormap for temperature mapping."""
    return LinearSegmentedColormap.from_list("heat_custom", COLORMAP_COLORS, N=256)


def select_colors(baseline_temp):
    """baseline temperature color scale differences"""
    try:
        bt = float(baseline_temp)
    except Exception:
        return COLORMAP_COLORS

    if bt < 10:
        return ['#08519c', '#9ecae1', '#fd8d3c']
    elif bt < 20:
        return ['#3182bd', '#c6dbef', '#fb6a4a']
    else:
        return ['#6baed6', '#fee5d9', '#de2d26']

MAX_WORKERS = 8

# coordinate transformation: WGS84 (EPSG:4326) to ETRS89/UTM Zone 33N (EPSG:25833)
# matches building models and base texture projection
USE_UTM_PROJECTION = True  # set to True to match ETRS89/UTM33 building models
UTM_ZONE = 33  # Berlin in UTM Zone 33N
EPSG_WGS84 = 4326  # Input: WGS84 lat/lon
EPSG_UTM33 = 25833  # Output: ETRS89/UTM Zone 33N

# offset adjustments (in m) for aligning with unity scene
UTM_OFFSET_X = 0  # add to easting
UTM_OFFSET_Y = 0  # add to northing
# scale adjustments if needed
UTM_SCALE_X = 1.0
UTM_SCALE_Y = 1.0

# transforms from (lat, lon) to (easting, northing)
transformer_to_utm = Transformer.from_crs(f"EPSG:{EPSG_WGS84}", f"EPSG:{EPSG_UTM33}", always_xy=True)
transformer_from_utm = Transformer.from_crs(f"EPSG:{EPSG_UTM33}", f"EPSG:{EPSG_WGS84}", always_xy=True)

def latlon_to_utm(lat, lon):
    """convert WGS84 lat/lon to ETRS89/UTM33 easting/northing."""
    if not USE_UTM_PROJECTION:
        return lon, lat
    # transformer expects (lon, lat) and returns (easting, northing)
    easting, northing = transformer_to_utm.transform(lon, lat)
    easting = easting * UTM_SCALE_X + UTM_OFFSET_X
    northing = northing * UTM_SCALE_Y + UTM_OFFSET_Y
    return easting, northing

def utm_to_latlon(easting, northing):
    """convert ETRS89/UTM33 easting/northing back to WGS84 lat/lon."""
    if not USE_UTM_PROJECTION:
        return northing, easting
    # transformer expects (easting, northing) and returns (lon, lat)
    lon, lat = transformer_from_utm.transform(easting, northing)
    return lat, lon

def transform_polygon_to_utm(poly):
    """transform shapely polygon from lat/lon to UTM coordinates."""
    if not USE_UTM_PROJECTION:
        return poly
    
    def transform_coords(coords):
        """transform list of (x, y) = (lon, lat) coords to (easting, northing)."""
        # shapely stores as (x, y) = (lon, lat) but latlon_to_utm expect (lat, lon)
        return [latlon_to_utm(y, x) for x, y in coords]
    
    if isinstance(poly, MultiPolygon):
        transformed_geoms = []
        for geom in poly.geoms:
            ext_coords = transform_coords([(x, y) for x, y in geom.exterior.coords])
            int_coords = [transform_coords([(x, y) for x, y in interior.coords]) 
                          for interior in geom.interiors]
            transformed_geoms.append(Polygon(ext_coords, int_coords))
        return MultiPolygon(transformed_geoms)
    else:
        ext_coords = transform_coords([(x, y) for x, y in poly.exterior.coords])
        int_coords = [transform_coords([(x, y) for x, y in interior.coords]) 
                      for interior in poly.interiors]
        return Polygon(ext_coords, int_coords)


def download_osm_data(bbox, output_file):
    # download OSM data for the bounding box using overpass API
    # bbox: lat_min, lon_min, lat_max, lon_max
    print(f"Downloading OSM data for bbox: {bbox}")
    
    # overpass query for building, parks, roads, and water
    overpass_url = "https://overpass-api.de/api/interpreter"
    query = f"""
    [out:xml][timeout:90];
    (
      way["building"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
      way["leisure"="park"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
      way["landuse"="grass"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
      way["landuse"="forest"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
      way["highway"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
    way["natural"="water"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
    node["amenity"="bench"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
      relation["building"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
      relation["leisure"="park"]({bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]});
    );
    out body;
    >;
    out skel qt;
    """
    
    try:
        response = requests.post(overpass_url, data={"data": query}, timeout=120)
        response.raise_for_status()
        with open(output_file, 'wb') as f:
            f.write(response.content)
        
        print(f"OSM data saved to {output_file}")
        return True
    except Exception as e:
        print(f"Error with OSM data downloading: {e}")
        return False


def clip_features_by_union(features, feature_types, union_geom):
    """clipping using union"""
    for ftype in feature_types:
        if ftype not in features or not features[ftype]:
            continue
        clipped = []
        for poly in features[ftype]:
            try:
                result = poly.difference(union_geom)
                if not result.is_empty:
                    if isinstance(result, MultiPolygon):
                        clipped.extend(list(result.geoms))
                    else:
                        clipped.append(result)
            except Exception:
                clipped.append(poly)
        features[ftype] = clipped
        print(f"  {ftype.capitalize()} clipped: {len(clipped)} polygons remaining")


def parse_osm_file(osm_file):

    # parse OSM XML file and extract polygons for buildings, parks, roads and so on

    print(f"Parsing OSM file: {osm_file}")
    tree = ET.parse(osm_file)
    root = tree.getroot()
    
    # collect all nodes and benches (amenity=bench)
    nodes = {}
    benches = []
    for node in root.findall('node'):
        node_id = node.get('id')
        lat = float(node.get('lat'))
        lon = float(node.get('lon'))
        nodes[node_id] = (lon, lat)

        tags = {tag.get('k'): tag.get('v') for tag in node.findall('tag')}
        if tags.get('amenity') == 'bench':
            benches.append((lon, lat))
    features = {'building': [], 'park': [], 'road': [], 'water': []}
    road_groups = defaultdict(list)
    
    # collect ways and create polygons
    for way in tqdm(root.findall('way'), desc="Processing ways"):
        tags = {tag.get('k'): tag.get('v') for tag in way.findall('tag')}
        
        # get node references
        nd_refs = [nd.get('ref') for nd in way.findall('nd')]
        
        if len(nd_refs) < 3:
            continue   # at least 3 points for a polygon neccessary
        
        # get coordinates
        coords = []
        for ref in nd_refs:
            if ref in nodes:
                coords.append(nodes[ref])
        if len(coords) < 3:
            continue
        
        # detects feature type
        feature_type = None
        if 'building' in tags:
            feature_type = 'building'
        elif tags.get('leisure') == 'park' or tags.get('landuse') in ['grass', 'forest']:
            feature_type = 'park'
        elif 'highway' in tags:
            feature_type = 'road'
            # for roads buffer the line to make a polygon
            if len(coords) >= 2:
                line = LineString(coords)
                # buffer size depends on road type
                road_type = tags.get('highway')
                buffer_size = 0.0001
                if road_type in ['motorway', 'trunk', 'primary']:
                    buffer_size = 0.0002
                elif road_type in ['residential', 'service']:
                    buffer_size = 0.00005
                elif road_type in ['path', 'footway', 'cycleway', 'pedestrian']:
                    buffer_size = 0.00002  # Very thin paths
                poly = line.buffer(buffer_size)
                if poly.is_valid and not poly.is_empty:
                    road_groups[road_type].append(poly)
            continue
        elif tags.get('natural') == 'water':
            feature_type = 'water'
        
        if feature_type and len(coords) >= 3:
            try:
                # close polygon if not already closed
                if coords[0] != coords[-1]:
                    coords.append(coords[0])
                poly = Polygon(coords)
                if poly.is_valid and not poly.is_empty:
                    features[feature_type].append(poly)
            except Exception:
                pass
    
    # keep individual road polygons
    if road_groups:
        individual_roads = []
        for _, polys in road_groups.items():
            individual_roads.extend(polys)
        features['road'] = individual_roads

    # clip buildings and gaps by roads, so that there's no overlap
    if features['road']:
        print("Clipping features by roads...")
        # merge all roads into one geometry
        try:
            road_union = unary_union(features['road'])
            print(f"[Roads] Built road union: empty={road_union.is_empty}")
        except Exception as e:
            print(f"[Roads] Failed to build road union: {e}")
            road_union = None
        
        if road_union is not None and not road_union.is_empty:
            # clip buildings, parks, water, and gaps using helper function
            clip_features_by_union(features, ['building', 'park', 'water', 'gap'], road_union)
            # continue split parks/water by road boundaries to avoid spanning under roads
            split_by_roads(features, road_union, min_area=0.0, types=['park', 'water'])

    for ftype, polys in features.items():
        print(f"  {ftype}: {len(polys)} polygons")
    
    return features, nodes, benches


# unused: brightsky api (replaced by csv)
# def fetch_temperature(lat, lon, dt_iso, tz=None, timeout=10):
#     params = {"lat": f"{lat:.6f}", "lon": f"{lon:.6f}", "date": dt_iso}
#     if tz:
#         params["tz"] = tz
#     try:
#         r = requests.get(BRIGHTSKY_URL, params=params, timeout=timeout)
#         r.raise_for_status()
#         data = r.json()
#         weather = data.get("weather") or []
#         if not weather and "temperature" in data:
#             return float(data["temperature"])
#         for w in weather:
#             if w.get("temperature") is not None:
#                 return float(w["temperature"])
#         return None
#     except Exception:
#         return None

def get_openmeteo_temperature(lat, lon, dt_iso, tz=None, timeout=10):
    # openmeteo data
    try:
        # parse requested local datetime and date
        dt_local = datetime.fromisoformat(dt_iso)
        date_str = dt_local.date().isoformat()  # YYYY-MM-DD
        hour_str = dt_local.strftime("%Y-%m-%dT%H")  # for matching on the hour

        # determine endpoint based on whether the date is past or future
        today = datetime.utcnow().date()
        is_past_or_today = dt_local.date() <= today

        if is_past_or_today:
            url = "https://archive-api.open-meteo.com/v1/archive"
            params = {
                "latitude": f"{lat:.6f}",
                "longitude": f"{lon:.6f}",
                "hourly": "temperature_2m",
                "start_date": date_str,
                "end_date": date_str,
                "timezone": (tz or "Europe/Berlin"),
            }
        else:
            url = "https://api.open-meteo.com/v1/forecast"
            params = {
                "latitude": f"{lat:.6f}",
                "longitude": f"{lon:.6f}",
                "hourly": "temperature_2m",
                "timezone": (tz or "Europe/Berlin"),
                "start_date": date_str,
                "end_date": date_str,
            }

        r = requests.get(url, params=params, timeout=timeout)
        r.raise_for_status()
        data = r.json()
        hourly = data.get("hourly") or {}
        times = hourly.get("time") or []
        temps = hourly.get("temperature_2m") or []
        if not times or not temps:
            return None
        idx = None
        for i, t in enumerate(times):
            if t.startswith(hour_str):
                idx = i
                break
        if idx is None:
            # fallback
            idx = 0
        val = temps[idx]
        return float(val) if val is not None else None
    except Exception:
        return None


# unused: brightsky grid fetching (replaced by csv)
# def fetch_temperature_grid(lat_min, lat_max, lon_min, lon_max, grid_size=10, max_workers=8, dt_iso=None):
#     print(f"Fetching temperature data for {grid_size}x{grid_size} grid...")
#     lats = np.linspace(lat_min, lat_max, grid_size)
#     lons = np.linspace(lon_min, lon_max, grid_size)
#     points = [(lat, lon) for lat in lats for lon in lons]
#     temperatures = {}
#     with ThreadPoolExecutor(max_workers=max_workers) as executor:
#         futures = {
#             executor.submit(fetch_temperature, lat, lon, (dt_iso or DATE_TIME), TIMEZONE): (lat, lon)
#             for lat, lon in points
#         }
#         for future in tqdm(as_completed(futures), total=len(futures), desc="Fetching temps"):
#             lat, lon = futures[future]
#             try:
#                 temp = future.result()
#                 if temp is not None:
#                     temperatures[(lat, lon)] = temp
#             except Exception:
#                 pass
#     print(f"Gotten {len(temperatures)} temperature values")
#     return temperatures

def fetch_temperature_grid_openmeteo(lat_min, lat_max, lon_min, lon_max, grid_size=10, max_workers=8, dt_iso=None):
    print(f"Open-Meteo: Fetching temperature data for {grid_size}x{grid_size} grid ...")
    lats = np.linspace(lat_min, lat_max, grid_size)
    lons = np.linspace(lon_min, lon_max, grid_size)
    points = [(lat, lon) for lat in lats for lon in lons]
    temperatures = {}
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        futures = {
            executor.submit(get_openmeteo_temperature, lat, lon, (dt_iso or DATE_TIME), TIMEZONE): (lat, lon)
            for lat, lon in points
        }
        for future in tqdm(as_completed(futures), total=len(futures), desc="Open-Meteo: Fetching temps"):
            lat, lon = futures[future]
            try:
                temp = future.result()
                temperatures[(lat, lon)] = temp
            except Exception:
                temperatures[(lat, lon)] = None
    
    # fill in any None values by interpolating from valid neighbors
    none_points = [(lat, lon) for (lat, lon), temp in temperatures.items() if temp is None]
    for lat, lon in none_points:
        # find nearest valid temp
        valid_neighbors = [(k, v) for k, v in temperatures.items() if v is not None]
        if valid_neighbors:
            nearest = min(valid_neighbors, key=lambda x: (x[0][0]-lat)**2 + (x[0][1]-lon)**2)
            temperatures[(lat, lon)] = nearest[1]
    
    print(f"Open-Meteo: Retrieved {len(temperatures)} temperature values ({len(none_points)} interpolated)")
    return temperatures


def load_temperature_csv(csv_path, bbox=None):
    """load temperature values from a CSV file with columns: latitude,longitude,temperature."""
    if not os.path.exists(csv_path):
        print(f"Temperature CSV not found: {csv_path}")
        return {}

    lat_min = lon_min = lat_max = lon_max = None
    if bbox:
        lat_min, lon_min, lat_max, lon_max = bbox

    temps = {}
    with open(csv_path, newline='', encoding='utf-8') as f:
        reader = csv.DictReader(f)
        for row in reader:
            try:
                lat = float(row['latitude'])
                lon = float(row['longitude'])
                temp = float(row['temperature'])
            except (KeyError, ValueError):
                continue
            if bbox:
                if not (lat_min <= lat <= lat_max and lon_min <= lon <= lon_max):
                    continue
            temps[(lat, lon)] = temp

    if temps:
        vals = np.array(list(temps.values()), dtype=float)
        print(f"CSV temperatures loaded: {len(temps)} points | min={vals.min():.2f}°C mean={vals.mean():.2f}°C max={vals.max():.2f}°C")
    else:
        print("Warning: CSV loaded but no temperature points inside bbox")
    return temps


def interpolate_temperature(polygon, temp_data):
    """sample temperature for polygon using nearby grid points (with averaging neighbours)."""
    centroid = polygon.centroid
    cx, cy = centroid.x, centroid.y
    if not temp_data:
        return None
    
    # find 5 nearest neighbors and average them
    distances = []
    for (lat, lon), temp in temp_data.items():
        if temp is None or np.isnan(temp):
            continue
        d = ((cx - lon)**2 + (cy - lat)**2) ** 0.5  # euclid dist
        distances.append((d, temp))
    
    if not distances:
        valid_temps = [t for t in temp_data.values() if t is not None and not np.isnan(t)]
        if valid_temps:
            return np.mean(valid_temps)
        return None
    
    # sort by distance and take 5 nearest
    distances.sort()
    k = min(5, len(distances))
    nearest_temps = [temp for (d, temp) in distances[:k]]
    return np.mean(nearest_temps)


def build_interpolated_grid(temp_data, bbox, res):
    if not temp_data:
        return None
    lat_min, lon_min, lat_max, lon_max = bbox
    
    if USE_UTM_PROJECTION:
        # build grid in UTM
        utm_pts = np.array([latlon_to_utm(lat, lon) for (lat, lon) in temp_data.keys()])
        utm_x = utm_pts[:, 0]
        utm_y = utm_pts[:, 1]
        vals = np.array(list(temp_data.values()), dtype=float)
        
        # UTM bounds
        utm_min_x, utm_min_y = latlon_to_utm(lat_min, lon_min)
        utm_max_x, utm_max_y = latlon_to_utm(lat_max, lon_max)
        
        gx = np.linspace(utm_min_x, utm_max_x, res)
        gy = np.linspace(utm_min_y, utm_max_y, res)
        GX, GY = np.meshgrid(gx, gy)
        
        try:
            GZ = griddata((utm_x, utm_y), vals, (GX, GY), method='linear')
        except Exception:
            GZ = None
        if GZ is None or np.isnan(GZ).all():
            GZ = griddata((utm_x, utm_y), vals, (GX, GY), method='nearest')
        else:
            GZ_near = griddata((utm_x, utm_y), vals, (GX, GY), method='nearest')
            GZ = np.where(np.isnan(GZ), GZ_near, GZ)
    else:
        # build grid in lat/lon space
        lon_pts = np.array([lon for (lat, lon) in temp_data.keys()], dtype=float)
        lat_pts = np.array([lat for (lat, lon) in temp_data.keys()], dtype=float)
        vals = np.array(list(temp_data.values()), dtype=float)
        gx = np.linspace(lon_min, lon_max, res)
        gy = np.linspace(lat_min, lat_max, res)
        GX, GY = np.meshgrid(gx, gy)
        try:
            GZ = griddata((lon_pts, lat_pts), vals, (GX, GY), method='linear')
        except Exception:
            GZ = None
        if GZ is None or np.isnan(GZ).all():
            GZ = griddata((lon_pts, lat_pts), vals, (GX, GY), method='nearest')
        else:
            GZ_near = griddata((lon_pts, lat_pts), vals, (GX, GY), method='nearest')
            GZ = np.where(np.isnan(GZ), GZ_near, GZ)
    
    return {"gx": gx, "gy": gy, "gz": GZ}

def sample_from_grid(interp_grid, x, y):
    # sample the interpolated grid at the closest grid point to x, y
    # x, y can be either (lon, lat) or (easting, northing) depending on USE_UTM_PROJECTION
    if not interp_grid:
        return None
    gx = interp_grid["gx"]; gy = interp_grid["gy"]; gz = interp_grid["gz"]
    # nearest indices
    ix = int(np.clip(np.round((x - gx[0]) / (gx[-1] - gx[0]) * (len(gx) - 1)), 0, len(gx) - 1))
    iy = int(np.clip(np.round((y - gy[0]) / (gy[-1] - gy[0]) * (len(gy) - 1)), 0, len(gy) - 1))
    return float(gz[iy, ix])


def sample_polygon_value(poly, temp_data, bg_grid=None):
    # pick a main temperature for a polygon
    if bg_grid is not None:
        c = poly.centroid
        return sample_from_grid(bg_grid, c.x, c.y)
    return interpolate_temperature(poly, temp_data)


def export_geojson(features, temp_data, bbox, out_path, bg_grid=None):
    # export polygons with per-feature temperature
    fc = {"type": "FeatureCollection", "features": []}
    def add_feature(poly, ftype):
        if poly.is_empty or not poly.is_valid:
            return
        temp = sample_polygon_value(poly, temp_data, bg_grid)
        fc["features"].append({
            "type": "Feature",
            "geometry": mapping(poly),
            "properties": {
                "feature": ftype,
                "temperature": temp
            }
        })

    for ftype, polys in features.items():
        for poly in polys:
            try:
                if isinstance(poly, MultiPolygon):
                    for p in poly.geoms:
                        add_feature(p, ftype)
                else:
                    add_feature(poly, ftype)
            except Exception:
                continue

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(fc, f)
    print(f"GeoJSON saved to {out_path}")

def split_large_polygons(features, max_area_deg2):
    # split polygons larger than threshold into smaller parts using cut line across long axis
    for ftype in ['building', 'park', 'water', 'gap']:
        if ftype not in features:
            continue
        new_polys = []
        for poly in features[ftype]:
            work = [poly]
            result = []
            guard = 0
            while work and guard < 6:
                cur = work.pop()
                guard += 1
                try:
                    if cur.area <= max_area_deg2:
                        result.append(cur)
                        continue
                    minx, miny, maxx, maxy = cur.bounds
                    width = maxx - minx
                    height = maxy - miny
                    cx, cy = (minx + maxx)/2, (miny + maxy)/2
                    # split on longer axis
                    if width >= height:
                        cut = mpatches.PathPatch(None)
                        line = LineString([(cx, miny-1.0), (cx, maxy+1.0)])
                    else:
                        line = LineString([(minx-1.0, cy), (maxx+1.0, cy)])
                    pieces = split_geom(cur, line)
                    if len(pieces.geoms) <= 1:
                        result.append(cur)
                    else:
                        for p in pieces.geoms:
                            work.append(p)
                except Exception:
                    result.append(cur)
            new_polys.extend(result)
        features[ftype] = new_polys


def split_by_roads(features, road_union, min_area=0.0, types=None):
    # further subdivide polygons using road boundaries
    if road_union is None or road_union.is_empty:
        return
    target_types = types if types else ['park', 'water', 'gap']
    boundary = road_union.boundary
    for ftype in target_types:
        if ftype not in features or not features[ftype]:
            continue
        new_polys = []
        for poly in features[ftype]:
            try:
                parts = split_geom(poly, boundary)
                if parts and hasattr(parts, 'geoms'):
                    for p in parts.geoms:
                        if p.is_valid and not p.is_empty and (min_area <= 0.0 or p.area >= min_area):
                            new_polys.append(p)
                else:
                    if poly.is_valid and not poly.is_empty and (min_area <= 0.0 or poly.area >= min_area):
                        new_polys.append(poly)
            except Exception:
                if poly.is_valid and not poly.is_empty and (min_area <= 0.0 or poly.area >= min_area):
                    new_polys.append(poly)
        features[ftype] = new_polys
        print(f"  {ftype.capitalize()} split by roads: {len(new_polys)} polygons")

def create_gap_polygons(features, bbox, grid_n=60):
    lat_min, lon_min, lat_max, lon_max = bbox
    bbox_poly = Polygon([(lon_min, lat_min), (lon_max, lat_min), (lon_max, lat_max), (lon_min, lat_max)])
    all_polys = []
    for ftype, polys in features.items():
        if polys and ftype != 'gap':
            all_polys.extend(polys)
    if not all_polys:
        return
    try:
        union = unary_union(all_polys)
        remaining = bbox_poly.difference(union)
        if remaining.is_empty:
            return
        if 'gap' not in features:
            features['gap'] = []
        
        road_union = None
        if 'road' in features and features['road']:
            try:
                road_union = unary_union(features['road'])
            except Exception:
                pass
        
        if road_union is not None and not road_union.is_empty:
            try:
                remaining = remaining.difference(road_union)
                if remaining.is_empty:
                    return
            except Exception:
                pass
        
        remaining_polys = []
        if isinstance(remaining, MultiPolygon):
            remaining_polys = list(remaining.geoms)
        elif isinstance(remaining, Polygon):
            remaining_polys = [remaining]
        
        created = [0]
        kept_as_is = [0]
        
        for poly in remaining_polys:
            if not poly.is_valid or poly.is_empty:
                continue
            
            if poly.area <= MAX_POLY_AREA_DEG2:
                if poly.area >= MIN_GAP_AREA_DEG2:
                    features['gap'].append(poly)
                    kept_as_is[0] += 1
                continue
            
            minx, miny, maxx, maxy = poly.bounds
            xs = np.linspace(minx, maxx, grid_n+1)
            ys = np.linspace(miny, maxy, grid_n+1)
            
            for i in range(grid_n):
                x0, x1 = xs[i], xs[i+1]
                for j in range(grid_n):
                    y0, y1 = ys[j], ys[j+1]
                    cell = Polygon([(x0, y0), (x1, y0), (x1, y1), (x0, y1)])
                    if not cell.intersects(poly):
                        continue
                    try:
                        piece = cell.intersection(poly)
                        if piece.is_empty:
                            continue
                        def add_piece(p):
                            try:
                                if p.is_valid and not p.is_empty and getattr(p, 'area', 0.0) >= MIN_GAP_AREA_DEG2:
                                    features['gap'].append(p)
                                    created[0] += 1
                            except Exception:
                                pass

                        if isinstance(piece, MultiPolygon):
                            for p in piece.geoms:
                                add_piece(p)
                        else:
                            add_piece(piece)
                    except Exception:
                        continue

        print(f"[Gaps] Created {created[0]} grid cells from large areas, kept {kept_as_is[0]} smaller areas as is (threshold={MAX_POLY_AREA_DEG2})")
    except Exception:
        return


def create_heatmap(features, temp_data, bbox, output_file, vmin_override=None, vmax_override=None, center_override=None, title_suffix=None, outline_only=False, bg_grid=None, minimal=False, smooth_mode=False):
    print("Creating heatmap visualisation ...")
    
    lat_min, lon_min, lat_max, lon_max = bbox
    
    # transform bbox to UTM if it is
    if USE_UTM_PROJECTION:
        utm_min_x, utm_min_y = latlon_to_utm(lat_min, lon_min)
        utm_max_x, utm_max_y = latlon_to_utm(lat_max, lon_max)
        print(f"Transformed bbox: WGS84 ({lat_min:.5f},{lon_min:.5f}) to ({lat_max:.5f},{lon_max:.5f})")
        print(f"                  UTM33 ({utm_min_x:.2f},{utm_min_y:.2f}) to ({utm_max_x:.2f},{utm_max_y:.2f})")
        display_bbox = (utm_min_x, utm_min_y, utm_max_x, utm_max_y)
    else:
        display_bbox = (lon_min, lat_min, lon_max, lat_max)
    
    # calculate temperature range for normalisation
    if temp_data:
        temp_values = np.array(list(temp_data.values()), dtype=float)
        data_min, data_max = float(np.min(temp_values)), float(np.max(temp_values))
        data_mean = float(np.mean(temp_values))
    else:
        data_min = data_max = data_mean = None

    compute_colours = (not outline_only) or smooth_mode

    if compute_colours and temp_data and (vmin_override is None or vmax_override is None):
        if SCALE_MODE == "tight":
            pad = TEMP_RANGE_PADDING
            vmin, vmax = data_min - pad, data_max + pad
            scale_info = f"tight+pad({pad}°C)"
        elif SCALE_MODE == "percentile":
            p_low, p_high = np.percentile(temp_values, [5, 95])
            vmin, vmax = float(p_low), float(p_high)
            scale_info = "p5–p95"
        else:
            span = data_max - data_min
            pad = max(0.1, span * 0.25)
            vmin = data_min - pad
            vmax = data_max + pad
            scale_info = f"auto ±{pad:.2f}°C"

        print(f"Actual temperature range: {data_min:.2f}°C to {data_max:.2f}°C (span {data_max-data_min:.2f}°C)")
        print(f"Display range ({scale_info}): {vmin:.2f}°C to {vmax:.2f}°C")
    elif compute_colours and temp_data and (vmin_override is not None and vmax_override is not None):
        vmin, vmax = float(vmin_override), float(vmax_override)
        print(f"Fixed display range: {vmin:.2f}°C to {vmax:.2f}°C")
    else:
        vmin, vmax = 0, 25
        if compute_colours:
            print("Warning: No temperature data available, so default range")
    
    # setup figure with UTM or lat/lon coordinates
    fig, ax = plt.subplots(figsize=FIGSIZE, dpi=DPI)
    
    if USE_UTM_PROJECTION:
        # UTM coordinates: equal aspect ratio (meters)
        ax.set_xlim(display_bbox[0], display_bbox[2])
        ax.set_ylim(display_bbox[1], display_bbox[3])
        ax.set_aspect('equal', adjustable='box')
    else:
        # Lat/lon coordinates: adjust aspect for latitude
        lat_center = (lat_min + lat_max) / 2
        aspect_ratio = 1.0 / np.cos(np.radians(lat_center))
        ax.set_xlim(display_bbox[0], display_bbox[2])
        ax.set_ylim(display_bbox[1], display_bbox[3])
        ax.set_aspect(aspect_ratio, adjustable='box')
    
    if minimal:
        ax.axis('off')  # hide all axes, labels, ...
    else:
        if USE_UTM_PROJECTION:
            ax.set_xlabel('Easting (m, ETRS89/UTM33)', fontsize=12)
            ax.set_ylabel('Northing (m, ETRS89/UTM33)', fontsize=12)
        else:
            ax.set_xlabel('Longitude', fontsize=12)
            ax.set_ylabel('Latitude', fontsize=12)
        title_base = f'Berlin Temperature Heatmap - {DATE_TIME[:10]}'
        if title_suffix:
            title_base += f' ({title_suffix})'
        ax.set_title(title_base, fontsize=14, fontweight='bold')
    
    ax.set_facecolor((GREYSCALE_COLORS['background'], GREYSCALE_COLORS['background'], GREYSCALE_COLORS['background']))
    
    # colourmap normalization
    center_at_mean = CENTER_AT_MEAN if center_override is None else bool(center_override)
    if compute_colours and temp_data:
        span = vmax - vmin
        if span <= 1e-9:
            vmin = vmin - 0.5
            vmax = vmax + 0.5
            if center_at_mean:
                center_at_mean = False
            print(f"Flat temperature field detected, expanded display range to {vmin:.2f}°C ... {vmax:.2f}°C")
    if outline_only and not smooth_mode:
        norm = None
        cmap = None
    else:
        if temp_data and center_at_mean and (vmax - vmin) > 1e-9 and vmin < data_mean < vmax:
            norm = TwoSlopeNorm(vmin=vmin, vcenter=data_mean, vmax=vmax)
        else:
            norm = Normalize(vmin=vmin, vmax=vmax)
        cmap = get_custom_colormap()
        try:
            cmap.set_bad((0.8, 0.8, 0.8, 1.0))
        except Exception:
            pass
    
    # optional: interpolated background raster for full coverage
    local_bg_grid = bg_grid
    if temp_data and local_bg_grid is None:
        local_bg_grid = build_interpolated_grid(temp_data, bbox, POLY_GRID_RES)
    

    render_bg = temp_data and (SHOW_INTERP_BACKGROUND or smooth_mode)
    if render_bg:
        # building background grid at BG_GRID_RES (for preview)
        if SHOW_INTERP_BACKGROUND or smooth_mode:
            # Transform temperature sample points to UTM if needed
            if USE_UTM_PROJECTION:
                # convert temp_data keys from (lat,lon) to (easting,northing)
                utm_pts = np.array([latlon_to_utm(lat, lon) for (lat, lon) in temp_data.keys()])
                utm_x = utm_pts[:, 0]
                utm_y = utm_pts[:, 1]
                vals = np.array(list(temp_data.values()), dtype=float)
                
                # create grid in UTM space
                gx = np.linspace(display_bbox[0], display_bbox[2], BG_GRID_RES)
                gy = np.linspace(display_bbox[1], display_bbox[3], BG_GRID_RES)
                GX, GY = np.meshgrid(gx, gy)
                GZ = griddata((utm_x, utm_y), vals, (GX, GY), method='cubic')
                if np.isnan(GZ).any():
                    GZ_near = griddata((utm_x, utm_y), vals, (GX, GY), method='nearest')
                    GZ = np.where(np.isnan(GZ), GZ_near, GZ)
                # use UTM bbox for extent
                ax.imshow(GZ, extent=(display_bbox[0], display_bbox[2], display_bbox[1], display_bbox[3]), 
                         origin='lower', cmap=cmap, norm=norm, zorder=0, interpolation='bilinear')
            else:
                lon_pts = np.array([lon for (lat, lon) in temp_data.keys()], dtype=float)
                lat_pts = np.array([lat for (lat, lon) in temp_data.keys()], dtype=float)
                vals = np.array(list(temp_data.values()), dtype=float)
                gx = np.linspace(lon_min, lon_max, BG_GRID_RES)
                gy = np.linspace(lat_min, lat_max, BG_GRID_RES)
                GX, GY = np.meshgrid(gx, gy)
                GZ = griddata((lon_pts, lat_pts), vals, (GX, GY), method='cubic')
                if np.isnan(GZ).any():
                    GZ_near = griddata((lon_pts, lat_pts), vals, (GX, GY), method='nearest')
                    GZ = np.where(np.isnan(GZ), GZ_near, GZ)
                ax.imshow(GZ, extent=(lon_min, lon_max, lat_min, lat_max), origin='lower',
                         cmap=cmap, norm=norm, zorder=0, interpolation='bilinear')
    
    
    # draw features in order (bg to fg); roads drawn before gaps so roads subdivide visible gap areas
    draw_order = ['water', 'park', 'road', 'gap', 'building']
    
    for feature_type in draw_order:
        if feature_type not in features:
            continue
        
        polys = features[feature_type]
        print(f"Drawing {len(polys)} {feature_type} polygons...")
        
        for poly in tqdm(polys, desc=f"Drawing {feature_type}"):
            # Transform polygon to UTM if enabled
            poly_display = transform_polygon_to_utm(poly)
            
            # get temperature for polygon
            if outline_only:
                # outlines only: same black outlines as polygon mode
                color = 'none'
                edge_col = 'black'
                alpha = 1.0
            else:
                if POLY_VALUE_MODE == "interpolated" and local_bg_grid is not None:
                    # use transformed centroid for grid sampling (UTM if enabled)
                    c = poly_display.centroid
                    temp = sample_from_grid(local_bg_grid, c.x, c.y)
                else:
                    temp = interpolate_temperature(poly, temp_data)

                if temp is not None and not np.isnan(temp):
                    tval = max(vmin, min(vmax, float(temp)))
                    color = cmap(norm(tval))
                    edge_col = 'black'
                    alpha = 1.0
                else:
                    # use global average if no temperature found
                    if temp_data:
                        valid_temps = [t for t in temp_data.values() if t is not None and not np.isnan(t)]
                        if valid_temps:
                            avg_temp = np.mean(valid_temps)
                            tval = max(vmin, min(vmax, float(avg_temp)))
                            color = cmap(norm(tval))
                            edge_col = 'black'
                            alpha = 1.0
                        else:
                            tval = (vmin + vmax) / 2.0
                            color = cmap(norm(tval))
                            edge_col = 'black'
                            alpha = 0.7
                    else:
                        tval = (vmin + vmax) / 2.0
                        color = cmap(norm(tval))
                        edge_col = 'black'
                        alpha = 1.0
                        # draw polygon (use transformed coordinates)
            try:
                def draw_poly(p):
                    # exterior
                    line_width = 0.3 if outline_only else 0.2
                    patch = mpatches.Polygon(list(p.exterior.coords),
                                             facecolor=color,
                                             edgecolor=edge_col,
                                             alpha=alpha,
                                             linewidth=line_width)
                    ax.add_patch(patch)
                    # fill interior
                    if not outline_only:
                        for interior in p.interiors:
                            patch_i = mpatches.Polygon(list(interior.coords),
                                                        facecolor=color,
                                                        edgecolor=edge_col,
                                                        alpha=alpha,
                                                        linewidth=0.2)
                            ax.add_patch(patch_i)

                if isinstance(poly_display, MultiPolygon):
                    for p in poly_display.geoms:
                        draw_poly(p)
                else:
                    draw_poly(poly_display)
            except Exception:
                pass
    
    # optional overlay sample points used for temperature (disabled for minimal)
    if compute_colours and not minimal and SHOW_SAMPLE_POINTS and temp_data:
        pts = np.array([(lon, lat) for (lat, lon) in temp_data.keys()])
        if len(pts) > 0:
            ax.scatter(pts[:,0], pts[:,1], s=SAMPLE_POINT_SIZE,
                       c=SAMPLE_POINT_FACE, edgecolors=SAMPLE_POINT_EDGE,
                       alpha=SAMPLE_POINT_ALPHA, zorder=5, label='sample')
            if SHOW_SAMPLE_LABELS:
                vals = [temp_data[(lat, lon)] for (lat, lon) in temp_data.keys()]
                for i, ((lon, lat), val) in enumerate(zip([(lon, lat) for (lat, lon) in temp_data.keys()], vals)):
                    if i % max(1, LABEL_EVERY) != 0:
                        continue
                    txt = LABEL_FMT.format(val)
                    ax.text(lon, lat, txt,
                            fontsize=LABEL_FONT_SIZE,
                            ha='center', va='center', zorder=6,
                            color='k',
                            bbox=dict(boxstyle='round,pad=0.15', fc='white', ec='none', alpha=0.6))

    # add colourbar
    if compute_colours and not minimal:
        sm = ScalarMappable(norm=norm, cmap=cmap)
        sm.set_array([])
        cbar = plt.colorbar(sm, ax=ax, orientation='vertical', pad=0.02, fraction=0.046)
        cbar.set_label('Temperature (°C)', fontsize=12)
    
    # save
    if minimal:
        fig.subplots_adjust(left=0, right=1, top=1, bottom=0)
        ax.set_position([0, 0, 1, 1])
        plt.savefig(output_file, dpi=DPI, bbox_inches='tight', pad_inches=0)
    else:
        plt.tight_layout()
        plt.savefig(output_file, dpi=DPI, bbox_inches='tight')
    plt.close(fig)
    print(f"Heatmap saved to {output_file}")
    return output_file


def crop_and_upscale_image(input_path, crop_coords, upscale_factor=1.0):
    """crop and upscale PNG using PIL."""
    try:
        from PIL import Image
        print(f"Post-processing: crop={crop_coords}, upscale={upscale_factor}x")
        img = Image.open(input_path)
        print(f"  Input image size: {img.size}")
        
        if crop_coords:
            x1, y1, x2, y2 = crop_coords
            img = img.crop((x1, y1, x2, y2))
            print(f"  Cropped to: {img.size}")
        
        if upscale_factor and upscale_factor != 1.0:
            new_w = int(img.width * upscale_factor)
            new_h = int(img.height * upscale_factor)
            img = img.resize((new_w, new_h), Image.Resampling.LANCZOS)
            print(f"  Upscaled to: {img.size}")
        
        img.save(input_path)
        print(f"  Saved processed image to {input_path}")
    except ImportError:
        print("Warnign: PIL not available, skipping crop/upscale")
    except Exception as e:
        print(f"Warning: Crop/upscale failed: {e}")


def export_benches(bench_nodes, bbox, out_path):
    """export bench locations as GeoJSON with normalized UV coordinates."""
    try:
        lat_min, lon_min, lat_max, lon_max = bbox
    except ValueError:
        lat_min, lon_min, lat_max, lon_max = bbox[0], bbox[1], bbox[2], bbox[3]

    # if using UTM projection, convert bbox and bench coordinates to UTM
    if USE_UTM_PROJECTION:
        utm_min_x, utm_min_y = latlon_to_utm(lat_min, lon_min)
        utm_max_x, utm_max_y = latlon_to_utm(lat_max, lon_max)
        
        denom_x = max(1e-9, utm_max_x - utm_min_x)
        denom_y = max(1e-9, utm_max_y - utm_min_y)
        
        features = []
        for lon, lat in bench_nodes:
            utm_x, utm_y = latlon_to_utm(lat, lon)
            u = (utm_x - utm_min_x) / denom_x
            v = (utm_y - utm_min_y) / denom_y
            features.append({
                "type": "Feature",
                "geometry": {"type": "Point", "coordinates": [lon, lat]},
                "properties": {
                    "u": u,
                    "v": v
                }
            })
    else:
        # use lat/lon normalization
        denom_lon = max(1e-9, lon_max - lon_min)
        denom_lat = max(1e-9, lat_max - lat_min)

        features = []
        for lon, lat in bench_nodes:
            u = (lon - lon_min) / denom_lon
            v = (lat - lat_min) / denom_lat
            features.append({
                "type": "Feature",
                "geometry": {"type": "Point", "coordinates": [lon, lat]},
                "properties": {
                    "u": u,
                    "v": v
                }
            })

    geojson = {"type": "FeatureCollection", "features": features}
    with open(out_path, 'w', encoding='utf-8') as f:
        json.dump(geojson, f, ensure_ascii=False, indent=2)
    print(f"Benches exported to {out_path} ({len(features)} points)")


def main(args):
    global SPLIT_LARGE_POLYGONS, DRAW_OUTLINES_ONLY, FILL_GAPS_WITH_GRID, MAX_POLY_AREA_DEG2, SHOW_INTERP_BACKGROUND
    smooth_mode = False
    if args.no_split:
        SPLIT_LARGE_POLYGONS = False
        print("Polygon splitting disabled: using original polygons.")
    if args.outline_only:
        DRAW_OUTLINES_ONLY = True
        print("Outline-only mode enabled.")
    if args.with_coloring:
        DRAW_OUTLINES_ONLY = False
        MAX_POLY_AREA_DEG2 = 1e-5  # smaller -> more spliting
        print("Coloring enabled: polygons will be filled with temperature colors (with finer splitting).")
    if args.smooth_coloring:
        smooth_mode = True
        DRAW_OUTLINES_ONLY = True   # outlines only over smooth background
        SHOW_INTERP_BACKGROUND = True
        print("Smooth coloring enabled: interpolated background with polygon outlines only.")
    if args.fill_gaps:
        FILL_GAPS_WITH_GRID = True
        print("Gap filling enabled: background will be filled with grid polygons.")
    
    # derive bbox either from explicit bounds or center/span overrides
    if args.lat_min is not None and args.lat_max is not None and args.lon_min is not None and args.lon_max is not None:
        lat_min, lat_max = args.lat_min, args.lat_max
        lon_min, lon_max = args.lon_min, args.lon_max
    else:
        # use default bounds if not specified
        lat_min, lat_max = LAT_MIN, LAT_MAX
        lon_min, lon_max = LON_MIN, LON_MAX

    bbox = (lat_min, lon_min, lat_max, lon_max)

    # if png_out is provided, use as is without appending provider/label suffix
    explicit_png_out = bool(args.png_out)
    png_base = args.png_out if args.png_out else OUTPUT_PNG
    osm_file = args.osm_file if args.osm_file else f"berlin_{lat_min:.4f}_{lon_min:.4f}_{lat_max:.4f}_{lon_max:.4f}.osm"

    features = None
    benches = None

    # try to load cached features to skip OSM parsing
    if args.features_cache_in and os.path.exists(args.features_cache_in):
        try:
            with open(args.features_cache_in, "rb") as f:
                cached = pickle.load(f)
            features = cached.get("features")
            benches = cached.get("benches")
            print(f"Loaded features from cache: {args.features_cache_in}")
        except Exception as e:
            print(f"Cache load failed, falling back to OSM parse: {e}")

    if features is None or benches is None:
        # download or check for osm data
        if not os.path.exists(osm_file):
            print("OSM file not found for this bbox. Downloading...")
            if not download_osm_data(bbox, osm_file):
                print("Failed to download OSM data. Exiting.")
                return
        else:
            print(f"Using existing OSM file: {osm_file}")
        
        # parse osm
        features, nodes, benches = parse_osm_file(osm_file)
        # create grid-polygons in gaps First (subdivided by roads)
        if FILL_GAPS_WITH_GRID:
            create_gap_polygons(features, bbox, grid_n=GAP_GRID_SIZE)
        # then split all large polygons by threshold (including gap polygons)
        if SPLIT_LARGE_POLYGONS:
            split_large_polygons(features, MAX_POLY_AREA_DEG2)

        # optional: save cache
        if args.features_cache_out:
            try:
                with open(args.features_cache_out, "wb") as f:
                    pickle.dump({"features": features, "benches": benches}, f)
                print(f"Saved features cache to {args.features_cache_out}")
            except Exception as e:
                print(f"Failed to write features cache: {e}")
    
    # check if we got any features
    total_features = sum(len(polys) for polys in features.values())
    if total_features == 0:
        print("Warning: No features extracted from OSM data!")
        print("You may need to adjust bounding box or download new data.")
        return
    # get temperature data from CSV (single time slice)
    temp_sets = []
    csv_path = args.temp_csv if args.temp_csv else os.path.join(os.path.dirname(__file__), "temperature_data.csv")
    td = load_temperature_csv(csv_path, bbox=bbox)
    if not td:
        print("No temperature data loaded from CSV, aborting.")
        return
    vals = np.array(list(td.values()), dtype=float)
    print(f"[CSV] min={vals.min():.1f}°C, mean={vals.mean():.1f}°C, max={vals.max():.1f}°C")
    # choose colormap based on mean of csv
    baseline_temp = float(np.mean(vals)) if vals.size > 0 else None
    try:
        global COLORMAP_COLORS
        COLORMAP_COLORS = select_colors(baseline_temp)
        print(f"baseline_temp = {baseline_temp:.2f}°C: {COLORMAP_COLORS}")
    except Exception:
        pass

    temp_sets.append(("csv", DATE_TIME, td))
    times = []

    def compare_sets(a, b, name_a, name_b, tol=1e-6):
        keys = set(a.keys()) & set(b.keys())
        if not keys:
            print(f"No common points between {name_a} and {name_b}")
            return None
        diffs = np.array([abs(a[k] - b[k]) for k in keys], dtype=float)
        identical = np.sum(diffs <= tol)
        print(f"Comparison {name_a} vs {name_b} on {len(keys)} points:")
        print(f"- Identical within tol: {identical}/{len(keys)} ({identical/len(keys)*100:.1f}%)")
        print(f"- Diff stats: min={np.min(diffs):.3f}°C, mean={np.mean(diffs):.3f}°C, max={np.max(diffs):.3f}°C")
        return diffs

    if len(temp_sets) >= 1:
        # prov_name = "Open-Meteo" if DATA_PROVIDER == "open-meteo" else "BrightSky"
        if len(temp_sets) == 2:
            compare_sets(temp_sets[0][2], temp_sets[1][2], f"csv {temp_sets[0][0]}", f"csv {temp_sets[1][0]}")

    all_vals = []
    for _, _, td in temp_sets:
        if td:
            all_vals.extend(list(td.values()))
    if all_vals:
        vmin_shared, vmax_shared = float(np.min(all_vals)), float(np.max(all_vals))
        if abs(vmax_shared - vmin_shared) < 1e-9:
            vmin_shared -= 0.5
            vmax_shared += 0.5
    else:
        vmin_shared, vmax_shared = 0.0, 25.0

    # provider_tag = "openmeteo" if DATA_PROVIDER == "open-meteo" else "brightsky"
    outputs = []

    # reuse interpolation grid for polygon sampling/export when possible
    bg_grid_shared = None
    if temp_sets and temp_sets[0][2] and POLY_VALUE_MODE == "interpolated":
        bg_grid_shared = build_interpolated_grid(temp_sets[0][2], bbox, POLY_GRID_RES)

    vmin_cli = getattr(args, "vmin", None)
    vmax_cli = getattr(args, "vmax", None)
    if getattr(args, "scale_mode", None):
        global SCALE_MODE
        SCALE_MODE = args.scale_mode

    crop_coords = None
    if getattr(args, "crop", None):
        try:
            crop_coords = tuple(map(int, args.crop.split(',')))
            if len(crop_coords) != 4:
                print("Warning: --crop expects 4 values (x1,y1,x2,y2), ignoring.")
                crop_coords = None
        except ValueError:
            print("Warning: Invalid --crop format, ignoring.")
            crop_coords = None
    upscale_factor = getattr(args, "upscale", None)

    for label, dt_iso, td in temp_sets:
        if explicit_png_out:
            out_path = png_base
        else:
            out_path = os.path.splitext(png_base)[0] + f"_{label}.png"
        output_file = create_heatmap(features, td, bbox, out_path,
                   vmin_override=vmin_cli,
                   vmax_override=vmax_cli,
                   center_override=None,
                       title_suffix=f"{label}",
                       outline_only=DRAW_OUTLINES_ONLY,
                       bg_grid=bg_grid_shared,
                       minimal=args.minimal,
                       smooth_mode=smooth_mode)
        
        if (crop_coords or upscale_factor) and output_file:
            crop_and_upscale_image(output_file, crop_coords, upscale_factor or 1.0)
        
        outputs.append(out_path)

    if args.geojson_out and temp_sets:
        base_td = temp_sets[0][2]
        if base_td:
            export_geojson(features, base_td, bbox, args.geojson_out, bg_grid=bg_grid_shared)
        else:
            print("Skipping GeoJSON export: no temperature data available.")

    if getattr(args, "benches_out", None):
        if benches:
            export_benches(benches, bbox, args.benches_out)
        else:
            print("No benches found in OSM data, skipping benches export.")


    print("\nDone! Outputs saved:")
    for p in outputs:
        print("  ", os.path.abspath(p))

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Render temperature coloured OSM polygons or outlines only.")
    parser.add_argument("--outline-only", action="store_true", help="Show only polygon outlines (no fill colouring or colour bar).")
    parser.add_argument("--lat-min", type=float, help="Lower latitude bound.")
    parser.add_argument("--lat-max", type=float, help="Upper latitude bound.")
    parser.add_argument("--lon-min", type=float, help="Lower longitude bound.")
    parser.add_argument("--lon-max", type=float, help="Upper longitude bound.")
    parser.add_argument("--center-lat", type=float, help="Center latitude (used if bounds not given).")
    parser.add_argument("--center-lon", type=float, help="Center longitude (used if bounds not given).")
    parser.add_argument("--lat-span", type=float, help="Latitude span in degrees (used with center).")
    parser.add_argument("--lon-span", type=float, help="Longitude span in degrees (used with center).")
    parser.add_argument("--png-out", type=str, help="Base name for PNG output (suffixes are added).")
    parser.add_argument("--geojson-out", type=str, help="Optional GeoJSON output path (polygons + temperature).")
    parser.add_argument("--benches-out", type=str, help="Optional benches GeoJSON output path (points with normalized UVs).")
    parser.add_argument("--osm-file", type=str, help="Optional pre-downloaded OSM file to reuse.")
    parser.add_argument("--temp-csv", type=str, help="Path to temperature CSV (latitude,longitude,temperature).")
    parser.add_argument("--features-cache-in", type=str, help="Path to features cache (pickle) to skip OSM parse.")
    parser.add_argument("--features-cache-out", type=str, help="Path to write features cache (pickle) after parsing.")
    parser.add_argument("--minimal", action="store_true", help="Minimal output: no title, labels, or colorbar (for overlays).")
    parser.add_argument("--no-split", action="store_true", help="Disable polygon splitting (keep original polygons).")
    parser.add_argument("--with-coloring", action="store_true", help="Enable polygon coloring by temperature (disable outline-only mode).")
    parser.add_argument("--smooth-coloring", action="store_true", help="Use smooth background interpolation (ignores polygon fills, shows outlines only).")
    parser.add_argument("--fill-gaps", action="store_true", help="Enable gap filling with grid polygons.")
    parser.add_argument("--scale-mode", type=str, choices=["tight","auto","percentile"], help="Scaling mode for color mapping.")
    parser.add_argument("--vmin", type=float, help="Fixed lower bound for color scale.")
    parser.add_argument("--vmax", type=float, help="Fixed upper bound for color scale.")
    parser.add_argument("--crop", type=str, help="Crop output image: x1,y1,x2,y2 (e.g., 185,187,2100,1632)")
    parser.add_argument("--upscale", type=float, help="Upscale cropped image by factor (e.g., 9.87)")
    args = parser.parse_args()
    main(args)
