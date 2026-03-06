# export temperature points in .csv format

import csv


def export_temperature_csv(temp_grid, lat_range, lon_range, output_path='temperature_data.csv'):
    print(f"Exporting to {output_path}...")

    with open(output_path, 'w', newline='') as f:
        writer = csv.writer(f)
        writer.writerow(['latitude', 'longitude', 'temperature'])

        for i in range(len(lat_range)):
            for j in range(len(lon_range)):
                writer.writerow([
                    f'{lat_range[i]:.6f}',
                    f'{lon_range[j]:.6f}',
                    f'{temp_grid[i, j]:.2f}'
                ])

    print(f"Done! Saved {len(lat_range) * len(lon_range)} points")
    return output_path