##  A tool for exporting and importing assets for ps2 game Yu-Gi-Oh Duelist of the Roses.
 Used for modding and preservation purposes
 Should work on cross platform and for all versions of the game.

## NOTES

### AI Notice and disclosure:
This has moderate to extensive use of AI primarily for the importing and exporting of the 3D Models, I do apologise, but many things around this project are out of my skillset, very time consuming and some of the formats are Konami specific formats with no documentation or uses elsewhere. I do not currently have the time, energy and passion to learn all the required skills that would normally be needed for this project without AI. But I know many people would like to have this functionality to unlock one of the last few hurdles in modding this game. If you do not wish to use this project because of the AI usage I completely understand.
Files written mostly by AI are marked with a `// Mostly AI` comment at the top.

### This is in Alpha, please report bugs, recommendations, features you would like to see


## Requirements

- **.NET 8**: Ensure that you have .NET 8 installed on your system. You can download it from the official Microsoft website: [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Your own Duelist of the Roses ISO. This tool doesn't work without one, and **it modifies the ISO you open, so please make backups.**

## Getting started

1. **Download the latest release** from the [DotrAssetModder Releases Page](https://github.com/batzpup/DotrAssetModder/releases).
2. **Run the application**:
   - Extract the contents of the ZIP file.
   - Run `DotrAssetModder.exe`.
3. If it doesn't open, go to the `runtimes` folder, and copy the folder matching your operating system into the main DotrAssetModder directory (next to `DotrAssetModder.exe`).
4. In the **Extractor** tab, click **Load ISO** (or drag the ISO onto the window).

## Features

The tool has four tabs:

### Extractor
- Extract the game's 3D models as `.glb` (with all their animations) and textures as `.png`.
- Extract the game's BGMs.
- Movies: not supported yet, the tab links to tools you can use instead (see below).

### Texture Viewer
- Browse every texture in the game's texture archives.
- Edit textures with the built-in tools (pencil, eraser, fill, eyedropper, undo/redo), or import a PNG, then save the slot back into the ISO.
- Export any texture as PNG.

### Model Viewer
- View any `.glb` / `.gltf` / `.fbx` / `.dae` model (Load Model, Load Folder, or drag a file onto the window).
- Play and scrub through its animations.
- See it as the game places it: **Grid** (battle, real size), **Duel board**, or **Map leader**, using
  scenery from your loaded ISO.

### Model Importer
- **View** any model as it is on the ISO, or **Extract .glb** of it.
- **Import a new model** into any slot from `.glb`, `.gltf`, `.fbx` or `.dae`.
- **Edit an existing card's model** (scale, rotate, move, pivot) without leaving the game's own data.
- **Remove model**: for when you dont have a 3d model for a new card
- Preview imports before saving, with you are able compare your new mode with the model it replaces (or Dark Magician as its an average sized model) beside it.
- Stage as many imports as you want, then **Save to ISO** once. **Check plan** shows what would be
  written without writing anything.
- Edit a card's height on the world map (Map leader); written on Save to ISO.


## How To Use

### Importing 3D models
1. Load your ISO, open the **Model Importer** tab, and select the card you want to replace.
2. Click **Load Model** (or drag a `.glb` / `.gltf` / `.fbx` / `.dae` onto the window).
3. The **Convert** window opens:
   - **Animations**: pick which of your model's animations plays in each game slot
     (Idle, Attack, Death, Win, Hurt). Animations with `slot_N` in their name are matched automatically.
   - **Transform**: Scale, Rotate (degrees, Y is up), Position, and **Pivot**: the point the game
     turns and scales the model about, and where the camera looks.
   - **Source fixes**: *Bind at first frame* (when the model's rest pose is wrong), *Flip UV* (when textures appear upside down).
   - **Radius**: the model's bounding size the game uses. It is measured for you; only change it if
     you need to. Increase the radius to scale down the model on the duel board and world map.
   - Hover over any option for an explanation.
4. Click **View import** to check it in the preview (Grid / Duel board / Map leader, and Compare).
5. Click **Import** to stage it. Repeat for any other cards.
6. Click **Save to ISO**.

You don't need to build a special node hierarchy any more: the tool wraps your model in the nodes the
game needs. Keep your model at its real size: extracted models are a good size reference (the
preview's Compare option shows the original beside yours).

### Editing an existing card's model
Select a card and click **View**: the card opens with the Convert options, so you can change its
Scale / Rotate / Position / Pivot / Radius and click **Import** to stage the edit.
The card keeps its own model, textures and animations. 163 Supporter in the Shadows and 207 The Little Swordsman of Aile can only have their radius changed due to its irregular node structure and animation, if you wish to edit these you will need to export them out and reimport, and the tool will fix the node structure.

### Animation notes
The game has five motion slots: **Idle, Attack, Death, Win, Hurt**.
A model missing one of these gets a copy of another motion in its place (the import status says which), so it still plays in battle, but a copied idle never shows a fall: give the model its own death and hurt animations if possible, if you care about the 3d battles.
The 3d battle attack animations cannot be edited at the present time, so whatever you put into slot 0 and view the battle animation with, will use blue-eyes white dragons attack beam.

### Texture notes
- A monster model must fit in **1 MB** after compression. If it doesn't fit, tick
  **Allow texture/animation reduction to fit** and the importer will shrink textures and animations until it does.
- Textures are kept at their own size up to **512x512**; 256 or smaller is recommended for models.
- Texture dimensions must be powers of 2.
- Parts with no texture are drawn with a plain white texture.

### Movies
Movie editing is not built in yet. To edit the movies:
1. Extract the ISO with [Ps2 ISO rebuilder](https://drive.google.com/file/d/10DJ-fYgSx6D7vSdwVCTa9FHFbIWZE7xU/view).
2. Split the movie files, edit them, and recombine them with [Ps2 PSS Mux](https://github.com/wagrenier/PssMux/releases).
3. Rebuild the ISO with the ISO tool.

## TODO
Extract and import the following:
- Movies (PSS files)
- Game sound effects (export and import)
- Import new BGMs
- Animated textures

### Currently not feasible or unable to do without full iso rebuilding + extensive modding knowledge and effort
- Custom battle animations & battle camera changes
