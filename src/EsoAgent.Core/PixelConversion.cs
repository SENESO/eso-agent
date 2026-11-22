namespace EsoAgent.Core;

/// <summary>
/// BGRA32 → NV12 conversion (BT.601, limited range — the format Media
/// Foundation's H.264 encoder accepts as MFVideoFormat_NV12). Pure and
/// allocation-lean: the caller owns the destination buffer so the capture
/// loop can reuse it across frames. Chroma is the 2×2 box average, the
/// standard NV12 subsampling. Dimensions may be odd (H.264 wants even —
/// the encoder is configured with the even-rounded size and this writer
/// clamps its reads/writes to the buffers it is given).
/// </summary>
public static class PixelConversion
{
    /// <summary>NV12 buffer size for a frame: luma plane + interleaved
    /// chroma plane at half resolution (rounded up on odd dimensions).</summary>
    public static int Nv12Size(int width, int height)
        => width * height + ((width + 1) / 2) * ((height + 1) / 2) * 2;

    /// <summary>
    /// Converts one BGRA frame into <paramref name="destination"/> (at least
    /// <see cref="Nv12Size"/> bytes). Returns the byte count written.
    /// </summary>
    public static int BgraToNv12(RawVideoFrame frame, Span<byte> destination)
    {
        var width = frame.Width;
        var height = frame.Height;
        var stride = frame.EffectiveStride;
        var src = frame.Bgra.AsSpan();
        var yPlane = destination;
        var uvPlane = destination[(width * height)..];

        for (var y = 0; y < height; y++)
        {
            var srcRow = src.Slice(y * stride, width * 4);
            var dstRow = yPlane.Slice(y * width, width);
            for (var x = 0; x < width; x++)
            {
                var px = srcRow.Slice(x * 4, 4);
                dstRow[x] = ToY(px[2], px[1], px[0]); // BGRA order: B, G, R
            }
        }

        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        for (var cy = 0; cy < chromaHeight; cy++)
        {
            var dstRow = uvPlane.Slice(cy * chromaWidth * 2, chromaWidth * 2);
            for (var cx = 0; cx < chromaWidth; cx++)
            {
                // Average the 2×2 block (clamped at the right/bottom edge).
                var rSum = 0; var gSum = 0; var bSum = 0; var n = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    var sy = Math.Min(cy * 2 + dy, height - 1);
                    var srcRow = src.Slice(sy * stride, width * 4);
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var sx = Math.Min(cx * 2 + dx, width - 1);
                        var px = srcRow.Slice(sx * 4, 4);
                        bSum += px[0]; gSum += px[1]; rSum += px[2]; n++;
                    }
                }
                dstRow[cx * 2] = ToU(rSum / n, gSum / n, bSum / n);
                dstRow[cx * 2 + 1] = ToV(rSum / n, gSum / n, bSum / n);
            }
        }
        return Nv12Size(width, height);
    }

    // BT.601 limited-range integer approximations (the same coefficients
    // libyuv uses): Y in [16,235], U/V centred on 128.
    private static byte ToY(int r, int g, int b) => (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
    private static byte ToU(int r, int g, int b) => (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
    private static byte ToV(int r, int g, int b) => (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
}
