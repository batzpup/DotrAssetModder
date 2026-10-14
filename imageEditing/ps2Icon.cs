namespace DotrAssetModder;

using SkiaSharp;
using static DotrAssetModder.Bin;

public static class Ps2Icon
{
    const int TEX_WIDTH = 128;
    const int TEX_HEIGHT = 64;
    const int TEX_BYTES = TEX_WIDTH * TEX_HEIGHT * 2;

    public static readonly int[] TextureOffsets = {
        0x62a8,
        0x14550,
        0x22be8,
    };

    public static int CurrentTextureIndex = 0;

    static bool[][] stpMasks = new bool[3][];

    static byte Expand5To8(int value5Bit) => (byte)((value5Bit << 3) | (value5Bit >> 2));

    public static SKBitmap ExtractIconTexture(byte[] iconData)
    {
        if (iconData.Length < TEX_BYTES * 2)
            throw new InvalidDataException($"Icon buffer too small: 0x{iconData.Length:X}, expected 0x{TEX_BYTES * 2:X}.");

        int topOffset = 0;
        int botOffset = TEX_BYTES;

        stpMasks[CurrentTextureIndex] = new bool[TEX_WIDTH * TEX_HEIGHT * 2];

        var bitmap = new SKBitmap(TEX_WIDTH, TEX_HEIGHT * 2, SKColorType.Bgra8888, SKAlphaType.Unpremul);

        unsafe
        {
            uint* pixels = (uint*)bitmap.GetPixels().ToPointer();

            for (int section = 0; section < 2; section++)
            {
                int dataOffset = section == 0 ? topOffset : botOffset;
                int pixelBase = section == 0 ? 0 : TEX_WIDTH * TEX_HEIGHT;

                for (int i = 0; i < TEX_WIDTH * TEX_HEIGHT; i++)
                {
                    ushort rawPixel = ReadU16(iconData, dataOffset + i * 2);

                    int red5 = (rawPixel & 0x1F);
                    int green5 = (rawPixel >> 5) & 0x1F;
                    int blue5 = (rawPixel >> 10) & 0x1F;

                    byte red8 = Expand5To8(red5);
                    byte green8 = Expand5To8(green5);
                    byte blue8 = Expand5To8(blue5);

                    bool stp = (rawPixel & 0x8000) != 0;
                    stpMasks[CurrentTextureIndex][pixelBase + i] = stp;

                    byte alpha8 = (red8 == 0 && green8 == 0 && blue8 == 0) ? (byte)0 : (byte)255;

                    pixels[pixelBase + i] =
                        (uint)(alpha8 << 24) | ((uint)red8 << 16) | ((uint)green8 << 8) | blue8;
                }
            }
        }

        return bitmap;
    }

    public static byte[] SaveIconToByte(byte[] originalData, SKBitmap bitmap)
    {
        if (bitmap.Width != TEX_WIDTH || bitmap.Height != TEX_HEIGHT * 2)
            throw new Exception($"Icon bitmap must be {TEX_WIDTH}x{TEX_HEIGHT * 2}.");

        if (originalData.Length < TEX_BYTES * 2)
            throw new Exception($"Original icon data too small: 0x{originalData.Length:X}, expected 0x{TEX_BYTES * 2:X}.");

        bool[] stpMask = stpMasks[CurrentTextureIndex] ?? new bool[TEX_WIDTH * TEX_HEIGHT * 2];

        byte[] output = (byte[])originalData.Clone();

        int topOffset = 0;
        int botOffset = TEX_BYTES;

        unsafe
        {
            uint* pixels = (uint*)bitmap.GetPixels().ToPointer();

            for (int section = 0; section < 2; section++)
            {
                int dataOffset = section == 0 ? topOffset : botOffset;
                int pixelBase = section == 0 ? 0 : TEX_WIDTH * TEX_HEIGHT;

                for (int i = 0; i < TEX_WIDTH * TEX_HEIGHT; i++)
                {
                    uint bgraPixel = pixels[pixelBase + i];

                    byte red8 = (byte)((bgraPixel >> 16) & 0xFF);
                    byte green8 = (byte)((bgraPixel >> 8) & 0xFF);
                    byte blue8 = (byte)(bgraPixel & 0xFF);

                    ushort red5 = (ushort)((red8 * 31 + 127) / 255);
                    ushort green5 = (ushort)((green8 * 31 + 127) / 255);
                    ushort blue5 = (ushort)((blue8 * 31 + 127) / 255);

                    bool stp = stpMask[pixelBase + i];

                    ushort packed = (ushort)(blue5 | (green5 << 5) | (red5 << 10) | (stp ? 0x8000 : 0));

                    int byteIndex = dataOffset + i * 2;
                    output[byteIndex] = (byte)(packed & 0xFF);
                    output[byteIndex + 1] = (byte)(packed >> 8);
                }
            }
        }

        return output;
    }
}