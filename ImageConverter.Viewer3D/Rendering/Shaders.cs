namespace ImageConverter.Viewer3D.Rendering;

/// <summary>
/// GLSL sources. Written in the subset shared by GLSL 1.30/1.50/3.30 (desktop) and GLSL ES 3.00; the first line
/// (<see cref="Header"/>) is chosen per context.
/// </summary>
internal static class Shaders
{
    /// <summary>Version line (and precision for ES) for the context.</summary>
    public static string Header(Gl gl)
    {
        if (gl.IsEs) return "#version 300 es\nprecision highp float;\nprecision highp int;\n";
        var v = gl.Version.Major * 10 + gl.Version.Minor;
        return v >= 33 ? "#version 330 core\n" : v >= 32 ? "#version 150\n" : "#version 130\n";
    }

    /// <summary>Model: position and the part number per vertex (normalised byte).</summary>
    public const string ModelVertex = """
        in vec3 aPos;
        in float aPart;
        uniform mat4 uViewProj;
        out vec3 vWorld;
        out float vPart;
        void main()
        {
            vWorld = aPos;
            vPart = floor(aPart * 255.0 + 0.5);
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    /// <summary>
    /// Model shading: flat normals from the screen derivatives of the position (no normals to store), lit by a key
    /// light from upper left and a weak fill from the right, both fixed to the camera, plus a sky/ground ambient that
    /// follows the model's up axis, a soft highlight and a rim; the screen-space ambient occlusion (<c>uSsao</c>)
    /// darkens the ambient part and some of the key light. Computed in linear light and converted to sRGB at the end.
    /// <c>uTint</c> overlays the selection colour.
    /// </summary>
    public const string ModelFragment = """
        in vec3 vWorld;
        in float vPart;
        uniform vec3 uEye;
        uniform vec3 uForward;
        uniform int uOrtho;
        uniform vec3 uKey;
        uniform vec3 uFill;
        uniform vec3 uBase;
        uniform int uParts;
        uniform vec4 uTint;
        uniform float uFlat;
        uniform sampler2D uSsao;
        uniform vec2 uScreen;
        uniform float uAo;
        uniform vec4 uClips[6];
        uniform int uClipCount;
        uniform int uInner;
        uniform float uExposure;
        out vec4 fragColor;

        vec3 linear(vec3 c) { return pow(c, vec3(2.2)); }

        vec3 partColour(float p)
        {
            float h = fract(p * 0.61803398875 + 0.08);
            vec3 k = clamp(abs(fract(h + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
            return mix(vec3(1.0), k, 0.42) * 0.86;
        }

        void main()
        {
            for (int i = 0; i < 6; i++) { if (i >= uClipCount) break; if (dot(vWorld, uClips[i].xyz) < uClips[i].w) discard; }
            vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));
            vec3 v = uOrtho == 1 ? -uForward : normalize(uEye - vWorld);
            if (dot(n, v) < 0.0) n = -n;
            vec3 base = linear(uParts == 1 ? partColour(vPart) : uBase);
            // Inner side (back of a face: inside the material or a cut wall) in a warm grey
            if (uInner == 1 && !gl_FrontFacing) base = mix(base, linear(vec3(0.80, 0.60, 0.50)), 0.75);
            // Occlusion fades out on faces seen nearly edge-on: there the screen-space estimate has no reliable depth
            // neighbours left, and a face turning away must not darken abruptly
            float ao = mix(1.0, pow(texture(uSsao, gl_FragCoord.xy / uScreen).r, 1.8), uAo * smoothstep(0.02, 0.15, dot(n, v)));
            float up = n.z * 0.5 + 0.5;
            vec3 ambient = mix(vec3(0.13, 0.12, 0.12), vec3(0.42, 0.45, 0.51), up) * ao;
            float key = max(dot(n, uKey), 0.0);
            float fill = max(dot(n, uFill), 0.0);
            vec3 h = normalize(uKey + v);
            float spec = pow(max(dot(n, h), 0.0), 40.0) * 0.16;
            float rim = pow(1.0 - max(dot(n, v), 0.0), 3.0) * 0.10;
            vec3 lit = base * (ambient + key * 0.95 * mix(1.0, ao, 0.65) * vec3(1.0, 0.98, 0.95) + fill * 0.25 * ao)
                       + vec3(spec + rim) * ao;
            // Blueprint: nearly flat colour, the edges carry the shape
            lit = mix(lit, base * (0.85 + 0.15 * up), uFlat);
            // Brighter lifts the mid tones along a curve, so that lit faces do not burn out to white; darker scales down
            lit = uExposure >= 1.0 ? pow(clamp(lit, 0.0, 1.0), vec3(1.0 / uExposure)) : lit * uExposure;
            lit = mix(lit, linear(uTint.rgb), uTint.a);
            fragColor = vec4(pow(clamp(lit, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
        }
        """;

    /// <summary>
    /// Depth pass for the ambient occlusion (cut away parts excluded): the depth, and the exact face normal in view
    /// space (right, up, towards the eye) packed into 0..1.
    /// </summary>
    public const string DepthFragment = """
        in vec3 vWorld;
        in float vPart;
        uniform vec4 uClips[6];
        uniform int uClipCount;
        uniform vec3 uRight;
        uniform vec3 uUp;
        uniform vec3 uBack;
        out vec4 fragColor;
        void main()
        {
            for (int i = 0; i < 6; i++) { if (i >= uClipCount) break; if (dot(vWorld, uClips[i].xyz) < uClips[i].w) discard; }
            vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));
            fragColor = vec4(vec3(dot(n, uRight), dot(n, uUp), dot(n, uBack)) * 0.5 + 0.5, 1.0);
        }
        """;

    /// <summary>
    /// Screen-space ambient occlusion: for each pixel, 16 points in the hemisphere above the surface are projected back;
    /// those behind the stored depth count as occluded, weighted down when the occluder is far in front (range
    /// check). Surfaces seen at a flat angle need two things, otherwise a wall in perspective occludes itself and turns
    /// blotchy dark as soon as the view gets flat enough (error memory #119): the exact face normal from the depth
    /// pass (one estimated from the neighbouring depths tilts into the surface where those neighbours lie far apart),
    /// and a bias that grows with the slope, because the stored depth is sampled at texel centres and differs from the
    /// depth under a sample point by up to half a texel times the slope. The pattern is turned per pixel and smoothed by the blur pass. After J. Chapman, "SSAO Tutorial"
    /// (2013), and the hemisphere variant of Crytek's method (M. Mittring, SIGGRAPH 2007).
    /// </summary>
    public const string SsaoFragment = """
        in vec2 vUv;
        uniform sampler2D uDepth;
        uniform sampler2D uNormal;
        uniform mat4 uProj;
        uniform mat4 uInvProj;
        uniform vec2 uTexel;
        uniform float uRadius;
        out vec4 fragColor;

        vec3 viewPos(vec2 uv)
        {
            float d = texture(uDepth, uv).r;
            vec4 p = uInvProj * vec4(uv * 2.0 - 1.0, d * 2.0 - 1.0, 1.0);
            return p.xyz / p.w;
        }

        void main()
        {
            float d = texture(uDepth, vUv).r;
            if (d >= 1.0) { fragColor = vec4(1.0); return; }
            vec3 p = viewPos(vUv);
            vec3 toEye = uProj[3][3] == 1.0 ? vec3(0.0, 0.0, 1.0) : normalize(-p);
            vec3 n = normalize(texture(uNormal, vUv).xyz * 2.0 - 1.0);
            if (dot(n, toEye) < 0.0) n = -n;

            // Size of one depth texel at this depth (w = 1 orthographic, −z perspective) and the slope of the surface
            float w = uProj[2][3] * p.z + uProj[3][3];
            float texel = 2.0 * w * uTexel.y / uProj[1][1];
            float facing = max(dot(n, toEye), 0.05);
            float bias = uRadius * 0.02 + texel * 0.75 * sqrt(1.0 - facing * facing) / facing;

            float a = 6.2831853 * fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
            vec3 rnd = vec3(cos(a), sin(a), 0.0);
            vec3 tang = normalize(rnd - n * dot(rnd, n) + vec3(1e-4, 0.0, 0.0));
            vec3 bit = cross(n, tang);
            float occlusion = 0.0;
            for (int i = 0; i < 16; i++)
            {
                float fi = float(i);
                float phi = 6.2831853 * fract(fi * 0.618034 + 0.1);
                float u = fract(fi * 0.381966 + 0.3);
                float st = sqrt(u);
                vec3 k = vec3(cos(phi) * st, sin(phi) * st, sqrt(1.0 - u));
                float scale = mix(0.15, 1.0, (fi + 1.0) / 16.0);
                vec3 s = p + (tang * k.x + bit * k.y + n * k.z) * uRadius * scale * scale;
                vec4 c = uProj * vec4(s, 1.0);
                vec2 suv = c.xy / c.w * 0.5 + 0.5;
                if (suv.x < 0.0 || suv.y < 0.0 || suv.x > 1.0 || suv.y > 1.0) continue;
                float sz = viewPos(suv).z;
                float range = smoothstep(0.0, 1.0, uRadius / max(abs(p.z - sz), 1e-6));
                occlusion += (sz >= s.z + bias ? 1.0 : 0.0) * range;
            }
            fragColor = vec4(1.0 - occlusion / 16.0, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>4 × 4 box blur over the same surface: removes the per-pixel turn of the occlusion pattern.</summary>
    public const string BlurFragment = """
        in vec2 vUv;
        uniform sampler2D uInput;
        uniform sampler2D uDepth;
        uniform mat4 uInvProj;
        uniform float uRadius;
        out vec4 fragColor;

        float viewZ(float d)
        {
            vec4 p = uInvProj * vec4(0.0, 0.0, d * 2.0 - 1.0, 1.0);
            return p.z / p.w;
        }

        void main()
        {
            ivec2 size = textureSize(uInput, 0);
            ivec2 at = ivec2(vUv * vec2(size));
            float z = viewZ(texelFetch(uDepth, at, 0).r);
            float sum = 0.0;
            float count = 0.0;
            for (int y = -2; y < 2; y++)
                for (int x = -2; x < 2; x++)
                {
                    ivec2 q = clamp(at + ivec2(x, y), ivec2(0), size - 1);
                    // Only neighbours on the same surface: no darkening carried across silhouettes onto what lies
                    // behind, or onto the background
                    if (abs(viewZ(texelFetch(uDepth, q, 0).r) - z) > uRadius * 0.25) continue;
                    sum += texelFetch(uInput, q, 0).r;
                    count += 1.0;
                }
            fragColor = vec4(count > 0.0 ? sum / count : 1.0, 0.0, 0.0, 1.0);
        }
        """;

    public const string LineVertex = """
        in vec3 aPos;
        uniform mat4 uViewProj;
        out vec3 vWorld;
        void main() { vWorld = aPos; gl_Position = uViewProj * vec4(aPos, 1.0); }
        """;

    public const string LineFragment = """
        in vec3 vWorld;
        uniform vec4 uColour;
        uniform vec4 uClips[6];
        uniform int uClipCount;
        out vec4 fragColor;
        void main()
        {
            for (int i = 0; i < 6; i++) { if (i >= uClipCount) break; if (dot(vWorld, uClips[i].xyz) < uClips[i].w) discard; }
            fragColor = uColour;
        }
        """;

    /// <summary>Full-screen triangle without vertex data (gl_VertexID 0, 1, 2).</summary>
    public const string BackgroundVertex = """
        out vec2 vUv;
        void main()
        {
            vec2 p = vec2(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0);
            vUv = p * 0.5 + 0.5;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    /// <summary>Vertical gradient with a faint lighter middle, so that the background does not look flat.</summary>
    public const string BackgroundFragment = """
        in vec2 vUv;
        uniform vec3 uTop;
        uniform vec3 uBottom;
        out vec4 fragColor;
        void main()
        {
            vec3 c = mix(uBottom, uTop, smoothstep(0.0, 1.0, vUv.y));
            float d = distance(vUv, vec2(0.5, 0.55));
            c *= 1.0 + 0.06 * (1.0 - smoothstep(0.0, 0.75, d));
            fragColor = vec4(c, 1.0);
        }
        """;

    public const string GroundVertex = """
        in vec3 aPos;
        in vec2 aUv;
        uniform mat4 uViewProj;
        out vec2 vUv;
        void main() { vUv = aUv; gl_Position = uViewProj * vec4(aPos, 1.0); }
        """;

    public const string GroundFragment = """
        in vec2 vUv;
        uniform sampler2D uShadow;
        uniform vec4 uColour;
        out vec4 fragColor;
        void main()
        {
            float s = texture(uShadow, vUv).r;
            fragColor = vec4(uColour.rgb, uColour.a * s);
        }
        """;
}
