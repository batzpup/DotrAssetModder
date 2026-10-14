using System.Drawing;
using System.Numerics;
namespace DotrAssetModder;

public struct GuiColour
{
    public Vector4 Value;

    public GuiColour(int red, int green, int blue, int alpha = 255)
    {
        red = Math.Clamp(red, 0, 255);
        green = Math.Clamp(green, 0, 255);
        blue = Math.Clamp(blue, 0, 255);
        alpha = Math.Clamp(alpha, 0, 255);

        Value = new Vector4(red / 255.0f, green / 255.0f, blue / 255.0f, alpha / 255.0f);

    }

    public GuiColour(float red, float green, float blue, float alpha = 1.0f)
    {
        red = Math.Clamp(red, 0.0f, 1.0f);
        green = Math.Clamp(green, 0.0f, 1.0f);
        blue = Math.Clamp(blue, 0.0f, 1.0f);
        alpha = Math.Clamp(alpha, 0.0f, 1.0f);
        Value = new Vector4(red, green, blue, alpha);
    }

    public GuiColour(Color color)
    {
      Value = new Vector4(
        Math.Clamp(color.R / 255f, 0f, 1f), 
        Math.Clamp(color.G / 255f, 0f, 1f), 
        Math.Clamp(color.B / 255f, 0f, 1f), 
        Math.Clamp(color.A / 255f, 0f, 1f));
    }

    public GuiColour(Vector4 colourVector)
    {
        colourVector.X = Math.Clamp(colourVector.X, 0.0f, 1.0f);
        colourVector.Y = Math.Clamp(colourVector.Y, 0.0f, 1.0f);
        colourVector.Z = Math.Clamp(colourVector.Z, 0.0f, 1.0f);
        colourVector.W = Math.Clamp(colourVector.W, 0.0f, 1.0f);
        Value = colourVector;
    }
}