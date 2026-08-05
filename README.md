A tool for exporter and importing assets for ps2 game Yu-Gi-Oh Duelist of the Roses.
Used for modding and preservation purposes
Should work on cross platform and for all versions of the game.
requires .NET 8

## NOTE
This has moderate to extensive use of AI primarily for the importing and exporting of the 3D Models, I do apologise, but many things around this project are out of my skillset, very time consuming and some of the formats are Konami specific formats with no documentation or uses elsewhere. I do not currently have the time, energy and passion to learn all the required skills that would normally be needed for this project without AI. But I know many people would like to have this functionality to unlock one of the last few hurdles in modding this game.
I have tried to label classes functions and code written by AI, and to the extent of what of it is AI code where i could. 

### This is in Alpha, please report bugs, recommendations, features you would like to see


## Requirements

- **.NET 8**: Ensure that you have .NET 8 installed on your system. You can download it from the official Microsoft website: [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/8.0?)
- Your own Duelist of the Roses ISO, this tool doesn't not work without one, and it modifies the one you open so please make backups.

- 1. **Download the Latest Release**:
   - Visit the [DotrAssetModder Releases Page](https://github.com/batzpup/DotrAssetModder/releases) to download the latest version of the tool.

2. **Run the Application**:
   - After downloading, extract the contents of the ZIP file.
   - Navigate to the extracted folder and run `DotrAssetModdder.exe`.
  
3. If it doesn't open, go to the runtimes folder, copy and paste the folder matching your operation system into main DotrAssetModder directory (next to DotrAssetModder.exe)

## Features

### Extract all the games 3D models and 2D textures as GLTF and PNG's respectively

### Import New 3D monster models into the game

### Edit textures files with in built tools, or import a new texture with a PNG

### Extract Game BGMs


## How To Use
### Importing 3D models
The most import aspect to importing new 3D models is having the hierarchy correct.
For the tool to work properly you must:
- Have RootNode that only contain one child node which determines the world position placement of the model
- The World Position node must only have one child which determines the models pivot point for rotation and where the camera will look at when focusing on the model
   - Recommend having the world pos and root at 0,0 and then adjust the pivot point.
- RootNode->WorldPos->ModelPivot/Camera -> any number of children
if this is not done models will appear too high or too low, the will rotate around a spot in front or behind them, the camera will not look at their center.
Use extracted 3D models as a size reference 
Good model Example:
<img width="1991" height="1328" alt="image" src="https://github.com/user-attachments/assets/e4fedf86-7cee-4830-a6d6-93baed0ad84a" />

### Texture notes
Keep textures sizes down, you cannot import a model greater than 1mb after compression from the importer.
The max texture sizes may not be bigger than 512x512 and for textures on models 256 is the recommended max size
Texture dimensions are required to be powers of 2

### Animated Textures
To Explain

## TODO
Extract And Import the following:
- Movies (PSS files)
- Game Sound Effects (Export and Import)
- Import new BGMS

### Currently not feasible or unable to do without full iso rebuilding + extensive modding knowledge and effort
- Import non Monster 3D Models
- Custom Battle animations & Battle camera changes



