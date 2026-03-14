# AR City Heat Model



## Setup and Requirements

**Clone the project (this may take some time):**

```bash
git clone https://github.com/Dennues/AR-City-Heat-Model/
```

## Setup the Unity project 

### Requirements

* [Unity 6000.3.8f1](https://unity.com/download) (with Android build support)

### Setup & Run

* Open the `cityHeatModelUnity` folder as a Unity project (this may take some time as well, because Unity has to recreate some files)
* Now open the scene `cityHeatModelUnity/Assets/Scenes/ARScene.unity`
* Under *File* &#8594; *Build Profiles* switch the Platform to *Android* (the only one tested in this version)
    * At first, there might be some errors, but after switching to Android, they usually disappear.
* Now you are ready to work with the Unity project.
    * You don't have any experience with Unity? Try their [tutorials](https://learn.unity.com/tutorial/start-learning-unity).

Build only tested on Android - Samsung Galaxy S24+ and Pixel 6 (Android 16)

---

## Setup the Heroku Server (for dynamic loading)
### Requirements

- Python
- NodeJS
- Heroku account or similar

**Install dependencies:**
```bash
cd heat_heroku_server
pip install -r requirements.txt
```

### Setup & Run
* Create an account for [Heroku](https://www.heroku.com/platform/) (or a similar service) and create a new app on their website
    * Optionally make use of the [GitHub Student Developer Pack](https://education.github.com/pack) to receive free platform credits for Heroku
* Enter the `heat_heroku_server` folder using cd, log in from there, and attach the Heroku remote using the Heroku Git URL provided in the settings section for your app:
```bash
heroku login
git remote add heroku https://git.heroku.com/heatmap.git
```
* Configure buildpacks (needs both Python and Node):
```bash
heroku buildpacks:add -a heatmap heroku/python
heroku buildpacks:add -a heatmap heroku/nodejs
```
* Make sure that the order is correct (you may also check the settings section in the web-dashboard) and that python is above nodejs
* Deploy the app (choose *master* instead of *main* if needed with your setup):
```bash
git add .
git commit -m "Deploy heatmap server"
git push heroku main
```
* On the web-dashboard for your app make sure that under Resources your Dynos is running
* The Unity app should be able to reach the backend now
* Under *More* click *View logs* in order to observe the logs of your app as it is running
* During generations empty errors may appear between the status messages, which can be ignored

---

## Setup the Data-Exploration (for .csv generation)

### Requirements

- Python 3.10+
- Internet connection (OpenStreetMap + open-meteo API)

**Install dependencies:**
```bash
cd temperature_model
pip install -r requirements.txt
```

### Setup & Run

1. Set your study area in `config.py` → `BOUNDS` (Default FU-Berlin)
2. Run the main script:
```bash
python main.py
```
3. Open `map.html` in a browser to see the result
4. `temperature_data.csv` is ready for import in Unity

---

## Building preparation (CityGML to objects)
### Requirements

- A folder with your chosen LoD2 building .xml-files from [Geodatensuche Berlin](https://gdi.berlin.de/geonetwork/srv/ger/catalog.search#/metadata/8a7ea996-7955-4fbb-8980-7be09be6f193)
- Python

**Clone the following external GitHub repository and install its dependencies:**
```bash
git clone https://github.com/tum-gis/CityGML2OBJv2
cd CityGML2OBJv2
pip install -r requirements.txt
```

### Process

1. Go into `building_preparation`:
```bash
cd building_preparation
```
2. Find out the bounding box (in ETRS89 / UTM33) of your new region. For example, between `[381000.0, 5811000.0]` and `[385000.0, 5814000.0]` in our case.
3. Run `flatten_citygml.py` and replace the input and output folders with yours. Additionally replace the bounds with the ones from the previous step. Buildings will be cut of there.
```bash
python flatten_citygml.py -i <folder_with_xml_files> -o <folder_for_flattened_citygml> --clip-min-x <min_x> --clip-max-x <max_x> --clip-min-y <min_y> --clip-max-y <max_y>
```
4. Now navigate to `CityGML2OBJv2` which you should have previously cloned.
5. Use [CityGML2OBJv2](https://github.com/tum-gis/CityGML2OBJv2) to convert the flattened CityGML to objects (use absolute paths for your folders):
```bash
python CityGML2OBJs.py -i <abs_path_to_flattened_citygml_folder> -o <abs_path_to_output_folder> -tC 1
```
6. Finally, you can drag the building objects somewhere under `Assets` in the Unity project and then drag them into the scene.
7. Scaling it to `0.01` and setting rotation around the X axis to `-90` should make it visible.
8. Now you can work with these building tiles as you wish. Alignment with the 2D map and other building tiles was done by hand.


## Further Documentation

For further documentation visit our [Wiki](https://github.com/Dennues/AR-City-Heat-Model/wiki).