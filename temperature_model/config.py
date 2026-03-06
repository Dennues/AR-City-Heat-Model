# configuration settings

# study area bounds (FU Berlin area)
BOUNDS = {
    "lat_min": 52.43700,
    "lat_max": 52.46400,
    "lon_min": 13.24950,
    "lon_max": 13.30830
}

# temperature effects from research (°C) - see documentation for detailed creation
TEMP_VALUES = {
    "water": -2.5,
    "forest": -3.0,
    "parks": -2.0,
    "grass": -1.0,
    "buildings": +2.5,
    "parking": +4.0,
    "roads": +3.0,
    "paths": +0.3,

    # later added cause present in FU-Area
    "railway": +2.8,
    "residential": -0.5,
    "allotments": -1.8,
    "farmland": -0.8,
}

# colors for map visualization - from early stage, not used in heatmap anymore
COLORS = {
    "water": "blue",
    "buildings": "gray",
    "forest": "darkgreen",
    "grass": "lightgreen",
    "parks": "purple",
    "parking": "red",
    "roads": "orange",
    "paths": "brown",
    "railway": "darkred",
    "residential": "lightgray",
    "allotments": "green",
    "farmland": "yellow",
}

# processing limits
GRID_SIZE = 200
MAX_ENTITIES = 100000

# API
OVERPASS_URL = "http://overpass-api.de/api/interpreter"