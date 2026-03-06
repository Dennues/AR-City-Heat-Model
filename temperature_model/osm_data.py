# download map data from OpenStreetMap

import requests
import time
from config import OVERPASS_URL


def download_osm_data(bounds):
    # query for OSM
    query = f"""
    [out:json];
    (
      way["natural"="water"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["building"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      relation["building"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["landuse"="forest"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["natural"="wood"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["landuse"="grass"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["landuse"="meadow"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["leisure"="park"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["leisure"="garden"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      relation["leisure"="park"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["amenity"="parking"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["highway"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["railway"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["landuse"="residential"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["landuse"="allotments"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
      way["landuse"="farmland"]({bounds['lat_min']},{bounds['lon_min']},{bounds['lat_max']},{bounds['lon_max']});
    );
    out body;
    >;
    out skel qt;
    """

    # try a few times in case it fails (which happens sometimes)
    for attempt in range(3):
        try:
            response = requests.get(OVERPASS_URL, params={"data": query}, timeout=60)
            response.raise_for_status()
            return response.json()
        except Exception as e:
            print(f"  Try {attempt + 1}/3 failed: {e}")
            if attempt < 2:
                time.sleep(3)
            else:
                raise


def process_osm_data(data):
    # organize the data into categories
    areas = {
        "water": [], "buildings": [], "forest": [],
        "grass": [], "parks": [], "parking": [],
        "residential": [], "allotments": [], "farmland": []
    }
    lines = {"roads": [], "paths": [], "railway": []}

    # make a lookup dict for all the nodes (points)
    nodes = {}
    for el in data.get("elements", []):
        if el.get("type") == "node" and el.get("lat") and el.get("lon"):
            nodes[el["id"]] = (el["lat"], el["lon"])

    # go through all the ways (lines/polygons)
    for el in data.get("elements", []):
        if el.get("type") != "way":
            continue

        # convert node IDs to actual coordinates
        coords = []
        for node_id in el.get("nodes", []):
            if node_id in nodes:
                coords.append(nodes[node_id])

        if len(coords) < 2:
            continue

        # figure out which category it is
        tags = el.get("tags", {})

        if tags.get("natural") == "water":
            areas["water"].append(coords)
        elif "building" in tags:
            areas["buildings"].append(coords)
        elif tags.get("landuse") == "forest" or tags.get("natural") == "wood":
            areas["forest"].append(coords)
        elif tags.get("landuse") in ["grass", "meadow"]:
            areas["grass"].append(coords)
        elif tags.get("leisure") in ["park", "garden"]:
            areas["parks"].append(coords)
        elif tags.get("amenity") == "parking":
            areas["parking"].append(coords)
        elif tags.get("landuse") == "residential":
            areas["residential"].append(coords)
        elif tags.get("landuse") == "allotments":
            areas["allotments"].append(coords)
        elif tags.get("landuse") == "farmland":
            areas["farmland"].append(coords)
        elif "highway" in tags and tags.get("area") != "yes":
            # roads and paths
            highway_type = tags.get("highway", "")
            if highway_type in ["primary", "secondary", "residential", "service"]:
                lines["roads"].append(coords)
            elif highway_type in ["footway", "path", "track", "cycleway"]:
                lines["paths"].append(coords)
        elif "railway" in tags:
            lines["railway"].append(coords)

    # also handle complex relations (multi-part things like big parks or building complexes)
    ways_dict = {}
    for el in data.get("elements", []):
        if el.get("type") == "way":
            ways_dict[el["id"]] = el

    for el in data.get("elements", []):
        if el.get("type") != "relation":
            continue

        tags = el.get("tags", {})
        category = None

        if "building" in tags:
            category = "buildings"
        elif tags.get("leisure") in ["park", "garden"]:
            category = "parks"
        elif tags.get("landuse") == "forest" or tags.get("natural") == "wood":
            category = "forest"
        elif tags.get("landuse") == "residential":
            category = "residential"
        elif tags.get("landuse") == "allotments":
            category = "allotments"
        elif tags.get("landuse") == "farmland":
            category = "farmland"

        if category:
            for member in el.get("members", []):
                if member.get("type") == "way" and member.get("role") in ["outer", ""]:
                    way = ways_dict.get(member.get("ref"))
                    if way:
                        coords = []
                        for node_id in way.get("nodes", []):
                            if node_id in nodes:
                                coords.append(nodes[node_id])
                        if len(coords) >= 3:
                            areas[category].append(coords)

    return areas, lines