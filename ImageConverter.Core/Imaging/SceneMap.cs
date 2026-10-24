namespace ImageConverter.Core.Imaging;

/// <summary>
/// Scene map: where the sky, the halo of a light source (sun) and the landscape lie. It is determined once at the start
/// from the image, on the small working copy (by default at most 1024 px), with soft transitions. All steps that protect
/// the sky or the sun read this single map instead of computing their own rules.
/// </summary>
/// <remarks>
/// Encoding: 0 landscape, 1 sky, values above 1 halo (1 + halo strength). From this,
/// <see cref="Land"/> = 1 − min(1, sky) and <see cref="Halo"/> = min(1, max(0, sky − 1)).
/// </remarks>
internal sealed class SceneMap
{
    /// <summary>Sky value per cell of the small copy (0 … 2, see the remarks).</summary>
    public float[] Sky { get; }
    public int W { get; }
    public int H { get; }

    /// <summary>One cell of the map covers <see cref="Factor"/> × <see cref="Factor"/> image pixels.</summary>
    public int Factor { get; }

    private SceneMap(float[] sky, int w, int h, int factor) => (Sky, W, H, Factor) = (sky, w, h, factor);

    /// <summary>Builds the map from the small working copy.</summary>
    /// <param name="small">Small copy in linear light (B, G, R per cell).</param>
    /// <param name="w">Width of the small copy in cells.</param>
    /// <param name="h">Height of the small copy in cells.</param>
    /// <param name="factor">Downscaling factor from the image to the small copy.</param>
    public static SceneMap Of(float[] small, int w, int h, int factor) => new(Dehaze.SkyMap(small, w, h), w, h, factor);

    /// <summary>Sky value (0 … 2) at an image pixel, bilinearly interpolated from the map.</summary>
    public float SkyAt(int x, int y)
    {
        var sx = Math.Clamp((x + 0.5f) / Factor - 0.5f, 0, W - 1);
        var sy = Math.Clamp((y + 0.5f) / Factor - 0.5f, 0, H - 1);
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
        float fx = sx - x0, fy = sy - y0;
        var top = Sky[y0 * W + x0] * (1 - fx) + Sky[y0 * W + x1] * fx;
        var bottom = Sky[y1 * W + x0] * (1 - fx) + Sky[y1 * W + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>1 = landscape, 0 = sky or halo.</summary>
    public static float Land(float sky) => 1 - Math.Clamp(sky, 0, 1);

    /// <summary>1 = in the centre of the halo of a light source.</summary>
    public static float Halo(float sky) => Math.Clamp(sky - 1, 0, 1);
}
