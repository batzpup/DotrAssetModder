using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using Raylib_cs;
using static DotrAssetModder.glRD;

namespace DotrAssetModder;

// Where a preview stands a model, as the game does: Feet on grid (battle, real size), Duel board
// or Map leader, with the board or map drawn around it from the ISO's MODEL.MRG. Shared by the
// importer's preview and the Model Viewer.
public class ViewScene
{
    const int MonsterCount = 683;

    // Where the preview stands the models. Feet: lowest idle point on the grid, real size (battle
    // draws monsters unscaled). Board: node 0 BoardHeight above the grid, which is the card
    // (SzDuel_AddUnit2 puts node 0 at y 50, the card at y 0; SzBattle_Entry and SzDuel_MoveUnit
    // change only X/Z). Map: scale 0.5, node 0 at the card's Target_Model_Y above the grid (the
    // arena point). Board and map both shrink a model by its radius (BoardShrink).
    // Board models stand on tiles TilePitch apart (SzDuel_CalcUnitPos: (col-3)*50, (row-3)*50).
    public const int PlaceFeet = 0;
    public const int PlaceBoard = 1;
    public const int PlaceMap = 2;
    static readonly string[] PlaceChoices = { "Grid", "Duel board", "Map leader" };
    public const float BoardHeight = 50f;
    public const float MapScale = 0.5f;
    public const float TilePitch = 50f;
    public int Placement = PlaceFeet;

    // Scenery for Duel board and Map leader, from this ISO's MODEL.MRG: the Normal terrain tile
    // (FIELD 7; the board draws tiles at y 0, SzDuel_CalcUnitPos) and the map (EVENT2, the pack
    // TkCamp_Init merges as type 3 index 2: land 0-7, pedestals 8-10, arena markers 11-26).
    const int FieldPack = 38;
    const int NormalTile = 7;
    const int MapPack = 35;
    const int MapLandCount = 8;
    const int MapPieceCount = 27;
    // The duel stage (lSzDuel_DrawStage_Pipe; places from the model table @ 0x28A880, to which a
    // draw call's position and turn are added): pack DUEL00 models 0-2 (table and board, emblem,
    // dark crystal) at (0,-35,0); a 7x7 grid of FIELD tiles at ((col-3)*50, -35, (row-3)*50)
    // turned 180; and the two stained-glass windows (model ids 5-6: side 0 red, side 1 white) at
    // (0,-35,0), each drawn twice, turned 180 -+ 45 and 0 -+ 45. Which window index is which
    // duelist is not mapped. The cyan and pink crystals (models 3-4, SzDuel_DrawStageAfter_Pipe)
    // are left out: the game moves them at run time (they belong in the board's corners) and
    // where they go is not traced.
    const int DuelPack = 0;
    const int DuelPieceCount = 3;
    const int DuelWindowsPerSide = 21;
    const float StageY = -35f;
    const int BoardTiles = 7;
    Task<byte[][]> sceneryTask;
    AnimatedGltfModel boardTile;
    AnimatedGltfModel[] mapPieces;
    float seaTime;                                     // the sea's own motion clock (AnimateSea)
    AnimatedGltfModel[] duelPieces;
    AnimatedGltfModel[] duelWindows;                   // side 0, side 1
    bool sceneryLoaded;
    ModelMrg mrg;                                      // the ISO's MODEL.MRG the scenery comes from
    public int MapArena;

#if DEBUG
    public bool DebugLines;                                   // Duel board: tile centres, node 1 and body boxes
#endif
    public string Error;                               // set when the scenery failed to load

    // The ISO the scenery comes from; a new one drops the old scenery.
    public void SetSource(ModelMrg source)
    {
        if (source == mrg)
        {
            return;
        }
        Unload();
        mrg = source;
    }

    public bool Loading
    {
        get { return Placement != PlaceFeet && sceneryTask != null; }
    }

    // The board or map is drawn instead of the grid.
    public bool SceneryShown
    {
        get { return Placement != PlaceFeet && sceneryLoaded; }
    }

    // The View combo (monsters only: a MODEL.MRG block stays on the grid), the arena slider and, in debug builds, the
    // debug lines toggle. `mapCard` gives the map height; `pickCard` lets the user choose it (a
    // file has no card), else the card's height is edited (in the tables only; the importer
    // saves it). True when the placement changed.
    public bool DrawControls(bool isMonster, ref int mapCard, bool pickCard, string extraTip)
    {
        if (!isMonster)
        {
            // only monsters stand on the duel board or lead a deck on the map
            bool wasPlaced = Placement != PlaceFeet;
            Placement = PlaceFeet;
            return wasPlaced;
        }
        bool changed = false;
        ImGui.SetNextItemWidth(FieldWidth(0.25f, 8f));
        if (ImGui.Combo("View", ref Placement, PlaceChoices, PlaceChoices.Length))
        {
            changed = true;
        }
        Request();
        HoveredTooltip("Grid: the lowest idle point on the grid, at real size (battle draws monsters unscaled)." +
            "\nDuel board: as the game stands a monster over its card, and in battle (the same height):" +
            "\nnode 0 50 above the grid, which is the card." +
            "\nMap leader: as the map draws the deck leader, at half size, node 0 at a height the game" +
            "\nkeeps per card (Target_Model_Y), not in the model." +
            "\nThe map's ground is not measured: retail leaders' feet land 11-17 above this grid (median 14)," +
            "\nso the ground is likely about there." +
            "\nBoard and map shrink a model whose radius is over 40 to fit 40 (at least x0.3), as the game does." +
            extraTip);
        if (Placement == PlaceMap && mrg != null)
        {
            // Card / Map height and Arena on their own line, labels on the left.
            if (pickCard)
            {
                ImGui.Text("Card");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(FieldWidth(0.12f, 5f));
                if (ImGui.InputInt("##mapcard", ref mapCard))
                {
                    mapCard = Math.Clamp(mapCard, 0, MonsterCount - 1);
                }
                HoveredTooltip("Whose map height to use: the game keeps it per card, not in the model.");
            }
            else if (mapCard >= 0 && mapCard < MonsterCount)
            {
                ImGui.Text("Map height");
                ImGui.SameLine();
                int height = mrg.Tables.MapLeaderY(mapCard);
                ImGui.SetNextItemWidth(FieldWidth(0.12f, 5f));
                if (ImGui.InputInt("##mapheight", ref height))
                {
                    mrg.Tables.SetMapLeaderY(mapCard, Math.Clamp(height, sbyte.MinValue, sbyte.MaxValue));
                }
                HoveredTooltip("How high the map stands this card's deck leader (node 0, Target_Model_Y)." +
                    "\nThe game keeps it per card, not in the model; Save to ISO writes it.");
            }
            if (pickCard || (mapCard >= 0 && mapCard < MonsterCount))
            {
                ImGui.SameLine();
            }
            ImGui.Text("Arena");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(FieldWidth(0.15f, 5f));
            ImGui.SliderInt("##arena", ref MapArena, 0, mrg.Tables.Addresses.ArenaCount - 1);
            HoveredTooltip("Which map arena the leader stands on; the map moves so that arena is under it.");
        }
        if (Placement != PlaceFeet && mrg == null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(load an ISO to see the board/map)");
        }
#if DEBUG
        if (Placement == PlaceBoard)
        {
            ImGui.SameLine();
            ImGui.Checkbox("Debug lines", ref DebugLines);
            HoveredTooltip("Yellow: the tile's outline and a post up its centre." +
                "\nGreen: node 1 at rest, the point the game puts over the tile centre (and scales about)." +
                "\nMagenta: node 0's stand point (50 above the card)." +
                "\nCyan: the box the idle animation fills (all its frames), its footprint and a post up its middle," +
                "\nto see how the body sits against the tile. A wide or long one is the wings or tail.");
        }
#endif
        if (Loading)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Loading the board/map...");
        }
        return changed;
    }

    // A model's height, and in Board/Map its radius and how much the game shrinks it there.
    public string StandingText(string name, float height, float radius)
    {
        string text = $"{name}: {height:0.#} tall";
        if (Placement != PlaceFeet)
        {
            text += $", radius {radius:0.#} -> x{ModelScale(radius):0.##}";
        }
        return text;
    }

    // The grid, 1 unit squares, wide enough to run under the models from `left` to `right` and
    // `deep` front to back: centred between them and coarser (10, 100) when that would take over
    // 200 lines. Lines stay on whole units of the origin.
    public static void DrawFloorGrid(float left, float right, float deep)
    {
        float size = MathF.Max(MathF.Max(right - left, deep) * 1.5f, 100f);
        float spacing = 1f;
        while (size / spacing > 200f)
        {
            spacing *= 10f;
        }
        int slices = (int)MathF.Ceiling(size / spacing);
        float middle = MathF.Round((left + right) * 0.5f / spacing) * spacing;
        Rlgl.PushMatrix();
        Rlgl.Translatef(middle, 0f, 0f);
        Raylib.DrawGrid(slices, spacing);
        Rlgl.PopMatrix();
    }

    // How far above the grid node 0 stands for card `card`'s model, whose lowest idle point
    // is `lowest` (before ModelScale).
    public float StandY(float lowest, int card)
    {
        if (Placement == PlaceBoard)
        {
            return BoardHeight;
        }
        if (Placement == PlaceMap)
        {
            if (mrg == null || card < 0)
            {
                return 0f;
            }
            return mrg.Tables.MapLeaderY(card);
        }
        return -lowest;
    }

    // Board and map: where `model` draws, as stand point + shift + s * (glb point). The unit's
    // position is node 0 (SzDuel_AddUnit2 SetPos ((col-3)*50, 50, (row-3)*50), absolute; the
    // table's own (0,50,0) for slots 0x14-0x1F is overwritten, and SzDuel_DrawUnit_Pipe adds
    // only the slide offsets and fieldRate*35/128 that the tiles get too). With deformRatio 0x20
    // SzModel_DrawWithFunc_Pipe draws pos + (0, c.y, 0) + s * (v - c), c = node 1's REST offset
    // from node 0 (node tree +0x20 / pos scale; the motion never writes it): node 1's rest
    // point lands over the tile centre at its own height, and the model scales about it.
    // In glb terms c = P - N0, v - c = g - P. Feet: no shift (real size, lowest on the grid).
    public Vector3 StandShift(AnimatedGltfModel model, float s)
    {
        if (model == null)
        {
            return Vector3.Zero;
        }
        return StandShift(model.RestPivot, model.RestNodeZero, s);
    }

    // The same for a glb whose node 1 rests at `p` and node 0 at `n0` (the importer's preview
    // while an option is changed and not yet converted).
    public Vector3 StandShift(Vector3 p, Vector3 n0, float s)
    {
        if (Placement == PlaceFeet)
        {
            return Vector3.Zero;
        }
        return new Vector3(-s * p.X, p.Y - n0.Y - s * p.Y, -s * p.Z);
    }

    // How big the game draws a model whose node 0 radius is `radius`: real size in battle (Feet),
    // shrunk to fit radius 40 on the board, and that at half size on the map.
    public float ModelScale(float radius)
    {
        if (Placement == PlaceBoard)
        {
            return BoardShrink(radius);
        }
        if (Placement == PlaceMap)
        {
            return MapScale * BoardShrink(radius);
        }
        return 1f;
    }

    // Sets the clip planes for a camera orbiting `camera.Target` around models drawn within
    // `drawnRadius` of the origin. Near follows the orbit distance rather than the model's size,
    // so a model whose node scales inflate its bind bounds is not cut off in front of the camera.
    // Raylib builds the projection in BeginMode3D, so call this before it.
    public void SetClipPlanes(Camera3D camera, float drawnRadius)
    {
        float dist = Vector3.Distance(camera.Position, camera.Target);
        float near = MathF.Max(dist * 0.01f, 0.01f);
        float reach = drawnRadius;
        if (Placement != PlaceFeet)
        {
            reach = MathF.Max(reach + BoardHeight, 800f);   // the map's sea and the duel windows reach ~800 out
        }
        float far = MathF.Max((camera.Position.Length() + reach) * 2f, 100f);
        Rlgl.SetClipPlanes(near, far);
    }

    // The board's shrink: lSzModel_LoadCB @ 0x1E8BF0 sets deformScale = clamp(40 / radius, 0.3, 1)
    // from node 0's radius, and lSzDuel_InitCom turns it on in full for the board's monsters.
    public static float BoardShrink(float radius)
    {
        if (!(radius > 1e-3f))
        {
            return 1f;
        }
        return Math.Clamp(40f / radius, 0.3f, 1f);
    }

    // Starts extracting the board and the map pieces the first time a Placement needs them:
    // the Normal tile, the 27 map pieces, the duel stage models and one window per side.
    public void Request()
    {
        if (Placement == PlaceFeet || sceneryLoaded || sceneryTask != null || mrg == null)
        {
            return;
        }
        List<byte[]> blocks = new List<byte[]>();
        blocks.Add(mrg.BlockBytes(mrg.Sub(FieldPack, NormalTile).Index));
        for (int k = 0; k < MapPieceCount; k++)
        {
            blocks.Add(mrg.BlockBytes(mrg.Sub(MapPack, k).Index));
        }
        for (int k = 0; k < DuelPieceCount; k++)
        {
            blocks.Add(mrg.BlockBytes(mrg.Sub(DuelPack, k).Index));
        }
        if (mrg.Windows.Count >= 2 * DuelWindowsPerSide)
        {
            blocks.Add(mrg.BlockBytes(mrg.Windows[0].Index));
            blocks.Add(mrg.BlockBytes(mrg.Windows[DuelWindowsPerSide].Index));
        }
        sceneryTask = Task.Run(() =>
        {
            byte[][] glbs = new byte[blocks.Count][];
            for (int i = 0; i < blocks.Count; i++)
            {
                glbs[i] = ModelExtractor.ExtractGlb(blocks[i]);
            }
            return glbs;
        });
    }

    public void Update()
    {
        if (sceneryTask == null || !sceneryTask.IsCompleted)
        {
            return;
        }
        if (sceneryTask.IsFaulted)
        {
            Error = "Board/map scenery failed: " + sceneryTask.Exception.InnerException.Message;
            sceneryTask = null;
            sceneryLoaded = true;                      // do not retry every frame
            return;
        }
        byte[][] glbs = sceneryTask.Result;
        sceneryTask = null;
        int next = 0;
        boardTile = LoadStill(glbs[next]);
        next++;
        mapPieces = new AnimatedGltfModel[MapPieceCount];
        for (int k = 0; k < MapPieceCount; k++)
        {
            mapPieces[k] = LoadStill(glbs[next]);
            next++;
        }
        duelPieces = new AnimatedGltfModel[DuelPieceCount];
        for (int k = 0; k < DuelPieceCount; k++)
        {
            duelPieces[k] = LoadStill(glbs[next]);
            next++;
        }
        if (next + 2 <= glbs.Length)
        {
            duelWindows = new AnimatedGltfModel[2];
            duelWindows[0] = LoadStill(glbs[next]);
            duelWindows[1] = LoadStill(glbs[next + 1]);
        }
        sceneryLoaded = true;
    }

    static AnimatedGltfModel LoadStill(byte[] glb)
    {
        AnimatedGltfModel m = AnimatedGltfModel.Load(glb);
        m.SetAnimationFrame(-1, 0f);
        return m;
    }

    public void Unload()
    {
        sceneryTask = null;
        sceneryLoaded = false;
        if (boardTile != null)
        {
            boardTile.Dispose();
            boardTile = null;
        }
        DisposeAll(mapPieces);
        mapPieces = null;
        DisposeAll(duelPieces);
        duelPieces = null;
        DisposeAll(duelWindows);
        duelWindows = null;
    }

    static void DisposeAll(AnimatedGltfModel[] models)
    {
        if (models == null)
        {
            return;
        }
        foreach (AnimatedGltfModel m in models)
        {
            m.Dispose();
        }
    }

    // The duel stage under the board models (Duel board), or the map around the chosen arena with
    // every arena's pedestal and marker (Map leader). Scenery is in its own world units, unscaled.
    public void DrawScenery()
    {
        if (Placement == PlaceBoard && boardTile != null)
        {
            DrawBoard();
        }
        else if (Placement == PlaceMap && mapPieces != null)
        {
            float ax;
            float ay;
            float az;
            int pedestal;
            int marker;
            mrg.Tables.Arena(MapArena, out ax, out ay, out az, out pedestal, out marker);
            // land and every arena's pedestal and marker (Home_Camp_3D, one per arena record; the
            // chosen one is under the preview), then the sea's black plane (7) before the additive sea
            // over it (6): additive pieces write no depth, so a later opaque piece would cover them.
            // A compare model stands beside it on bare land.
            for (int k = 0; k < MapLandCount - 2; k++)
            {
                DrawAt(mapPieces[k], -ax, -ay, -az);
            }
            for (int a = 0; a < mrg.Tables.Addresses.ArenaCount; a++)
            {
                float x;
                float y;
                float z;
                mrg.Tables.Arena(a, out x, out y, out z, out pedestal, out marker);
                DrawArena(x - ax, y - ay, z - az, pedestal, marker);
            }
            AnimateSea();
            DrawAt(mapPieces[7], -ax, -ay, -az);
            DrawAt(mapPieces[6], -ax, -ay, -az);
        }
    }

    // The sea moves by its own motion, not by code: EVENT2/06 (caustics) slides node 1 X 25 -> 12
    // -> 25 over its 10 s slot 0, EVENT2/07 (black plane) Z 0 -> 30 over 20 s; SzModel_Motion
    // @ 0x1ED4A0 loops each model's active slot every frame and Home_Camp_3D @ 0x22A710 just draws
    // them. Two small meshes re-posed per frame; it runs while paused too, as in the game.
    void AnimateSea()
    {
        seaTime += Raylib.GetFrameTime();
        if (seaTime > 600f)
        {
            seaTime -= 600f;                           // a whole number of both loops; keeps float precision
        }
        for (int k = MapLandCount - 2; k < MapLandCount; k++)
        {
            AnimatedGltfModel sea = mapPieces[k];
            float dur = sea.Duration(0);
            if (sea.AnimationCount > 0 && dur > 0f)
            {
                sea.SetAnimationFrame(0, seaTime % dur);
            }
        }
    }

    // The opaque part of the duel stage, in lSzDuel_DrawStage_Pipe's order: the 7x7 tiles, the
    // table, emblem and dark crystal, then each window twice. The board's centre tile (col 3,
    // row 3) is under the preview's model; a compare model stands on the tile to its left.
    void DrawBoard()
    {
        for (int row = 0; row < BoardTiles; row++)
        {
            for (int col = 0; col < BoardTiles; col++)
            {
                DrawAtTurned(boardTile, (col - 3) * TilePitch, StageY, (row - 3) * TilePitch, 180f);
            }
        }
        if (duelPieces != null)
        {
            for (int k = 0; k < DuelPieceCount; k++)
            {
                DrawAt(duelPieces[k], 0f, StageY, 0f);
            }
        }
        if (duelWindows != null)
        {
            DrawAtTurned(duelWindows[0], 0f, StageY, 0f, 180f - 45f);
            DrawAtTurned(duelWindows[0], 0f, StageY, 0f, 180f + 45f);
            DrawAtTurned(duelWindows[1], 0f, StageY, 0f, -45f);
            DrawAtTurned(duelWindows[1], 0f, StageY, 0f, 45f);
        }
    }

    void DrawArena(float x, float y, float z, int pedestal, int marker)
    {
        if (pedestal >= 0 && pedestal < MapPieceCount)
        {
            DrawAt(mapPieces[pedestal], x, y, z);
        }
        if (marker >= 0 && marker < MapPieceCount)
        {
            DrawAt(mapPieces[marker], x, y, z);
        }
    }

    // A model in its own coordinates, moved by (x, y, z): Draw() recentres, the centre goes back.
    static void DrawAt(AnimatedGltfModel model, float x, float y, float z)
    {
        Rlgl.PushMatrix();
        Rlgl.Translatef(x + model.Center.X, y + model.Center.Y, z + model.Center.Z);
        model.Draw();
        Rlgl.PopMatrix();
    }

    // The same, turned about the vertical axis by `degrees` first.
    static void DrawAtTurned(AnimatedGltfModel model, float x, float y, float z, float degrees)
    {
        Rlgl.PushMatrix();
        Rlgl.Translatef(x, y, z);
        Rlgl.Rotatef(degrees, 0f, 1f, 0f);
        Rlgl.Translatef(model.Center.X, model.Center.Y, model.Center.Z);
        model.Draw();
        Rlgl.PopMatrix();
    }

#if DEBUG
    // `lo` / `hi`: the idle animation's box in the glb (MeasureStanding), drawn where the model is.
    public void DrawDebugViewLines(AnimatedGltfModel model, float x, float lowest, int card, float radius, Vector3 lo, Vector3 hi)
    {
        const float TileTop = StageY + 5f;             // FIELD tile geometry is 0..5 high
        float half = TilePitch * 0.5f;
        float stand = StandY(lowest, card);
        float s = ModelScale(radius);
        Vector3 shift = StandShift(model, s);

        Vector3 a = new Vector3(x - half, TileTop, -half);
        Vector3 b = new Vector3(x + half, TileTop, -half);
        Vector3 c = new Vector3(x + half, TileTop, half);
        Vector3 d = new Vector3(x - half, TileTop, half);
        Raylib.DrawLine3D(a, b, Color.Yellow);
        Raylib.DrawLine3D(b, c, Color.Yellow);
        Raylib.DrawLine3D(c, d, Color.Yellow);
        Raylib.DrawLine3D(d, a, Color.Yellow);
        Raylib.DrawLine3D(a, c, Color.Yellow);
        Raylib.DrawLine3D(b, d, Color.Yellow);
        Raylib.DrawLine3D(new Vector3(x, TileTop, 0f), new Vector3(x, stand + TilePitch, 0f), Color.Yellow);

        Vector3 pivot = new Vector3(x, stand, 0f) + shift + s * model.RestPivot;
        Raylib.DrawSphere(pivot, 1f, Color.Green);
        Raylib.DrawSphere(new Vector3(x, stand, 0f), 1f, Color.Magenta);

        // the idle box as drawn: scaled about node 1 like the model
        Vector3 mid = new Vector3(x, stand, 0f) + shift + s * (lo + hi) * 0.5f;
        Vector3 size = s * (hi - lo);
        float y = TileTop + 0.1f;
        Vector3 e = new Vector3(mid.X - size.X * 0.5f, y, mid.Z - size.Z * 0.5f);
        Vector3 f = new Vector3(mid.X + size.X * 0.5f, y, mid.Z - size.Z * 0.5f);
        Vector3 g = new Vector3(mid.X + size.X * 0.5f, y, mid.Z + size.Z * 0.5f);
        Vector3 h = new Vector3(mid.X - size.X * 0.5f, y, mid.Z + size.Z * 0.5f);
        Raylib.DrawLine3D(e, f, Color.SkyBlue);
        Raylib.DrawLine3D(f, g, Color.SkyBlue);
        Raylib.DrawLine3D(g, h, Color.SkyBlue);
        Raylib.DrawLine3D(h, e, Color.SkyBlue);
        Raylib.DrawLine3D(new Vector3(mid.X, y, mid.Z), new Vector3(mid.X, mid.Y + size.Y * 0.5f, mid.Z), Color.SkyBlue);
    }
#endif

    // One model stood at (x, StandY, 0) (Feet: its lowest point on the grid). On the board and
    // map that is the unit's position and the game draws with full deform (StandShift).
    public void DrawStanding(AnimatedGltfModel model, float x, float lowest, int card, float radius)
    {
        Rlgl.PushMatrix();
        if (Placement != PlaceFeet)
        {
            float s = ModelScale(radius);
            Vector3 shift = StandShift(model, s);
            Rlgl.Translatef(x + shift.X, StandY(lowest, card) + shift.Y, shift.Z);
            Rlgl.Scalef(s, s, s);
        }
        else
        {
            Rlgl.Translatef(x, StandY(lowest, card), 0f);
        }
        Rlgl.Translatef(model.Center.X, model.Center.Y, model.Center.Z);
        model.Draw();
        Rlgl.PopMatrix();
    }

}
