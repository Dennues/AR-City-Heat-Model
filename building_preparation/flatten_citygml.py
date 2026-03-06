#!/usr/bin/env python3
"""
CityGML batch processor:
  • Aligns all buildings to a common minimum Z
  • Levels ground surfaces
  • Snaps wall bottoms to the leveled floor
  • Optionally clips geometry in the XY plane
"""

import argparse
import os
import re
import xml.etree.ElementTree as ET
from math import isclose, inf

# ----------------------------------------------------------------------
# Helper functions
# ----------------------------------------------------------------------
def parse_poslist(text: str):
    """Convert a whitespace‑separated posList string to a list of floats."""
    return [float(n) for n in re.split(r"\s+", text.strip()) if n]


def serialize_poslist(nums):
    """Turn a list of floats back into a space‑separated string (3‑decimal)."""
    return " ".join(f"{n:.3f}" for n in nums)


def clip_value(val, min_val, max_val):
    """Clamp a single coordinate to the provided min / max."""
    return max(min(val, max_val), min_val)


def clip_polygon_xy(poslist_nums, min_x, max_x, min_y, max_y):
    """
    Clamp every X/Y pair in a posList to the rectangular bounds.
    Returns the new list and a list of indices where clipping occurred.
    """
    new_nums = []
    clipped_edges = []
    for i in range(0, len(poslist_nums), 3):
        x, y, z = (
            poslist_nums[i],
            poslist_nums[i + 1],
            poslist_nums[i + 2],
        )
        x_clipped = clip_value(x, min_x, max_x)
        y_clipped = clip_value(y, min_y, max_y)
        if not (isclose(x, x_clipped) and isclose(y, y_clipped)):
            clipped_edges.append(i)
        new_nums.extend([x_clipped, y_clipped, z])
    return new_nums, clipped_edges


# ----------------------------------------------------------------------
# Argument parsing
# ----------------------------------------------------------------------
def build_arg_parser():
    parser = argparse.ArgumentParser(
        description="Batch‑process CityGML files: normalize Z, level ground, "
        "snap walls, and optionally clip XY extents."
    )
    parser.add_argument(
        "--input-folder",
        "-i",
        required=True,
        help="Path to the folder containing the original CityGML (.xml) files.",
    )
    parser.add_argument(
        "--output-folder",
        "-o",
        required=True,
        help="Folder where processed files will be written (created if missing).",
    )
    parser.add_argument(
        "--clip-min-x",
        type=float,
        default=-inf,
        help="Minimum X coordinate (e.g 381000.0) for clipping (default: -inf, i.e. no clipping).",
    )
    parser.add_argument(
        "--clip-max-x",
        type=float,
        default=inf,
        help="Maximum X coordinate (e.g 385000.0) for clipping (default: +inf, i.e. no clipping).",
    )
    parser.add_argument(
        "--clip-min-y",
        type=float,
        default=-inf,
        help="Minimum Y coordinate (e.g 5811000.0) for clipping (default: -inf).",
    )
    parser.add_argument(
        "--clip-max-y",
        type=float,
        default=inf,
        help="Maximum Y coordinate (e.g 5814000.0) for clipping (default: +inf).",
    )
    return parser


# ----------------------------------------------------------------------
# Main processing logic
# ----------------------------------------------------------------------
def main():
    args = build_arg_parser().parse_args()

    input_folder = args.input_folder
    output_folder = args.output_folder
    clip_min_x = args.clip_min_x
    clip_max_x = args.clip_max_x
    clip_min_y = args.clip_min_y
    clip_max_y = args.clip_max_y

    # Namespace mapping for CityGML XML
    ns = {
        "gml": "http://www.opengis.net/gml",
        "bldg": "http://www.opengis.net/citygml/building/1.0",
        "core": "http://www.opengis.net/citygml/1.0",
        "gen": "http://www.opengis.net/citygml/generics/1.0",
    }

    os.makedirs(output_folder, exist_ok=True)

    # ------------------------------------------------------------
    # STEP 1 – Find global minimum Z across ALL tiles
    # ------------------------------------------------------------
    print("Scanning all tiles for global minimum Z...")
    global_min_z = inf

    for filename in os.listdir(input_folder):
        if not filename.lower().endswith(".xml"):
            continue

        path = os.path.join(input_folder, filename)
        try:
            tree = ET.parse(path)
        except Exception as e:
            print(f"⚠️  Failed to parse {filename}: {e}")
            continue

        root = tree.getroot()
        ground_poslists = root.findall(".//bldg:GroundSurface//gml:posList", ns)

        for poslist in ground_poslists:
            nums = parse_poslist(poslist.text)
            zmin = min(nums[2::3])  # every third value is a Z
            global_min_z = min(global_min_z, zmin)

    print(f"✔ Global minimum Z = {global_min_z:.3f}\n")

    # ------------------------------------------------------------
    # STEP 2 – Process each tile
    # ------------------------------------------------------------
    for filename in os.listdir(input_folder):
        if not filename.lower().endswith(".xml"):
            continue

        inpath = os.path.join(input_folder, filename)
        outpath = os.path.join(
            output_folder, filename.replace(".xml", "_processed.xml")
        )

        print(f"\n=== Processing tile: {filename} ===")
        try:
            tree = ET.parse(inpath)
        except Exception as e:
            print(f"❌ Failed to parse {filename}: {e}")
            continue

        root = tree.getroot()

        for building in root.findall(".//bldg:Building", ns):
            # Gather all posLists we might need to edit
            poslists = building.findall(".//gml:posList", ns)
            ground_poslists = building.findall(
                ".//bldg:GroundSurface//gml:posList", ns
            )
            wall_poslists = building.findall(
                ".//bldg:WallSurface//gml:posList", ns
            )

            if not poslists:
                continue

            # ----------------------------------------------------
            # A. Local minimum Z for this building
            # ----------------------------------------------------
            local_min = inf
            for pl in poslists:
                nums = parse_poslist(pl.text)
                local_min = min(local_min, min(nums[2::3]))

            delta_z = global_min_z - local_min

            # ----------------------------------------------------
            # B. Translate whole building by delta_z
            # ----------------------------------------------------
            for pl in poslists:
                nums = parse_poslist(pl.text)
                for i in range(2, len(nums), 3):
                    nums[i] += delta_z
                pl.text = serialize_poslist(nums)

            # ----------------------------------------------------
            # C. Level the GroundSurface to the global minimum Z
            # ----------------------------------------------------
            for pl in ground_poslists:
                nums = parse_poslist(pl.text)
                for i in range(2, len(nums), 3):
                    nums[i] = global_min_z
                pl.text = serialize_poslist(nums)

            # ----------------------------------------------------
            # D. Snap wall bottoms to the leveled floor
            # ----------------------------------------------------
            for pl in wall_poslists:
                nums = parse_poslist(pl.text)
                zvals = nums[2::3]
                wall_min = min(zvals)

                for i in range(2, len(nums), 3):
                    if isclose(nums[i], wall_min, abs_tol=0.2):
                        nums[i] = global_min_z
                pl.text = serialize_poslist(nums)

            # ----------------------------------------------------
            # E. Clip XY coordinates (optional)
            # ----------------------------------------------------
            for pl in poslists:
                nums = parse_poslist(pl.text)
                clipped_nums, _ = clip_polygon_xy(
                    nums, clip_min_x, clip_max_x, clip_min_y, clip_max_y
                )
                pl.text = serialize_poslist(clipped_nums)

        # Write the modified file
        tree.write(outpath, encoding="utf-8", xml_declaration=True)
        print(f"✔ Saved: {outpath}")

    print("\n=== ALL TILES PROCESSED ===")


if __name__ == "__main__":
    main()