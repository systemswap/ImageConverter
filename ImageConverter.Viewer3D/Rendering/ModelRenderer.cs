using System.Numerics;

namespace ImageConverter.Viewer3D.Rendering;

/// <summary>How the model is drawn.</summary>
public enum RenderMode
{
    /// <summary>Shaded surfaces with soft shadows and the sharp edges.</summary>
    Solid,

    /// <summary>All triangle edges, also the hidden ones.</summary>
    Wireframe,

    /// <summary>Blue background, flat surfaces, white edges.</summary>
    Blueprint,
}

/// <summary>A run of the face-sorted triangles drawn again in a colour (selection, hover).</summary>
/// <param name="FirstTriangle">First triangle of the run.</param>
/// <param name="Triangles">Number of triangles.</param>
/// <param name="Colour">sRGB colour; alpha = how strongly it covers the shading.</param>
public readonly record struct Highlight(int FirstTriangle, int Triangles, Vector4 Colour);

/// <summary>Everything one frame needs besides the buffers.</summary>
internal sealed record FrameState(
    Matrix4x4 ViewProjection, Matrix4x4 Projection, float OcclusionRadius,
    Vector3 Eye, Vector3 Forward, Vector3 Right, Vector3 Up, bool Orthographic,
    RenderMode Mode, bool Edges, bool Parts, bool Dark,
    IReadOnlyList<Highlight> Highlights, Vector4? Clip = null);

/// <summary>
/// CPU side of what is drawn: owned by the UI thread, filled by the background steps. Each part carries a version, so
/// that the renderer uploads only what changed; large arrays that only the GPU needs are produced on demand and not
/// kept.
/// </summary>
internal sealed class ModelScene
{
    public required float[] Positions { get; init; }
    public required int TriangleCount { get; init; }

    /// <summary>Triangle indices in file order (until the faces are known).</summary>
    public required int[] FileIndices { get; init; }

    /// <summary>Face-sorted indices, produced when needed (after the faces are known); null before.</summary>
    public Func<int[]>? SortedIndices { get; set; }

    public int IndicesVersion { get; set; }

    /// <summary>Part number per vertex; null until the parts are known.</summary>
    public byte[]? Parts { get; set; }

    public int ExtrasVersion { get; set; }

    public int[]? SharpEdges { get; set; }

    public int SharpEdgesVersion { get; set; }

    /// <summary>All edges, computed in the background when the wireframe is first shown; null before.</summary>
    public int[]? AllEdges { get; set; }

    public GroundShadow? Shadow { get; set; }

    public int ShadowVersion { get; set; }
}

/// <summary>
/// Draws a <see cref="ModelScene"/> with OpenGL: background gradient, soft ground shadow, shaded model, selected faces
/// and edge lines, into a multisampled framebuffer that is resolved into the control's one (smooth edges).
/// </summary>
/// <remarks>
/// <para>The solid view takes four passes: the depth of the model at half resolution, the screen-space ambient
/// occlusion from it, a blur, then the model itself reading the occlusion – so corners, holes and the foot of a boss on
/// a plate get their soft shade at any mesh size, without work on the CPU.</para>
/// <para>All methods run on the thread of the GL context (the UI thread in Avalonia).</para>
/// </remarks>
internal sealed class ModelRenderer
{
    private Gl _gl = null!;
    private int _modelProgram, _lineProgram, _backgroundProgram, _groundProgram, _depthProgram, _ssaoProgram, _blurProgram;
    private int _modelVao, _lineVao, _wireVao, _groundVao, _emptyVao;
    private int _posBuffer, _extraBuffer, _triBuffer, _edgeBuffer, _wireBuffer, _groundBuffer, _shadowTexture;
    private int _edgeCount, _wireCount;
    private int _uploadedIndices = -1, _uploadedExtras = -1, _uploadedEdges = -1, _uploadedShadow = -1;
    private bool _positionsUploaded, _wireUploaded, _hasShadow;
    private int _fbo, _colourRb, _depthRb, _fboWidth, _fboHeight, _samples;

    // Ambient occlusion at half resolution: depth (+ an unused colour target), raw result, blurred result
    private int _aoDepthFbo, _aoDepthTex, _aoDummyTex, _aoRawFbo, _aoRawTex, _aoFbo, _aoTex, _whiteTex, _aoWidth, _aoHeight;
    private bool _aoBroken;

    // Uniform locations
    private int _mViewProj, _mEye, _mForward, _mOrtho, _mKey, _mFill, _mBase, _mParts, _mTint, _mAo, _mFlat, _mSsao, _mScreen;
    private int _dViewProj, _sDepth, _sProj, _sInvProj, _sTexel, _sRadius, _blInput;
    private int _mClip, _mClipOn, _mInner, _dClip, _dClipOn, _lClip, _lClipOn;
    private int _lViewProj, _lColour, _bTop, _bBottom, _gViewProj, _gShadow, _gColour;

    /// <summary>Why drawing is not possible (missing OpenGL features), or null.</summary>
    public string? Failure { get; private set; }

    public void Init(Gl gl)
    {
        _gl = gl;
        try
        {
            var head = Shaders.Header(gl);
            _modelProgram = gl.Program(head + Shaders.ModelVertex, head + Shaders.ModelFragment, "aPos", "aPart");
            _depthProgram = gl.Program(head + Shaders.ModelVertex, head + Shaders.DepthFragment, "aPos", "aPart");
            _ssaoProgram = gl.Program(head + Shaders.BackgroundVertex, head + Shaders.SsaoFragment);
            _blurProgram = gl.Program(head + Shaders.BackgroundVertex, head + Shaders.BlurFragment);
            _lineProgram = gl.Program(head + Shaders.LineVertex, head + Shaders.LineFragment, "aPos");
            _backgroundProgram = gl.Program(head + Shaders.BackgroundVertex, head + Shaders.BackgroundFragment);
            _groundProgram = gl.Program(head + Shaders.GroundVertex, head + Shaders.GroundFragment, "aPos", "aUv");
        }
        catch (InvalidOperationException e)
        {
            Failure = e.Message;
            AppLog.Write("3D: " + e.Message);
            return;
        }

        _mViewProj = gl.Uniform(_modelProgram, "uViewProj");
        _mEye = gl.Uniform(_modelProgram, "uEye");
        _mForward = gl.Uniform(_modelProgram, "uForward");
        _mOrtho = gl.Uniform(_modelProgram, "uOrtho");
        _mKey = gl.Uniform(_modelProgram, "uKey");
        _mFill = gl.Uniform(_modelProgram, "uFill");
        _mBase = gl.Uniform(_modelProgram, "uBase");
        _mParts = gl.Uniform(_modelProgram, "uParts");
        _mTint = gl.Uniform(_modelProgram, "uTint");
        _mAo = gl.Uniform(_modelProgram, "uAo");
        _mFlat = gl.Uniform(_modelProgram, "uFlat");
        _mSsao = gl.Uniform(_modelProgram, "uSsao");
        _mScreen = gl.Uniform(_modelProgram, "uScreen");
        _dViewProj = gl.Uniform(_depthProgram, "uViewProj");
        _mClip = gl.Uniform(_modelProgram, "uClip");
        _mClipOn = gl.Uniform(_modelProgram, "uClipOn");
        _mInner = gl.Uniform(_modelProgram, "uInner");
        _dClip = gl.Uniform(_depthProgram, "uClip");
        _dClipOn = gl.Uniform(_depthProgram, "uClipOn");
        _lClip = gl.Uniform(_lineProgram, "uClip");
        _lClipOn = gl.Uniform(_lineProgram, "uClipOn");
        _sDepth = gl.Uniform(_ssaoProgram, "uDepth");
        _sProj = gl.Uniform(_ssaoProgram, "uProj");
        _sInvProj = gl.Uniform(_ssaoProgram, "uInvProj");
        _sTexel = gl.Uniform(_ssaoProgram, "uTexel");
        _sRadius = gl.Uniform(_ssaoProgram, "uRadius");
        _blInput = gl.Uniform(_blurProgram, "uInput");
        _lViewProj = gl.Uniform(_lineProgram, "uViewProj");
        _lColour = gl.Uniform(_lineProgram, "uColour");
        _bTop = gl.Uniform(_backgroundProgram, "uTop");
        _bBottom = gl.Uniform(_backgroundProgram, "uBottom");
        _gViewProj = gl.Uniform(_groundProgram, "uViewProj");
        _gShadow = gl.Uniform(_groundProgram, "uShadow");
        _gColour = gl.Uniform(_groundProgram, "uColour");

        _modelVao = gl.GenVertexArray();
        _lineVao = gl.GenVertexArray();
        _wireVao = gl.GenVertexArray();
        _groundVao = gl.GenVertexArray();
        _emptyVao = gl.GenVertexArray();
        _posBuffer = gl.GenBuffer();
        _extraBuffer = gl.GenBuffer();
        _triBuffer = gl.GenBuffer();
        _edgeBuffer = gl.GenBuffer();
        _wireBuffer = gl.GenBuffer();
        _groundBuffer = gl.GenBuffer();
        _shadowTexture = gl.GenTexture();
        _samples = Math.Clamp(gl.GetInteger(Gl.MaxSamples), 0, 4);

        // White 1 × 1 texture: "no occlusion" where the occlusion pass is not used
        _whiteTex = gl.GenTexture();
        gl.BindTexture(Gl.Texture2D, _whiteTex);
        gl.PixelStore(Gl.UnpackAlignment, 1);
        gl.TexImageR8(1, 1, [255]);
        SetFiltering(Gl.Nearest);
    }

    public void Deinit()
    {
        if (_gl is not { } gl) return;
        gl.DeleteProgram(_modelProgram);
        gl.DeleteProgram(_lineProgram);
        gl.DeleteProgram(_backgroundProgram);
        gl.DeleteProgram(_groundProgram);
        gl.DeleteProgram(_depthProgram);
        gl.DeleteProgram(_ssaoProgram);
        gl.DeleteProgram(_blurProgram);
        gl.DeleteTexture(_whiteTex);
        DeleteOcclusionTargets();
        foreach (var vao in new[] { _modelVao, _lineVao, _wireVao, _groundVao, _emptyVao }) gl.DeleteVertexArray(vao);
        foreach (var b in new[] { _posBuffer, _extraBuffer, _triBuffer, _edgeBuffer, _wireBuffer, _groundBuffer }) gl.DeleteBuffer(b);
        gl.DeleteTexture(_shadowTexture);
        DeleteFramebuffer();
        _positionsUploaded = _wireUploaded = _hasShadow = false;
        _uploadedIndices = _uploadedExtras = _uploadedEdges = _uploadedShadow = -1;
    }

    /// <summary>Approximate GPU memory of the model buffers in bytes.</summary>
    public long GpuBytes { get; private set; }

    /// <summary>Draws a frame into <paramref name="target"/>.</summary>
    /// <param name="scene">What to draw.</param>
    /// <param name="frame">Camera, mode and highlights.</param>
    /// <param name="target">Framebuffer of the control.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    public void Render(ModelScene scene, FrameState frame, int target, int width, int height)
    {
        var gl = _gl;
        if (Failure is not null || width <= 0 || height <= 0) return;
        while (gl.GetError() != 0) { }   // errors of others before us
        Upload(scene, frame.Mode == RenderMode.Wireframe);
        if (gl.GetError() == Gl.OutOfMemory)
        {
            // A huge model on a graphics card with little memory: say so instead of drawing garbage
            Failure = Tr.T("Die Grafikkarte hat nicht genug Speicher für dieses Modell.");
            AppLog.Write("3D: out of graphics memory", $"{scene.TriangleCount} triangles");
            return;
        }

        var own = EnsureFramebuffer(width, height);
        gl.BindFramebuffer(Gl.Framebuffer, own ? _fbo : target);
        gl.Viewport(0, 0, width, height);
        gl.Disable(Gl.ScissorTest);
        gl.ColorMask(true);
        gl.DepthMask(true);
        gl.ClearColor(0, 0, 0, 1);
        gl.ClearDepth();
        gl.Clear(Gl.ColorBufferBit | Gl.DepthBufferBit);

        var occlusion = frame.Mode == RenderMode.Solid && Occlusion(scene, frame, width, height);
        gl.BindFramebuffer(Gl.Framebuffer, own ? _fbo : target);
        gl.Viewport(0, 0, width, height);

        DrawBackground(frame);
        // With a section the shadow would show parts that are cut away
        if (frame.Mode == RenderMode.Solid && frame.Clip is null) DrawGround(frame);
        if (frame.Mode != RenderMode.Wireframe) DrawModel(scene, frame, occlusion, width, height);
        DrawLines(frame);

        if (own)
        {
            gl.BindFramebuffer(Gl.ReadFramebuffer, _fbo);
            gl.BindFramebuffer(Gl.DrawFramebuffer, target);
            gl.BlitFramebuffer(width, height);
            if (gl.GetError() != 0)
            {
                // The control's framebuffer cannot take the resolve (format differs): draw straight into it from now on
                AppLog.Write("3D: resolving into the control failed, drawing without smoothing");
                DeleteFramebuffer();
                _samples = -1;
                gl.BindFramebuffer(Gl.Framebuffer, target);
            }
        }
        gl.BindFramebuffer(Gl.Framebuffer, target);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    /// <summary>Reads the last frame from the control's framebuffer (RGBA, top row first); for checks of the drawing.</summary>
    public byte[] ReadBack(int target, int width, int height)
    {
        var gl = _gl;
        gl.BindFramebuffer(Gl.Framebuffer, target);
        gl.PixelStore(Gl.PackAlignment, 1);
        var rows = new byte[width * height * 4];
        gl.ReadPixels(width, height, rows);
        var flipped = new byte[rows.Length];
        for (var y = 0; y < height; y++)
            Array.Copy(rows, (height - 1 - y) * width * 4, flipped, y * width * 4, width * 4);
        return flipped;
    }

    private void Upload(ModelScene scene, bool wire)
    {
        var gl = _gl;
        if (!_positionsUploaded)
        {
            gl.BindVertexArray(_modelVao);
            gl.BindBuffer(Gl.ArrayBuffer, _posBuffer);
            gl.BufferData<float>(Gl.ArrayBuffer, scene.Positions, Gl.StaticDraw);
            gl.VertexAttribPointer(0, 3, Gl.Float, false, 12, 0);
            gl.EnableVertexAttribArray(0);
            foreach (var vao in new[] { _lineVao, _wireVao })
            {
                gl.BindVertexArray(vao);
                gl.BindBuffer(Gl.ArrayBuffer, _posBuffer);
                gl.VertexAttribPointer(0, 3, Gl.Float, false, 12, 0);
                gl.EnableVertexAttribArray(0);
            }
            _positionsUploaded = true;
        }

        if (_uploadedIndices != scene.IndicesVersion)
        {
            gl.BindVertexArray(_modelVao);
            gl.BindBuffer(Gl.ElementArrayBuffer, _triBuffer);
            var indices = scene.SortedIndices?.Invoke() ?? scene.FileIndices;
            gl.BufferData<int>(Gl.ElementArrayBuffer, indices, Gl.StaticDraw);
            _uploadedIndices = scene.IndicesVersion;
        }

        if (_uploadedExtras != scene.ExtrasVersion)
        {
            var parts = scene.Parts ?? new byte[scene.Positions.Length / 3];
            gl.BindVertexArray(_modelVao);
            gl.BindBuffer(Gl.ArrayBuffer, _extraBuffer);
            gl.BufferData<byte>(Gl.ArrayBuffer, parts, Gl.StaticDraw);
            gl.VertexAttribPointer(1, 1, Gl.UnsignedByte, true, 1, 0);
            gl.EnableVertexAttribArray(1);
            _uploadedExtras = scene.ExtrasVersion;
        }

        if (_uploadedEdges != scene.SharpEdgesVersion && scene.SharpEdges is { } edges)
        {
            gl.BindVertexArray(_lineVao);
            gl.BindBuffer(Gl.ElementArrayBuffer, _edgeBuffer);
            gl.BufferData<int>(Gl.ElementArrayBuffer, edges, Gl.StaticDraw);
            _edgeCount = edges.Length;
            _uploadedEdges = scene.SharpEdgesVersion;
        }

        if (wire && !_wireUploaded && scene.AllEdges is { } lines)
        {
            gl.BindVertexArray(_wireVao);
            gl.BindBuffer(Gl.ElementArrayBuffer, _wireBuffer);
            gl.BufferData<int>(Gl.ElementArrayBuffer, lines, Gl.StaticDraw);
            _wireCount = lines.Length;
            _wireUploaded = true;
        }

        if (_uploadedShadow != scene.ShadowVersion && scene.Shadow is { } s)
        {
            Span<float> quad =
            [
                s.Min.X, s.Min.Y, s.Z, 0, 0,
                s.Max.X, s.Min.Y, s.Z, 1, 0,
                s.Max.X, s.Max.Y, s.Z, 1, 1,
                s.Min.X, s.Min.Y, s.Z, 0, 0,
                s.Max.X, s.Max.Y, s.Z, 1, 1,
                s.Min.X, s.Max.Y, s.Z, 0, 1,
            ];
            gl.BindVertexArray(_groundVao);
            gl.BindBuffer(Gl.ArrayBuffer, _groundBuffer);
            gl.BufferData<float>(Gl.ArrayBuffer, quad, Gl.StaticDraw);
            gl.VertexAttribPointer(0, 3, Gl.Float, false, 20, 0);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(1, 2, Gl.Float, false, 20, 12);
            gl.EnableVertexAttribArray(1);
            gl.BindTexture(Gl.Texture2D, _shadowTexture);
            gl.PixelStore(Gl.UnpackAlignment, 1);
            gl.TexImageR8(s.Size, s.Size, s.Cells);
            SetFiltering(Gl.Linear);
            _uploadedShadow = scene.ShadowVersion;
            _hasShadow = true;
        }
        gl.BindVertexArray(0);
        GpuBytes = scene.Positions.Length * 4L + scene.TriangleCount * 12L + scene.Positions.Length / 3
                   + _edgeCount * 4L + _wireCount * 4L;
    }

    // Colours (sRGB 0 … 1)
    private static readonly Vector3 LightTop = Rgb(0xF4F6F9), LightBottom = Rgb(0xD5DAE1);
    private static readonly Vector3 DarkTop = Rgb(0x34373C), DarkBottom = Rgb(0x1E1F22);
    private static readonly Vector3 BlueTop = Rgb(0x2A64A8), BlueBottom = Rgb(0x173E73);
    private static readonly Vector3 ModelLight = Rgb(0xC9CED6), ModelDark = Rgb(0xB9BEC6), ModelBlue = Rgb(0x2F6DB5);

    private static Vector3 Rgb(int hex) => new(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f);

    private void DrawBackground(FrameState f)
    {
        var gl = _gl;
        gl.Disable(Gl.DepthTest);
        gl.DepthMask(false);
        gl.UseProgram(_backgroundProgram);
        var (top, bottom) = f.Mode == RenderMode.Blueprint ? (BlueTop, BlueBottom) : f.Dark ? (DarkTop, DarkBottom) : (LightTop, LightBottom);
        gl.Uniform3(_bTop, top);
        gl.Uniform3(_bBottom, bottom);
        gl.BindVertexArray(_emptyVao);
        gl.DrawArrays(Gl.Triangles, 0, 3);
        gl.DepthMask(true);
    }

    private void DrawGround(FrameState f)
    {
        if (!_hasShadow) return;
        // Looking from below, the ground would cover the model: fade it out towards the horizon
        var fromAbove = Math.Clamp(-f.Forward.Z * 6, 0, 1);
        if (fromAbove <= 0) return;
        var gl = _gl;
        gl.Enable(Gl.DepthTest);
        gl.DepthFunc(Gl.LessEqual);
        gl.DepthMask(false);
        gl.Enable(Gl.Blend);
        gl.BlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
        gl.UseProgram(_groundProgram);
        gl.UniformMatrix(_gViewProj, f.ViewProjection);
        gl.ActiveTexture(Gl.Texture0);
        gl.BindTexture(Gl.Texture2D, _shadowTexture);
        gl.Uniform1(_gShadow, 0);
        gl.Uniform4(_gColour, f.Dark ? new Vector4(0, 0, 0, 0.8f * fromAbove) : new Vector4(0.10f, 0.12f, 0.16f, 0.62f * fromAbove));
        gl.BindVertexArray(_groundVao);
        gl.DrawArrays(Gl.Triangles, 0, 6);
        gl.Disable(Gl.Blend);
        gl.DepthMask(true);
    }

    private void DrawModel(ModelScene scene, FrameState f, bool occlusion, int width, int height)
    {
        var gl = _gl;
        gl.Enable(Gl.DepthTest);
        gl.DepthFunc(Gl.LessEqual);
        // Push the surfaces back a little, so that edge lines on them are not swallowed
        gl.Enable(Gl.PolygonOffsetFill);
        gl.PolygonOffset(1, 1);
        gl.UseProgram(_modelProgram);
        gl.UniformMatrix(_mViewProj, f.ViewProjection);
        gl.Uniform3(_mEye, f.Eye);
        gl.Uniform3(_mForward, f.Forward);
        gl.Uniform1(_mOrtho, f.Orthographic ? 1 : 0);
        // Lights fixed to the camera: key from upper left in front, weak fill from the right
        var key = Vector3.Normalize(-0.45f * f.Right + 0.65f * f.Up - 0.62f * f.Forward);
        var fill = Vector3.Normalize(0.7f * f.Right - 0.1f * f.Up - 0.5f * f.Forward);
        gl.Uniform3(_mKey, key);
        gl.Uniform3(_mFill, fill);
        var blueprint = f.Mode == RenderMode.Blueprint;
        gl.Uniform3(_mBase, blueprint ? ModelBlue : f.Dark ? ModelDark : ModelLight);
        gl.Uniform1(_mParts, f.Parts && !blueprint ? 1 : 0);
        gl.Uniform1(_mAo, occlusion ? 1f : 0f);
        gl.ActiveTexture(Gl.Texture0);
        gl.BindTexture(Gl.Texture2D, occlusion ? _aoTex : _whiteTex);
        gl.Uniform1(_mSsao, 0);
        gl.Uniform2(_mScreen, width, height);
        gl.Uniform1(_mFlat, blueprint ? 1f : 0f);
        gl.Uniform1(_mInner, blueprint ? 0 : 1);
        SetClip(_mClip, _mClipOn, f);
        gl.Uniform4(_mTint, Vector4.Zero);
        gl.BindVertexArray(_modelVao);
        gl.DrawElements(Gl.Triangles, 3 * scene.TriangleCount, 0);

        foreach (var h in f.Highlights)
        {
            if (h.Triangles <= 0) continue;
            gl.Uniform4(_mTint, h.Colour);
            gl.DrawElements(Gl.Triangles, 3 * h.Triangles, 3L * h.FirstTriangle);
        }
        gl.Disable(Gl.PolygonOffsetFill);
    }

    private void DrawLines(FrameState f)
    {
        var gl = _gl;
        gl.UseProgram(_lineProgram);
        gl.UniformMatrix(_lViewProj, f.ViewProjection);
        SetClip(_lClip, _lClipOn, f);
        gl.Enable(Gl.Blend);
        gl.BlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
        if (f.Mode == RenderMode.Wireframe)
        {
            gl.Disable(Gl.DepthTest);
            gl.Uniform4(_lColour, f.Dark ? new Vector4(0.85f, 0.88f, 0.92f, 0.55f) : new Vector4(0.12f, 0.16f, 0.24f, 0.55f));
            gl.BindVertexArray(_wireVao);
            gl.DrawElements(Gl.Lines, _wireCount, 0);
        }
        else if (f.Edges || f.Mode == RenderMode.Blueprint)
        {
            gl.Enable(Gl.DepthTest);
            gl.DepthFunc(Gl.LessEqual);
            gl.DepthMask(false);
            var colour = f.Mode == RenderMode.Blueprint ? new Vector4(1, 1, 1, 0.9f)
                : f.Dark ? new Vector4(0.05f, 0.05f, 0.06f, 0.55f) : new Vector4(0.15f, 0.17f, 0.21f, 0.45f);
            gl.Uniform4(_lColour, colour);
            gl.BindVertexArray(_lineVao);
            gl.DrawElements(Gl.Lines, _edgeCount, 0);
            gl.DepthMask(true);
        }
        gl.Disable(Gl.Blend);
    }

    private void SetClip(int plane, int on, FrameState f)
    {
        _gl.Uniform1(on, f.Clip is null ? 0 : 1);
        if (f.Clip is { } c) _gl.Uniform4(plane, c);
    }

    /// <summary>Depth at half resolution, occlusion from it, blurred into <see cref="_aoTex"/>; false if not available.</summary>
    private bool Occlusion(ModelScene scene, FrameState f, int width, int height)
    {
        if (_aoBroken || !EnsureOcclusionTargets(Math.Max(1, width / 2), Math.Max(1, height / 2))) return false;
        var gl = _gl;
        gl.Viewport(0, 0, _aoWidth, _aoHeight);

        gl.BindFramebuffer(Gl.Framebuffer, _aoDepthFbo);
        gl.DepthMask(true);
        gl.ClearDepth();
        gl.Clear(Gl.DepthBufferBit | Gl.ColorBufferBit);
        gl.Enable(Gl.DepthTest);
        gl.DepthFunc(Gl.LessEqual);
        gl.UseProgram(_depthProgram);
        gl.UniformMatrix(_dViewProj, f.ViewProjection);
        SetClip(_dClip, _dClipOn, f);
        gl.BindVertexArray(_modelVao);
        gl.DrawElements(Gl.Triangles, 3 * scene.TriangleCount, 0);

        gl.Disable(Gl.DepthTest);
        gl.DepthMask(false);
        gl.BindVertexArray(_emptyVao);
        gl.BindFramebuffer(Gl.Framebuffer, _aoRawFbo);
        gl.UseProgram(_ssaoProgram);
        gl.ActiveTexture(Gl.Texture0);
        gl.BindTexture(Gl.Texture2D, _aoDepthTex);
        gl.Uniform1(_sDepth, 0);
        gl.UniformMatrix(_sProj, f.Projection);
        Matrix4x4.Invert(f.Projection, out var inverse);
        gl.UniformMatrix(_sInvProj, inverse);
        gl.Uniform2(_sTexel, 1f / _aoWidth, 1f / _aoHeight);
        gl.Uniform1(_sRadius, f.OcclusionRadius);
        gl.DrawArrays(Gl.Triangles, 0, 3);

        gl.BindFramebuffer(Gl.Framebuffer, _aoFbo);
        gl.UseProgram(_blurProgram);
        gl.BindTexture(Gl.Texture2D, _aoRawTex);
        gl.Uniform1(_blInput, 0);
        gl.DrawArrays(Gl.Triangles, 0, 3);
        gl.DepthMask(true);
        return true;
    }

    private bool EnsureOcclusionTargets(int w, int h)
    {
        if (_aoFbo != 0 && _aoWidth == w && _aoHeight == h) return true;
        var gl = _gl;
        DeleteOcclusionTargets();
        _aoDepthTex = Texture(depth: true);
        _aoDummyTex = Texture(depth: false);
        _aoRawTex = Texture(depth: false);
        _aoTex = Texture(depth: false);
        _aoDepthFbo = Target(_aoDummyTex, _aoDepthTex);
        _aoRawFbo = Target(_aoRawTex, 0);
        _aoFbo = Target(_aoTex, 0);
        if (_aoDepthFbo == 0 || _aoRawFbo == 0 || _aoFbo == 0)
        {
            AppLog.Write("3D: occlusion targets incomplete, drawing without ambient occlusion");
            DeleteOcclusionTargets();
            _aoBroken = true;
            return false;
        }
        _aoWidth = w;
        _aoHeight = h;
        return true;

        int Texture(bool depth)
        {
            var t = gl.GenTexture();
            gl.BindTexture(Gl.Texture2D, t);
            if (depth) gl.TexImageDepth(w, h);
            else gl.TexImageR8(w, h, default);
            SetFiltering(depth ? Gl.Nearest : Gl.Linear);
            return t;
        }

        int Target(int colour, int depthTexture)
        {
            var fbo = gl.GenFramebuffer();
            gl.BindFramebuffer(Gl.Framebuffer, fbo);
            gl.FramebufferTexture(Gl.ColorAttachment0, colour);
            if (depthTexture != 0) gl.FramebufferTexture(Gl.DepthAttachment, depthTexture);
            if (gl.CheckFramebufferStatus(Gl.Framebuffer) == Gl.FramebufferComplete) return fbo;
            gl.DeleteFramebuffer(fbo);
            return 0;
        }
    }

    private void DeleteOcclusionTargets()
    {
        var gl = _gl;
        foreach (var f in new[] { _aoDepthFbo, _aoRawFbo, _aoFbo }) gl.DeleteFramebuffer(f);
        foreach (var t in new[] { _aoDepthTex, _aoDummyTex, _aoRawTex, _aoTex }) gl.DeleteTexture(t);
        _aoDepthFbo = _aoRawFbo = _aoFbo = _aoDepthTex = _aoDummyTex = _aoRawTex = _aoTex = 0;
        _aoWidth = _aoHeight = 0;
    }

    private void SetFiltering(int filter)
    {
        var gl = _gl;
        gl.TexParameter(Gl.Texture2D, Gl.TextureMinFilter, filter);
        gl.TexParameter(Gl.Texture2D, Gl.TextureMagFilter, filter);
        gl.TexParameter(Gl.Texture2D, Gl.TextureWrapS, Gl.ClampToEdge);
        gl.TexParameter(Gl.Texture2D, Gl.TextureWrapT, Gl.ClampToEdge);
    }

    /// <summary>Own multisampled framebuffer of the control's size; false = draw directly (no smoothing available).</summary>
    private bool EnsureFramebuffer(int width, int height)
    {
        if (_samples < 0) return false;
        if (_fbo != 0 && _fboWidth == width && _fboHeight == height) return true;
        var gl = _gl;
        DeleteFramebuffer();
        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(Gl.Framebuffer, _fbo);
        _colourRb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(_colourRb);
        gl.RenderbufferStorageMultisample(_samples, Gl.Rgba8, width, height);
        gl.FramebufferRenderbuffer(Gl.ColorAttachment0, _colourRb);
        _depthRb = gl.GenRenderbuffer();
        gl.BindRenderbuffer(_depthRb);
        gl.RenderbufferStorageMultisample(_samples, Gl.DepthComponent24, width, height);
        gl.FramebufferRenderbuffer(Gl.DepthAttachment, _depthRb);
        gl.BindRenderbuffer(0);
        if (gl.CheckFramebufferStatus(Gl.Framebuffer) != Gl.FramebufferComplete)
        {
            AppLog.Write("3D: own framebuffer incomplete, drawing without smoothing");
            DeleteFramebuffer();
            _samples = -1;
            return false;
        }
        _fboWidth = width;
        _fboHeight = height;
        return true;
    }

    private void DeleteFramebuffer()
    {
        var gl = _gl;
        gl.DeleteFramebuffer(_fbo);
        gl.DeleteRenderbuffer(_colourRb);
        gl.DeleteRenderbuffer(_depthRb);
        _fbo = _colourRb = _depthRb = 0;
        _fboWidth = _fboHeight = 0;
    }
}
