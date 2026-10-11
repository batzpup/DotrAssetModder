namespace DotrAssetModder;
// Mostly AI

// The options tools/toglb.py takes on the command line (DotR-PC), with its defaults.
public sealed class ConvertOptions
{
    // Factor on the source's size in metres (one game unit is one metre).
    public double Scale = 1.0;
    // Extra turn in degrees (X first), if the model is not upright facing +Z.
    public double RotateX;
    public double RotateY;
    public double RotateZ;
    // Moves the whole model off node 0, in game units (+Y up, +Z front); node 0 stays put.
    public double TranslateX;
    public double TranslateY;
    public double TranslateZ;
    // Node 0 is the source's own origin. Where node 1 (what the game scales and turns the model
    // about, and the camera aims at) sits, in game units from node 0; Move does not change it.
    // The body stays put around node 0.
    public double[] PivotPoint = new double[3];
    // Bind at the first animation's frame 0 instead of the skin's bind pose.
    public bool RestFromAnim;
    // The source's UVs are already top-left (Unity rips; found by itself for FBX).
    public bool FlipUv;
    // Comma-separated animation names to keep, in this order; name=new renames.
    public string? Anims;
    // Files of the same model to take animations from, matched by node name (3DS rips).
    public List<string> AnimsFrom = new List<string>();
}
