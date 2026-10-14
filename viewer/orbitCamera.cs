using System.Numerics;
using Raylib_cs;

namespace DotrAssetModder;

// The camera the model previews share: it circles Target at Distance, turned by Yaw and Pitch.
// Left drag orbits, middle drag pans, the wheel zooms; a window adds its own moves on top.
public sealed class OrbitCamera
{
    public float Yaw;
    public float Pitch = 0.3f;
    public float Distance = 6f;
    public Vector3 Target = Vector3.Zero;
    public float Sensitivity = 0.0035f;

    public Camera3D Camera = new Camera3D {
        Position = new Vector3(0, 2, 6),
        Target = Vector3.Zero,
        Up = Vector3.UnitY,
        FovY = 60,
        Projection = CameraProjection.Perspective
    };

    // From the camera towards Target.
    public Vector3 Forward
    {
        get
        {
            float cosPitch = MathF.Cos(Pitch);
            return -new Vector3(cosPitch * MathF.Sin(Yaw), MathF.Sin(Pitch), cosPitch * MathF.Cos(Yaw));
        }
    }

    public Vector3 Right
    {
        get { return Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY)); }
    }

    public Vector3 Up
    {
        get { return Vector3.Normalize(Vector3.Cross(Right, Forward)); }
    }

    public Vector3 Position
    {
        get { return Target - Forward * Distance; }
    }

    // The wheel: a tenth of the distance a notch.
    public void Zoom(float wheel)
    {
        if (wheel != 0f)
        {
            Distance = Math.Clamp(Distance - wheel * Distance * 0.1f, 0.1f, 5000f);
        }
    }

    // Turns by a mouse move, in pixels.
    public void Turn(Vector2 delta)
    {
        Yaw -= delta.X * Sensitivity;
        Pitch = Math.Clamp(Pitch + delta.Y * Sensitivity, -1.5f, 1.5f);
    }

    // Slides Target across the view by a mouse move, in pixels.
    public void Pan(Vector2 delta)
    {
        Target += (-Right * delta.X + Up * delta.Y) * (Distance * 0.0015f);
    }

    // The mouse moves both previews share: wheel, left drag and middle drag.
    public void UpdateMouse()
    {
        Zoom(Raylib.GetMouseWheelMove());
        if (Raylib.IsMouseButtonDown(MouseButton.Left))
        {
            Turn(Raylib.GetMouseDelta());
        }
        if (Raylib.IsMouseButtonDown(MouseButton.Middle))
        {
            Pan(Raylib.GetMouseDelta());
        }
    }

    // Looks square-on at the box from left to right and bottom to top (at z 0), from far
    // enough back to see all of it, tipped down by `pitch`.
    public void Frame(float left, float right, float bottom, float top, float pitch)
    {
        Target = new Vector3((left + right) * 0.5f, (top + bottom) * 0.5f, 0f);
        Distance = MathF.Max(MathF.Max(top - bottom, right - left) * 1.3f, 0.5f);
        Yaw = 0f;
        Pitch = pitch;
    }

    // Puts Camera where Yaw, Pitch, Distance and Target say.
    public void Apply()
    {
        Camera.Position = Position;
        Camera.Target = Target;
    }
}
