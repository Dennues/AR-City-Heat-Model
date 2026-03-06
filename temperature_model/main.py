# main script - runs everything

from config import BOUNDS
from osm_data import download_osm_data, process_osm_data
from make_map import create_map
from weather_api import get_current_weather


def main():
    print("=" * 60)
    print("Urban Heat Island Temperature Model")
    print("=" * 60 + "\n")

    print("Step 1: Getting weather data...")
    weather = get_current_weather(BOUNDS)

    print("Step 2: Downloading from OpenStreetMap...")
    raw_data = download_osm_data(BOUNDS)

    print("Step 3: Processing map features...")
    areas, lines = process_osm_data(raw_data)

    print("Step 4: Creating temperature map...\n")
    m = create_map(
        areas, lines, BOUNDS,
        show_heatmap=True,
        baseline_temp=weather["temperature"],
        weather_data=weather
    )

    m.save("map.html")
    print("\nDone! Open map.html to see the result")

if __name__ == "__main__":
    main()