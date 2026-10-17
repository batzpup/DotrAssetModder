using System.Runtime.InteropServices;
using NAudio.Wave;
using Raylib_cs;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;
using MediaPacket = Sdcb.FFmpeg.Codecs.Packet;
namespace DotrAssetModder;

/* Plays one PSS in the movie window: picture and sound, Play/Pause, Stop, seek.
   The sound is the clock: the shown picture is the newest decoded one at or before the sound's
   position. A worker thread feeds the MPEG-2 decoder whole pictures from the video stream (indexed
   once by start codes) and keeps a small ring of RGBA frames ahead; a seek restarts the decoder at
   the GOP holding the target picture and drops what lies before it. Each picture's display number
   = its GOP's first number + the picture header's temporal reference, passed through the decoder
   as the packet's pts. The texture is created and updated on the draw thread only. */
public sealed class MoviePreview : IDisposable
{
    const int RingSize = 8;
    const int BytesPerPixel = 4;
    const byte SequenceHeaderCode = 0xB3;
    const byte GroupStartCode = 0xB8;
    const byte PictureStartCode = 0x00;
    const byte HighestSliceCode = 0xAF;

    sealed class PictureUnit
    {
        public int Offset;
        public int Length;
        public bool StartsGroup;
        public bool HasSequenceHeader;
        public int PictureType;            // 1 = I, 2 = P, 3 = B
        public long DisplayNumber;
    }

    sealed class DecodedFrame
    {
        public readonly byte[] Rgba = new byte[PssMovie.Width * PssMovie.Height * BytesPerPixel];
        public long DisplayNumber;
    }

    // One sound source over the whole movie, starting at a sample frame.
    sealed class SampleSource : IWaveProvider
    {
        readonly short[] samples;
        long bytePosition;
        public WaveFormat WaveFormat { get; }

        public SampleSource(short[] samples, long startSampleFrame)
        {
            this.samples = samples;
            bytePosition = startSampleFrame * PssMovie.ChannelCount * 2;
            WaveFormat = new WaveFormat(PssMovie.SampleRate, 16, PssMovie.ChannelCount);
        }

        // Silence after the end: the output never stops by itself, so its position (the clock)
        // stays valid until the preview pauses at the last picture.
        public int Read(byte[] buffer, int offset, int count)
        {
            ReadOnlySpan<byte> sampleBytes = MemoryMarshal.AsBytes(samples.AsSpan());
            int available = (int)Math.Max(0, Math.Min(count, sampleBytes.Length - bytePosition));
            sampleBytes.Slice((int)Math.Min(bytePosition, sampleBytes.Length), available).CopyTo(buffer.AsSpan(offset, available));
            buffer.AsSpan(offset + available, count - available).Clear();
            bytePosition += count;
            return count;
        }
    }

    public readonly string MovieName;
    public readonly long FrameCount;
    readonly byte[] videoStream;
    readonly short[] samples;
    readonly PictureUnit[] units;
    // Bytes before the first GOP header: the sequence header and its extension.
    readonly byte[] sequenceHeader;

    // Decoder thread state, guarded by ringLock.
    readonly object ringLock = new object();
    readonly DecodedFrame[] ring = new DecodedFrame[RingSize];
    int ringStart;
    int ringCount;
    long requestedSeekFrame = -1;
    int seekGeneration;
    bool decoderAtEnd;
    bool stopping;
    readonly Thread decoderThread;
    string? decoderError;

    // Draw thread state.
    Texture2D texture;
    bool textureLoaded;
    readonly DecodedFrame shownFrame = new DecodedFrame();
    bool shownFrameValid;
    bool textureNeedsUpdate;
    WaveOutEvent? output;
    long clockStartFrame;        // sound position 0 = this picture number
    bool playing;
    float volume = 1f;

    public bool IsPlaying => playing;

    /* 0-1 as a volume slider reads it: 1 = the movie's own level, 0.5 = sounds about half as loud.
       Kept across seeks (each seek makes a new output). */
    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0f, 1f);
            if (output != null)
            {
                output.Volume = OutputGain;
            }
        }
    }

    // Loudness follows the square of the slider (0.5 -> -12 dB), not the gain itself (0.5 -> -6 dB).
    float OutputGain => volume * volume;
    public string? Error => decoderError;

    MoviePreview(string movieName, PssMovie movie)
    {
        MovieName = movieName;
        videoStream = movie.VideoStream;
        samples = movie.Samples;
        units = IndexPictures(videoStream, out sequenceHeader);
        FrameCount = units.Length;
        for (int i = 0; i < RingSize; i++)
        {
            ring[i] = new DecodedFrame();
        }
        requestedSeekFrame = 0;
        decoderThread = new Thread(DecoderLoop) { IsBackground = true, Name = "Movie preview decoder" };
        decoderThread.Start();
    }

    // Worker thread: demuxes and indexes; the texture waits for the first Update.
    public static MoviePreview Load(string movieName, byte[] pssBytes)
    {
        PssMovie movie = PssMovie.Read(pssBytes);
        if (movie.AudioSampleRate != PssMovie.SampleRate || movie.AudioChannelCount != PssMovie.ChannelCount)
        {
            throw new InvalidDataException($"Audio is {movie.AudioSampleRate} Hz x{movie.AudioChannelCount}; the preview plays {PssMovie.SampleRate} Hz stereo.");
        }
        return new MoviePreview(movieName, movie);
    }

    public double DurationSeconds => FrameCount / (double)PssMovie.FrameRate;

    // Seconds into the movie by the sound clock.
    public double PositionSeconds
    {
        get
        {
            double seconds = clockStartFrame / (double)PssMovie.FrameRate;
            if (output != null)
            {
                seconds += output.GetPosition() / (double)(PssMovie.BytesPerSecondOfAudio);
            }
            return Math.Min(seconds, DurationSeconds);
        }
    }

    // ---- controls (draw thread) ---------------------------------------------------------------

    public void Play()
    {
        if (playing)
        {
            return;
        }
        if (PositionSeconds >= DurationSeconds - 1.0 / PssMovie.FrameRate)
        {
            Seek(0);
        }
        if (output == null)
        {
            StartOutputAt(clockStartFrame);
        }
        output!.Play();
        playing = true;
    }

    public void Pause()
    {
        if (!playing)
        {
            return;
        }
        output?.Pause();
        playing = false;
    }

    public void Stop()
    {
        Pause();
        Seek(0);
    }

    public void Seek(double seconds)
    {
        long frame = Math.Clamp((long)(seconds * PssMovie.FrameRate), 0, Math.Max(0, FrameCount - 1));
        bool wasPlaying = playing;
        StopOutput();
        clockStartFrame = frame;
        playing = false;
        lock (ringLock)
        {
            requestedSeekFrame = frame;
            seekGeneration++;
            ringCount = 0;
            Monitor.PulseAll(ringLock);
        }
        shownFrameValid = false;
        if (wasPlaying)
        {
            Play();
        }
    }

    void StartOutputAt(long frame)
    {
        output = new WaveOutEvent();
        output.Init(new SampleSource(samples, frame * (PssMovie.SampleRate / PssMovie.FrameRate)));
        output.Volume = OutputGain;
    }

    void StopOutput()
    {
        if (output == null)
        {
            return;
        }
        clockStartFrame = (long)(PositionSeconds * PssMovie.FrameRate);
        output.Stop();
        output.Dispose();
        output = null;
    }

    /* Draw thread, once per frame: takes the newest decoded picture due by the clock into the
       texture; pauses at the end. */
    public unsafe Texture2D Update()
    {
        if (!textureLoaded)
        {
            Image blankImage = Raylib.GenImageColor(PssMovie.Width, PssMovie.Height, Color.Black);
            texture = Raylib.LoadTextureFromImage(blankImage);
            Raylib.UnloadImage(blankImage);
            Raylib.SetTextureFilter(texture, TextureFilter.Bilinear);
            textureLoaded = true;
        }

        long clockFrame = (long)(PositionSeconds * PssMovie.FrameRate);
        bool reachedEnd = false;
        lock (ringLock)
        {
            if (requestedSeekFrame < 0)
            {
                while (ringCount > 0)
                {
                    DecodedFrame next = ring[ringStart];
                    // Show the first frame after a seek at once; then follow the clock.
                    if (shownFrameValid && next.DisplayNumber > clockFrame)
                    {
                        break;
                    }
                    Buffer.BlockCopy(next.Rgba, 0, shownFrame.Rgba, 0, next.Rgba.Length);
                    shownFrame.DisplayNumber = next.DisplayNumber;
                    shownFrameValid = true;
                    textureNeedsUpdate = true;
                    ringStart = (ringStart + 1) % RingSize;
                    ringCount--;
                    Monitor.PulseAll(ringLock);
                }
                reachedEnd = decoderAtEnd && ringCount == 0;
            }
        }
        if (textureNeedsUpdate)
        {
            fixed (byte* pixels = shownFrame.Rgba)
            {
                Raylib.UpdateTexture(texture, pixels);
            }
            textureNeedsUpdate = false;
        }
        if (playing && reachedEnd && PositionSeconds >= DurationSeconds - 1.0 / PssMovie.FrameRate)
        {
            Pause();
        }
        return texture;
    }

    // ---- decoding (worker thread) -------------------------------------------------------------

    void DecoderLoop()
    {
        try
        {
            DecodeUntilStopped();
        }
        catch (Exception exception)
        {
            decoderError = exception.Message;
        }
    }

    unsafe void DecodeUntilStopped()
    {
        CodecContext? decoder = null;
        using var packet = new MediaPacket();
        using var decodedFrame = new Frame();
        int unitIndex = 0;
        long dropBefore = 0;
        int generation = -1;
        bool needsSequenceHeader = false;
        bool flushed = false;
        try
        {
            while (true)
            {
                lock (ringLock)
                {
                    while (!stopping && requestedSeekFrame < 0 && (ringCount == RingSize || decoderAtEnd))
                    {
                        Monitor.Wait(ringLock);
                    }
                    if (stopping)
                    {
                        return;
                    }
                    if (requestedSeekFrame >= 0)
                    {
                        dropBefore = requestedSeekFrame;
                        unitIndex = GroupStartFor(requestedSeekFrame);
                        requestedSeekFrame = -1;
                        generation = seekGeneration;
                        decoderAtEnd = false;
                        ringCount = 0;
                        decoder?.Dispose();
                        decoder = OpenDecoder();
                        // A GOP without its own sequence header gets the stream's in front.
                        needsSequenceHeader = !units[unitIndex].HasSequenceHeader;
                        flushed = false;
                    }
                }

                if (unitIndex < units.Length)
                {
                    ReadOnlySpan<byte> prefix = ReadOnlySpan<byte>.Empty;
                    if (needsSequenceHeader)
                    {
                        prefix = sequenceHeader;
                        needsSequenceHeader = false;
                    }
                    SendUnit(decoder!, packet, prefix, units[unitIndex]);
                    unitIndex++;
                }
                else if (!flushed)
                {
                    decoder!.SendPacket(null);
                    flushed = true;
                }

                while (decoder!.ReceiveFrame(decodedFrame) == CodecResult.Success)
                {
                    long displayNumber = decodedFrame.Pts;
                    bool wanted = displayNumber >= dropBefore;
                    bool stored = wanted && StoreFrame(decodedFrame, displayNumber, generation);
                    decodedFrame.Unref();
                    if (wanted && !stored)
                    {
                        break;
                    }
                }
                if (flushed)
                {
                    lock (ringLock)
                    {
                        if (generation == seekGeneration && requestedSeekFrame < 0)
                        {
                            decoderAtEnd = true;
                        }
                    }
                }
            }
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    // One picture as one packet; prefix (the sequence header after a seek) goes in front.
    void SendUnit(CodecContext decoder, MediaPacket packet, ReadOnlySpan<byte> prefix, PictureUnit unit)
    {
        ReadOnlySpan<byte> unitBytes = videoStream.AsSpan(unit.Offset, unit.Length);
        packet.Unref();
        packet.Grow(prefix.Length + unitBytes.Length);
        Span<byte> packetBytes = packet.Data.AsSpan();
        prefix.CopyTo(packetBytes);
        unitBytes.CopyTo(packetBytes.Slice(prefix.Length));
        packet.Pts = unit.DisplayNumber;
        packet.Dts = ffmpeg.AV_NOPTS_VALUE;
        decoder.SendPacket(packet);
        packet.Unref();
    }

    static CodecContext OpenDecoder()
    {
        Codec codec = Codec.FindDecoderById(AVCodecID.Mpeg2video);
        var decoder = new CodecContext(codec);
        decoder.TimeBase = new AVRational(1, PssMovie.FrameRate);
        decoder.PktTimebase = new AVRational(1, PssMovie.FrameRate);
        decoder.ThreadCount = 0;
        decoder.ThreadType = 2;   // slice threads only, as MovieConversion (no named frame threads)
        decoder.Open(codec);
        return decoder;
    }

    // Converts into the next ring slot; waits while the ring is full. False when a seek or Dispose
    // made the frame unwanted.
    unsafe bool StoreFrame(Frame decodedFrame, long displayNumber, int generation)
    {
        DecodedFrame slot;
        lock (ringLock)
        {
            while (!stopping && ringCount == RingSize && generation == seekGeneration && requestedSeekFrame < 0)
            {
                Monitor.Wait(ringLock);
            }
            if (stopping || generation != seekGeneration || requestedSeekFrame >= 0)
            {
                return false;
            }
            slot = ring[(ringStart + ringCount) % RingSize];
        }
        ConvertToRgba(decodedFrame, slot.Rgba);
        slot.DisplayNumber = displayNumber;
        lock (ringLock)
        {
            if (generation != seekGeneration || requestedSeekFrame >= 0)
            {
                return false;
            }
            ringCount++;
        }
        return true;
    }

    // yuv420p, BT.601 limited range (all four retail movies and our encodes) -> RGBA.
    static unsafe void ConvertToRgba(Frame frame, byte[] rgba)
    {
        int width = Math.Min(frame.Width, PssMovie.Width);
        int height = Math.Min(frame.Height, PssMovie.Height);
        byte* lumaPlane = (byte*)frame.Data[0];
        byte* bluePlane = (byte*)frame.Data[1];
        byte* redPlane = (byte*)frame.Data[2];
        int lumaStride = frame.Linesize[0];
        int blueStride = frame.Linesize[1];
        int redStride = frame.Linesize[2];
        fixed (byte* output = rgba)
        {
            for (int y = 0; y < height; y++)
            {
                byte* lumaRow = lumaPlane + y * lumaStride;
                byte* blueRow = bluePlane + (y >> 1) * blueStride;
                byte* redRow = redPlane + (y >> 1) * redStride;
                byte* outputRow = output + y * PssMovie.Width * BytesPerPixel;
                for (int x = 0; x < width; x++)
                {
                    int luma = 298 * (lumaRow[x] - 16);
                    int blue = blueRow[x >> 1] - 128;
                    int red = redRow[x >> 1] - 128;
                    outputRow[x * 4] = ClampToByte((luma + 409 * red + 128) >> 8);
                    outputRow[x * 4 + 1] = ClampToByte((luma - 100 * blue - 208 * red + 128) >> 8);
                    outputRow[x * 4 + 2] = ClampToByte((luma + 516 * blue + 128) >> 8);
                    outputRow[x * 4 + 3] = 255;
                }
            }
        }
    }

    static byte ClampToByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }
        if (value > 255)
        {
            return 255;
        }
        return (byte)value;
    }

    // The GOP to start decoding at for a picture: the last one whose I picture is not after it.
    int GroupStartFor(long frame)
    {
        int chosen = 0;
        for (int i = 0; i < units.Length; i++)
        {
            if (units[i].StartsGroup && units[i].PictureType == 1 && units[i].DisplayNumber <= frame)
            {
                chosen = i;
            }
        }
        return chosen;
    }

    // ---- index --------------------------------------------------------------------------------

    /* One unit per picture: from the first header (sequence, GOP or picture) after the previous
       picture's slices up to the next such header. */
    static PictureUnit[] IndexPictures(byte[] videoStream, out byte[] sequenceHeader)
    {
        var units = new List<PictureUnit>();
        ReadOnlySpan<byte> bytes = videoStream;
        ReadOnlySpan<byte> startCodePrefix = stackalloc byte[] { 0, 0, 1 };
        PictureUnit? current = null;
        bool inSlices = false;
        int unitStart = 0;
        bool unitHasGroup = false;
        bool unitHasSequence = false;
        int firstGroupOffset = -1;
        int position = 0;
        while (true)
        {
            int found = bytes.Slice(position).IndexOf(startCodePrefix);
            if (found < 0 || position + found + 3 >= bytes.Length)
            {
                break;
            }
            int codeOffset = position + found;
            byte code = bytes[codeOffset + 3];
            bool isHeader = code == SequenceHeaderCode || code == GroupStartCode || code == PictureStartCode;
            if (isHeader && inSlices)
            {
                if (current != null)
                {
                    current.Length = codeOffset - current.Offset;
                    units.Add(current);
                }
                current = null;
                inSlices = false;
                unitStart = codeOffset;
                unitHasGroup = false;
                unitHasSequence = false;
            }
            if (code == SequenceHeaderCode)
            {
                unitHasSequence = true;
            }
            else if (code == GroupStartCode)
            {
                unitHasGroup = true;
                if (firstGroupOffset < 0)
                {
                    firstGroupOffset = codeOffset;
                }
            }
            else if (code == PictureStartCode && codeOffset + 5 < bytes.Length)
            {
                current = new PictureUnit();
                current.Offset = unitStart;
                current.StartsGroup = unitHasGroup || unitHasSequence;
                current.HasSequenceHeader = unitHasSequence;
                int temporalReference = (bytes[codeOffset + 4] << 2) | (bytes[codeOffset + 5] >> 6);
                current.PictureType = (bytes[codeOffset + 5] >> 3) & 7;
                current.DisplayNumber = temporalReference;   // made absolute below
            }
            else if (code >= 0x01 && code <= HighestSliceCode)
            {
                inSlices = true;
            }
            position = codeOffset + 3;
        }
        if (current != null)
        {
            current.Length = bytes.Length - current.Offset;
            units.Add(current);
        }

        // Absolute display numbers: each GOP shows its own pictures, numbered by temporal reference.
        long groupBase = 0;
        long picturesInGroup = 0;
        foreach (PictureUnit unit in units)
        {
            if (unit.StartsGroup)
            {
                groupBase += picturesInGroup;
                picturesInGroup = 0;
            }
            unit.DisplayNumber += groupBase;
            picturesInGroup++;
        }

        int headerEnd = firstGroupOffset;
        if (headerEnd < 0 && units.Count > 0)
        {
            headerEnd = units[0].Offset;
        }
        sequenceHeader = videoStream.AsSpan(0, Math.Max(0, headerEnd)).ToArray();
        return units.ToArray();
    }

    // ---- teardown -----------------------------------------------------------------------------

    public void Dispose()
    {
        StopOutput();
        playing = false;
        lock (ringLock)
        {
            stopping = true;
            Monitor.PulseAll(ringLock);
        }
        decoderThread.Join();
        if (textureLoaded)
        {
            Raylib.UnloadTexture(texture);
            textureLoaded = false;
        }
    }
}
