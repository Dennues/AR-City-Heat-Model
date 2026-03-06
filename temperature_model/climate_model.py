# simplified urban heat island temperature model

import numpy as np
from scipy.ndimage import gaussian_filter
from scipy.signal import convolve2d
from PIL import Image
from matplotlib.colors import LinearSegmentedColormap
from config import TEMP_VALUES, GRID_SIZE, MAX_ENTITIES
from export_temperature_csv import export_temperature_csv


def create_heatmap_png(areas, lines, bounds, output_path='heatmap.png',
                       baseline_temp=20.0, weather_data=None):

    lat_range = np.linspace(bounds["lat_min"], bounds["lat_max"], GRID_SIZE)
    lon_range = np.linspace(bounds["lon_min"], bounds["lon_max"], GRID_SIZE)
    # scale so buffer sizes etc. still work if GRID_SIZE changes
    scale = GRID_SIZE / 200.0

    print(f"Creating temperature model (baseline: {baseline_temp:.1f}°C, grid: {GRID_SIZE}x{GRID_SIZE})...")

    temp_grid = _build_temp_grid(areas, lines, lat_range, lon_range, baseline_temp, scale)

    if weather_data and weather_data.get("wind_speed"):
        print(f"Applying wind effect ({weather_data['wind_speed']:.1f} km/h)...")
        temp_grid = _apply_wind(temp_grid, areas, weather_data, lat_range, lon_range, scale)

    temp_min = temp_grid.min()
    temp_max = temp_grid.max()
    print(f"Temperature range: {temp_min:.1f}°C to {temp_max:.1f}°C (Δ{temp_max-temp_min:.1f}°C)")

    colormap = _get_colormap()
    rgba = _create_heatmap_image(temp_grid, temp_min, temp_max, colormap, scale)
    Image.fromarray(np.flipud(rgba), mode='RGBA').save(output_path, optimize=True)
    print(f"Saved: {output_path}")

    csv_path = export_temperature_csv(temp_grid, lat_range, lon_range)

    # return some stats so make_map.py can build the legend
    stats = {
        "baseline": baseline_temp,
        "min": float(temp_min),
        "max": float(temp_max),
        "mean": float(temp_grid.mean()),
        "range": float(temp_max - temp_min),
        "csv_file": csv_path,
        "cool_color": tuple(int(c * 255) for c in colormap(0.0)[:3]),
        "avg_color": tuple(int(c * 255) for c in colormap(0.5)[:3]),
        "hot_color": tuple(int(c * 255) for c in colormap(1.0)[:3]),
    }
    return output_path, stats


def _build_temp_grid(areas, lines, lat_range, lon_range, baseline_temp, scale):
    n = len(lat_range)
    temp_sum = np.zeros((n, n))
    weight_sum = np.zeros((n, n))

    # go through all area features (parks, buildings, water etc.)
    for category, polygons in areas.items():
        effect = TEMP_VALUES.get(category, 0)
        if effect == 0:
            continue
        for coords in polygons:
            if len(coords) < 3:
                continue
            _add_polygon(coords, effect, temp_sum, weight_sum, lat_range, lon_range)

    # same for line features (roads, paths) - buffer makes them a few cells wide
    buffer = int(2 * scale)
    for category, paths in lines.items():
        effect = TEMP_VALUES.get(category, 0)
        if effect == 0:
            continue
        for path in paths:
            if len(path) < 2:
                continue
            _add_path(path, effect, buffer, temp_sum, weight_sum, lat_range, lon_range)

    # start from baseline everywhere, then add the weighted effects
    temp_grid = np.full((n, n), baseline_temp, dtype=np.float64)
    has_data = weight_sum > 0
    temp_grid[has_data] = baseline_temp + (temp_sum[has_data] / weight_sum[has_data])

    # smooth a few times so there are no hard edges between zones
    # tried just doing it once but the result looked weird/blocky
    temp_grid = gaussian_filter(temp_grid, sigma=3.0 * scale)
    temp_grid = gaussian_filter(temp_grid, sigma=2.0 * scale)
    temp_grid = gaussian_filter(temp_grid, sigma=1.0 * scale)

    # urban heat island: clustered buildings make each other hotter
    urban_density = _calc_urban_density(areas, lines, lat_range, lon_range, scale)
    hot_areas = temp_grid > baseline_temp + 0.5
    temp_grid[hot_areas] += urban_density[hot_areas] * 0.5

    # vegetation cooling spreads outward to neighbouring cells
    cool_areas = temp_grid < baseline_temp - 0.3
    if cool_areas.any():
        cooling = np.zeros_like(temp_grid)
        cooling[cool_areas] = (baseline_temp - temp_grid[cool_areas]) * 0.8
        cooling_spread = gaussian_filter(cooling, sigma=5.0 * scale)
        temp_grid -= cooling_spread * 0.3

    return gaussian_filter(temp_grid, sigma=1.5 * scale)


def _apply_wind(temp_grid, areas, weather, lat_range, lon_range, scale):
    wind_speed = weather["wind_speed"]
    wind_dir = weather["wind_direction"]

    # stronger wind = more cooling
    wind_strength = np.clip(wind_speed / 15.0, 0, 2.0)

    exposure = _calc_wind_exposure(areas, lat_range, lon_range, wind_dir, scale)
    max_cooling = 1.5 * wind_strength
    cooling = gaussian_filter(exposure * max_cooling, sigma=2.0 * scale)

    return temp_grid - cooling


def _calc_wind_exposure(areas, lat_range, lon_range, wind_dir, scale):
    # 1.0 = fully exposed, lower = shadow behind a building
    grid_size = len(lat_range)
    exposure = np.ones((grid_size, grid_size))

    # rasterize buildings onto the grid first
    building_grid = np.zeros((grid_size, grid_size))
    for coords in areas.get("buildings", [])[:MAX_ENTITIES]:
        if len(coords) < 3:
            continue
        lats = [c[0] for c in coords]
        lons = [c[1] for c in coords]
        for i in range(max(0, np.searchsorted(lat_range, min(lats))),
                       min(grid_size, np.searchsorted(lat_range, max(lats)))):
            for j in range(max(0, np.searchsorted(lon_range, min(lons))),
                           min(grid_size, np.searchsorted(lon_range, max(lons)))):
                if _point_in_poly(lat_range[i], lon_range[j], coords):
                    building_grid[i, j] = 1.0

    # cast wind shadow behind each building
    shadow_angle = (wind_dir + 180) % 360
    dx = np.sin(np.radians(shadow_angle))
    dy = np.cos(np.radians(shadow_angle))
    shadow_len = int(8 * scale)

    for i in range(grid_size):
        for j in range(grid_size):
            if building_grid[i, j] > 0:
                for dist in range(1, shadow_len + 1):
                    si = int(i + dy * dist)
                    sj = int(j + dx * dist)
                    if 0 <= si < grid_size and 0 <= sj < grid_size:
                        strength = 1.0 - (dist / shadow_len)
                        # 0.6 = how much a building blocks the wind
                        exposure[si, sj] *= (1.0 - 0.6 * strength)

    # open areas like parks and water are more exposed to wind
    for category in ["water", "parks", "forest", "grass"]:
        for coords in areas.get(category, [])[:MAX_ENTITIES]:
            if len(coords) < 3:
                continue
            lats = [c[0] for c in coords]
            lons = [c[1] for c in coords]
            for i in range(max(0, np.searchsorted(lat_range, min(lats))),
                           min(grid_size, np.searchsorted(lat_range, max(lats)))):
                for j in range(max(0, np.searchsorted(lon_range, min(lons))),
                               min(grid_size, np.searchsorted(lon_range, max(lons)))):
                    if _point_in_poly(lat_range[i], lon_range[j], coords):
                        exposure[i, j] = min(1.0, exposure[i, j] * 1.2)

    return gaussian_filter(exposure, sigma=1.5 * scale)


def _calc_urban_density(areas, lines, lat_range, lon_range, scale):
    grid_size = len(lat_range)

    buildings = _mark_buildings(areas.get("buildings", []), lat_range, lon_range, grid_size)

    # count how many buildings are nearby each cell
    kernel_size = int(8 * scale)
    kernel = np.ones((kernel_size, kernel_size))
    neighbor_count = convolve2d(buildings, kernel, mode='same', boundary='fill')
    density = np.clip(neighbor_count / (kernel_size * kernel_size * 0.3), 0, 1.5)

    # roads and parking lots also heat up the area
    radius = int(2 * scale)
    for category in ["parking", "roads"]:
        features = areas.get(category, lines.get(category, []))
        for coords in features[:MAX_ENTITIES]:
            if len(coords) < 2:
                continue
            center_lat = np.mean([c[0] for c in coords])
            center_lon = np.mean([c[1] for c in coords])
            ci = np.argmin(np.abs(lat_range - center_lat))
            cj = np.argmin(np.abs(lon_range - center_lon))
            for di in range(-radius, radius + 1):
                for dj in range(-radius, radius + 1):
                    if 0 <= ci+di < grid_size and 0 <= cj+dj < grid_size:
                        dist = np.sqrt(di*di + dj*dj)
                        if dist < radius + 1:
                            density[ci+di, cj+dj] += (1.0 - dist / (radius + 1)) * 0.2

    return gaussian_filter(density, sigma=3.0 * scale)


def _mark_buildings(polygon_list, lat_range, lon_range, grid_size):
    # put buildings on the grid as 1s (used for density + wind calculations)
    grid = np.zeros((grid_size, grid_size))
    for coords in polygon_list[:MAX_ENTITIES]:
        if len(coords) < 3:
            continue
        lats = [c[0] for c in coords]
        lons = [c[1] for c in coords]
        for i in range(max(0, np.searchsorted(lat_range, min(lats))),
                       min(grid_size, np.searchsorted(lat_range, max(lats)))):
            for j in range(max(0, np.searchsorted(lon_range, min(lons))),
                           min(grid_size, np.searchsorted(lon_range, max(lons)))):
                if _point_in_poly(lat_range[i], lon_range[j], coords):
                    grid[i, j] = 1.0
    return grid


def _add_polygon(coords, effect, temp_sum, weight_sum, lat_range, lon_range):
    grid_size = len(lat_range)
    lats = [c[0] for c in coords]
    lons = [c[1] for c in coords]
    i_min = max(0, np.searchsorted(lat_range, min(lats)) - 1)
    i_max = min(grid_size, np.searchsorted(lat_range, max(lats)) + 1)
    j_min = max(0, np.searchsorted(lon_range, min(lons)) - 1)
    j_max = min(grid_size, np.searchsorted(lon_range, max(lons)) + 1)

    for i in range(i_min, i_max):
        for j in range(j_min, j_max):
            if _point_in_poly(lat_range[i], lon_range[j], coords):
                temp_sum[i, j] += effect
                weight_sum[i, j] += 1.0


def _add_path(path, effect, buffer, temp_sum, weight_sum, lat_range, lon_range):
    grid_size = len(lat_range)
    for k in range(len(path) - 1):
        i1 = np.argmin(np.abs(lat_range - path[k][0]))
        j1 = np.argmin(np.abs(lon_range - path[k][1]))
        i2 = np.argmin(np.abs(lat_range - path[k+1][0]))
        j2 = np.argmin(np.abs(lon_range - path[k+1][1]))

        for i in range(max(0, min(i1,i2)-buffer), min(grid_size, max(i1,i2)+buffer+1)):
            for j in range(max(0, min(j1,j2)-buffer), min(grid_size, max(j1,j2)+buffer+1)):
                temp_sum[i, j] += effect
                weight_sum[i, j] += 0.7  # roads get less weight than solid polygons


def _create_heatmap_image(temp_grid, temp_min, temp_max, colormap, scale):
    temp_range = temp_max - temp_min if temp_max > temp_min else 1
    normalized = (temp_grid - temp_min) / temp_range
    rgba = (colormap(normalized) * 255).astype(np.uint8)

    # make extreme cells more opaque so hot/cold spots stand out
    deviation = np.abs(temp_grid - temp_grid.mean())
    alpha = np.clip(deviation / (temp_range * 0.5), 0.3, 1.0)
    rgba[:, :, 3] = (gaussian_filter(alpha, sigma=1.0 * scale) * 180 + 75).astype(np.uint8)

    return rgba


def _get_colormap():
    colors = [
        '#08519c',  # dark blue (coldest)
        '#9ecae1',  # light blue
        '#fd8d3c',  # orange (hottest)
    ]
    return LinearSegmentedColormap.from_list('temperature', colors, N=256)


def _point_in_poly(lat, lon, polygon):
    # ray casting - count how many edges the ray crosses, odd = inside
    inside = False
    p1_lat, p1_lon = polygon[0]
    for i in range(1, len(polygon) + 1):
        p2_lat, p2_lon = polygon[i % len(polygon)]
        if ((p1_lon > lon) != (p2_lon > lon)) and \
           (lat < (p2_lat - p1_lat) * (lon - p1_lon) / (p2_lon - p1_lon) + p1_lat):
            inside = not inside
        p1_lat, p1_lon = p2_lat, p2_lon
    return inside