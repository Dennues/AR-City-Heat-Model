# create the interactive map with Folium

import folium
from climate_model import create_heatmap_png
from weather_api import wind_direction_to_text


def create_map(areas, lines, bounds, show_heatmap=True, baseline_temp=20.0, weather_data=None):
    # center of fu area
    center_lat = (bounds["lat_min"] + bounds["lat_max"]) / 2
    center_lon = (bounds["lon_min"] + bounds["lon_max"]) / 2

    m = folium.Map(
        location=[center_lat, center_lon],
        zoom_start=16,
        tiles='CartoDB positron'
    )

    # draw rectangle around study area
    folium.Rectangle(
        bounds=[[bounds["lat_min"], bounds["lon_min"]],
                [bounds["lat_max"], bounds["lon_max"]]],
        color='black',
        fill=False,
        weight=2
    ).add_to(m)

    if show_heatmap:
        heatmap_file, stats = create_heatmap_png(
            areas, lines, bounds,
            baseline_temp=baseline_temp,
            weather_data=weather_data
        )

        folium.raster_layers.ImageOverlay(
            image=heatmap_file,
            bounds=[[bounds["lat_min"], bounds["lon_min"]],
                    [bounds["lat_max"], bounds["lon_max"]]],
            opacity=0.7
        ).add_to(m)

        add_legend(m, stats, weather_data)

    return m


def add_legend(m, stats, weather_data):
    # build weather block if we have data
    weather_html = ""
    if weather_data:
        wind_dir = wind_direction_to_text(weather_data["wind_direction"])
        weather_html = f'''
        <div style="margin-top: 10px; padding-top: 8px; border-top: 1px solid #ccc;
                   font-size: 11px; color: #333;">
            <strong>🌤️ Current Weather:</strong>
            <div style="margin-top: 4px;">
                <div>🌡️ {weather_data['temperature']:.1f}°C</div>
                <div>💨 {weather_data['wind_speed']:.1f} km/h {wind_dir}</div>
                <div>💧 {weather_data['humidity']}% humidity</div>
            </div>
        </div>
        '''

    legend_html = f'''
    <div style="position: fixed; bottom: 50px; right: 50px; width: 220px;
                background: white; border: 2px solid #333; border-radius: 5px;
                box-shadow: 0 0 10px rgba(0,0,0,0.3); z-index: 9999;
                font-family: Arial; padding: 12px">
        <p style="margin: 0 0 8px 0; font-size: 15px; font-weight: bold;
                  border-bottom: 2px solid #333; padding-bottom: 5px;">
            🌡️ Temperature Map
        </p>
        <div style="margin: 8px 0;">
            <div style="display: flex; justify-content: space-between; margin: 4px 0;">
                <span>Hottest:</span>
                <strong style="color: #d62728;">{stats['max']:.1f}°C</strong>
            </div>
            <div style="display: flex; justify-content: space-between; margin: 4px 0;">
                <span>Average:</span>
                <strong style="color: #666;">{stats['mean']:.1f}°C</strong>
            </div>
            <div style="display: flex; justify-content: space-between; margin: 4px 0;">
                <span>Coolest:</span>
                <strong style="color: #1f77b4;">{stats['min']:.1f}°C</strong>
            </div>
        </div>
        <div style="margin-top: 10px; padding-top: 8px; border-top: 1px solid #ccc;
                   font-size: 11px; color: #666;">
            <div><strong>Range:</strong> {stats['range']:.1f}°C</div>
        </div>
        {weather_html}
    </div>
    '''

    m.get_root().html.add_child(folium.Element(legend_html))