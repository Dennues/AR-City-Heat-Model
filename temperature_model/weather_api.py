# get current weather data from the internet

import requests
from datetime import datetime


def get_current_weather(bounds):
    # find center of our area
    lat = (bounds["lat_min"] + bounds["lat_max"]) / 2
    lon = (bounds["lon_min"] + bounds["lon_max"]) / 2

    url = "https://api.open-meteo.com/v1/forecast"
    params = {
        "latitude": lat,
        "longitude": lon,
        "current": "temperature_2m,wind_speed_10m,wind_direction_10m,relative_humidity_2m",
        "timezone": "auto"
    }

    try:
        response = requests.get(url, params=params, timeout=10)
        response.raise_for_status()
        data = response.json()

        curr = data.get("current", {})
        weather = {
            "temperature": curr.get("temperature_2m"),
            "wind_speed": curr.get("wind_speed_10m"),
            "wind_direction": curr.get("wind_direction_10m"),
            "humidity": curr.get("relative_humidity_2m"),
            "time": curr.get("time"),
            "timezone": data.get("timezone"),
            "location": {"lat": lat, "lon": lon}
        }

        wind_dir = wind_direction_to_text(weather["wind_direction"])
        print(f"Weather: {weather['temperature']:.1f}°C, Wind: {weather['wind_speed']:.1f} km/h {wind_dir}\n")
        return weather

    except Exception:
        # if internet fails, just use some default values
        print("Couldn't get weather, using defaults\n")
        return {
            "temperature": 20.0,
            "wind_speed": 10.0,
            "wind_direction": 270.0,
            "humidity": 60,
            "time": datetime.now().isoformat(),
            "timezone": "UTC",
            "location": {"lat": lat, "lon": lon}
        }


def wind_direction_to_text(degrees):
    # convert degrees to N, NE, E, etc.
    dirs = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
            "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"]
    idx = int((degrees + 11.25) / 22.5) % 16
    return dirs[idx]