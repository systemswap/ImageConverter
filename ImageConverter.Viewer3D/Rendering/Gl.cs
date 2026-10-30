using System.Runtime.InteropServices;
using System.Text;
using Avalonia.OpenGL;

namespace ImageConverter.Viewer3D.Rendering;

/// <summary>
/// The OpenGL functions the viewer uses, loaded once per context through the context's own lookup
/// (<see cref="GlInterface.GetProcAddress"/>). Works with desktop OpenGL 3.0+ and OpenGL ES 3.0+ (Windows: ANGLE);
/// only functions common to both are used.
/// </summary>
/// <remarks>
/// Avalonia's <see cref="GlInterface"/> covers only part of what is needed (no uniforms other than float/int/matrix,
/// no multisampled render buffers, no blending), so all calls go through function pointers here – no extra package.
/// A missing function throws <see cref="NotSupportedException"/> when the context is set up, and the view then says
/// that 3D display is not available.
/// </remarks>
internal sealed unsafe class Gl
{
    public const int DepthBufferBit = 0x0100, ColorBufferBit = 0x4000;
    public const int Triangles = 0x0004, Lines = 0x0001;
    public const int ArrayBuffer = 0x8892, ElementArrayBuffer = 0x8893, StaticDraw = 0x88E4, DynamicDraw = 0x88E8;
    public const int Float = 0x1406, UnsignedByte = 0x1401, UnsignedInt = 0x1405;
    public const int VertexShader = 0x8B31, FragmentShader = 0x8B30, CompileStatus = 0x8B81, LinkStatus = 0x8B82;
    public const int DepthTest = 0x0B71, Blend = 0x0BE2, PolygonOffsetFill = 0x8037, CullFace = 0x0B44, ScissorTest = 0x0C11;
    public const int Less = 0x0201, LessEqual = 0x0203, Always = 0x0207;
    public const int SrcAlpha = 0x0302, OneMinusSrcAlpha = 0x0303, One = 1;
    public const int Texture2D = 0x0DE1, Texture0 = 0x84C0, TextureMinFilter = 0x2801, TextureMagFilter = 0x2800;
    public const int TextureWrapS = 0x2802, TextureWrapT = 0x2803, Linear = 0x2601, ClampToEdge = 0x812F;
    public const int Red = 0x1903, R8 = 0x8229, Rgba = 0x1908, Rgba8 = 0x8058, UnpackAlignment = 0x0CF5, PackAlignment = 0x0D05;
    public const int Framebuffer = 0x8D40, ReadFramebuffer = 0x8CA8, DrawFramebuffer = 0x8CA9, Renderbuffer = 0x8D41;
    public const int ColorAttachment0 = 0x8CE0, DepthAttachment = 0x8D00, DepthComponent24 = 0x81A6, DepthComponent = 0x1902;
    public const int FramebufferComplete = 0x8CD5, MaxSamples = 0x8D57, Nearest = 0x2600, FramebufferBinding = 0x8CA6;

    private readonly delegate* unmanaged<int> _getError;
    private readonly delegate* unmanaged<int, int*, void> _getIntegerv;
    private readonly delegate* unmanaged<int, int, int, int, void> _viewport;
    private readonly delegate* unmanaged<float, float, float, float, void> _clearColor;
    private readonly delegate* unmanaged<int, void> _clear;
    private readonly delegate* unmanaged<int, void> _enable;
    private readonly delegate* unmanaged<int, void> _disable;
    private readonly delegate* unmanaged<int, void> _depthFunc;
    private readonly delegate* unmanaged<byte, void> _depthMask;
    private readonly delegate* unmanaged<byte, byte, byte, byte, void> _colorMask;
    private readonly delegate* unmanaged<int, int, int, int, void> _blendFuncSeparate;
    private readonly delegate* unmanaged<float, float, void> _polygonOffset;
    private readonly delegate* unmanaged<int, int*, void> _genBuffers;
    private readonly delegate* unmanaged<int, int*, void> _deleteBuffers;
    private readonly delegate* unmanaged<int, int, void> _bindBuffer;
    private readonly delegate* unmanaged<int, nint, void*, int, void> _bufferData;
    private readonly delegate* unmanaged<int, nint, nint, void*, void> _bufferSubData;
    private readonly delegate* unmanaged<int, int*, void> _genVertexArrays;
    private readonly delegate* unmanaged<int, int*, void> _deleteVertexArrays;
    private readonly delegate* unmanaged<int, void> _bindVertexArray;
    private readonly delegate* unmanaged<int, int, int, byte, int, nint, void> _vertexAttribPointer;
    private readonly delegate* unmanaged<int, void> _enableVertexAttribArray;
    private readonly delegate* unmanaged<int, void> _disableVertexAttribArray;
    private readonly delegate* unmanaged<int, float, void> _vertexAttrib1f;
    private readonly delegate* unmanaged<int, int> _createShader;
    private readonly delegate* unmanaged<int, int, byte**, int*, void> _shaderSource;
    private readonly delegate* unmanaged<int, void> _compileShader;
    private readonly delegate* unmanaged<int, int, int*, void> _getShaderiv;
    private readonly delegate* unmanaged<int, int, int*, byte*, void> _getShaderInfoLog;
    private readonly delegate* unmanaged<int, void> _deleteShader;
    private readonly delegate* unmanaged<int> _createProgram;
    private readonly delegate* unmanaged<int, int, void> _attachShader;
    private readonly delegate* unmanaged<int, int, byte*, void> _bindAttribLocation;
    private readonly delegate* unmanaged<int, void> _linkProgram;
    private readonly delegate* unmanaged<int, int, int*, void> _getProgramiv;
    private readonly delegate* unmanaged<int, int, int*, byte*, void> _getProgramInfoLog;
    private readonly delegate* unmanaged<int, void> _useProgram;
    private readonly delegate* unmanaged<int, void> _deleteProgram;
    private readonly delegate* unmanaged<int, byte*, int> _getUniformLocation;
    private readonly delegate* unmanaged<int, float, void> _uniform1f;
    private readonly delegate* unmanaged<int, int, void> _uniform1i;
    private readonly delegate* unmanaged<int, float, float, void> _uniform2f;
    private readonly delegate* unmanaged<int, float, float, float, void> _uniform3f;
    private readonly delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
    private readonly delegate* unmanaged<int, int, byte, float*, void> _uniformMatrix4fv;
    private readonly delegate* unmanaged<int, int, int, void> _drawArrays;
    private readonly delegate* unmanaged<int, int, int, nint, void> _drawElements;
    private readonly delegate* unmanaged<int, int*, void> _genTextures;
    private readonly delegate* unmanaged<int, int*, void> _deleteTextures;
    private readonly delegate* unmanaged<int, int, void> _bindTexture;
    private readonly delegate* unmanaged<int, void> _activeTexture;
    private readonly delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> _texImage2D;
    private readonly delegate* unmanaged<int, int, int, void> _texParameteri;
    private readonly delegate* unmanaged<int, int, void> _pixelStorei;
    private readonly delegate* unmanaged<int, int*, void> _genFramebuffers;
    private readonly delegate* unmanaged<int, int*, void> _deleteFramebuffers;
    private readonly delegate* unmanaged<int, int, void> _bindFramebuffer;
    private readonly delegate* unmanaged<int, int> _checkFramebufferStatus;
    private readonly delegate* unmanaged<int, int, int, int, void> _framebufferRenderbuffer;
    private readonly delegate* unmanaged<int, int, int, int, int, void> _framebufferTexture2D;
    private readonly delegate* unmanaged<int, int*, void> _genRenderbuffers;
    private readonly delegate* unmanaged<int, int*, void> _deleteRenderbuffers;
    private readonly delegate* unmanaged<int, int, void> _bindRenderbuffer;
    private readonly delegate* unmanaged<int, int, int, int, int, void> _renderbufferStorageMultisample;
    private readonly delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void> _blitFramebuffer;
    private readonly delegate* unmanaged<int, int, int, int, int, int, void*, void> _readPixels;

    private readonly GlInterface _gl;

    /// <summary>Loads the functions from the current context.</summary>
    /// <exception cref="NotSupportedException">The context lacks a function the viewer needs.</exception>
    public Gl(GlInterface gl, GlVersion version)
    {
        _gl = gl;
        IsEs = version.Type == GlProfileType.OpenGLES;
        Version = version;
        _getError = (delegate* unmanaged<int>)Get("glGetError");
        _getIntegerv = (delegate* unmanaged<int, int*, void>)Get("glGetIntegerv");
        _viewport = (delegate* unmanaged<int, int, int, int, void>)Get("glViewport");
        _clearColor = (delegate* unmanaged<float, float, float, float, void>)Get("glClearColor");
        _clear = (delegate* unmanaged<int, void>)Get("glClear");
        _enable = (delegate* unmanaged<int, void>)Get("glEnable");
        _disable = (delegate* unmanaged<int, void>)Get("glDisable");
        _depthFunc = (delegate* unmanaged<int, void>)Get("glDepthFunc");
        _depthMask = (delegate* unmanaged<byte, void>)Get("glDepthMask");
        _colorMask = (delegate* unmanaged<byte, byte, byte, byte, void>)Get("glColorMask");
        _blendFuncSeparate = (delegate* unmanaged<int, int, int, int, void>)Get("glBlendFuncSeparate");
        _polygonOffset = (delegate* unmanaged<float, float, void>)Get("glPolygonOffset");
        _genBuffers = (delegate* unmanaged<int, int*, void>)Get("glGenBuffers");
        _deleteBuffers = (delegate* unmanaged<int, int*, void>)Get("glDeleteBuffers");
        _bindBuffer = (delegate* unmanaged<int, int, void>)Get("glBindBuffer");
        _bufferData = (delegate* unmanaged<int, nint, void*, int, void>)Get("glBufferData");
        _bufferSubData = (delegate* unmanaged<int, nint, nint, void*, void>)Get("glBufferSubData");
        _genVertexArrays = (delegate* unmanaged<int, int*, void>)Get("glGenVertexArrays");
        _deleteVertexArrays = (delegate* unmanaged<int, int*, void>)Get("glDeleteVertexArrays");
        _bindVertexArray = (delegate* unmanaged<int, void>)Get("glBindVertexArray");
        _vertexAttribPointer = (delegate* unmanaged<int, int, int, byte, int, nint, void>)Get("glVertexAttribPointer");
        _enableVertexAttribArray = (delegate* unmanaged<int, void>)Get("glEnableVertexAttribArray");
        _disableVertexAttribArray = (delegate* unmanaged<int, void>)Get("glDisableVertexAttribArray");
        _vertexAttrib1f = (delegate* unmanaged<int, float, void>)Get("glVertexAttrib1f");
        _createShader = (delegate* unmanaged<int, int>)Get("glCreateShader");
        _shaderSource = (delegate* unmanaged<int, int, byte**, int*, void>)Get("glShaderSource");
        _compileShader = (delegate* unmanaged<int, void>)Get("glCompileShader");
        _getShaderiv = (delegate* unmanaged<int, int, int*, void>)Get("glGetShaderiv");
        _getShaderInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)Get("glGetShaderInfoLog");
        _deleteShader = (delegate* unmanaged<int, void>)Get("glDeleteShader");
        _createProgram = (delegate* unmanaged<int>)Get("glCreateProgram");
        _attachShader = (delegate* unmanaged<int, int, void>)Get("glAttachShader");
        _bindAttribLocation = (delegate* unmanaged<int, int, byte*, void>)Get("glBindAttribLocation");
        _linkProgram = (delegate* unmanaged<int, void>)Get("glLinkProgram");
        _getProgramiv = (delegate* unmanaged<int, int, int*, void>)Get("glGetProgramiv");
        _getProgramInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)Get("glGetProgramInfoLog");
        _useProgram = (delegate* unmanaged<int, void>)Get("glUseProgram");
        _deleteProgram = (delegate* unmanaged<int, void>)Get("glDeleteProgram");
        _getUniformLocation = (delegate* unmanaged<int, byte*, int>)Get("glGetUniformLocation");
        _uniform1f = (delegate* unmanaged<int, float, void>)Get("glUniform1f");
        _uniform1i = (delegate* unmanaged<int, int, void>)Get("glUniform1i");
        _uniform2f = (delegate* unmanaged<int, float, float, void>)Get("glUniform2f");
        _uniform3f = (delegate* unmanaged<int, float, float, float, void>)Get("glUniform3f");
        _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)Get("glUniform4f");
        _uniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)Get("glUniformMatrix4fv");
        _drawArrays = (delegate* unmanaged<int, int, int, void>)Get("glDrawArrays");
        _drawElements = (delegate* unmanaged<int, int, int, nint, void>)Get("glDrawElements");
        _genTextures = (delegate* unmanaged<int, int*, void>)Get("glGenTextures");
        _deleteTextures = (delegate* unmanaged<int, int*, void>)Get("glDeleteTextures");
        _bindTexture = (delegate* unmanaged<int, int, void>)Get("glBindTexture");
        _activeTexture = (delegate* unmanaged<int, void>)Get("glActiveTexture");
        _texImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>)Get("glTexImage2D");
        _texParameteri = (delegate* unmanaged<int, int, int, void>)Get("glTexParameteri");
        _pixelStorei = (delegate* unmanaged<int, int, void>)Get("glPixelStorei");
        _genFramebuffers = (delegate* unmanaged<int, int*, void>)Get("glGenFramebuffers");
        _deleteFramebuffers = (delegate* unmanaged<int, int*, void>)Get("glDeleteFramebuffers");
        _bindFramebuffer = (delegate* unmanaged<int, int, void>)Get("glBindFramebuffer");
        _checkFramebufferStatus = (delegate* unmanaged<int, int>)Get("glCheckFramebufferStatus");
        _framebufferRenderbuffer = (delegate* unmanaged<int, int, int, int, void>)Get("glFramebufferRenderbuffer");
        _framebufferTexture2D = (delegate* unmanaged<int, int, int, int, int, void>)Get("glFramebufferTexture2D");
        _genRenderbuffers = (delegate* unmanaged<int, int*, void>)Get("glGenRenderbuffers");
        _deleteRenderbuffers = (delegate* unmanaged<int, int*, void>)Get("glDeleteRenderbuffers");
        _bindRenderbuffer = (delegate* unmanaged<int, int, void>)Get("glBindRenderbuffer");
        _renderbufferStorageMultisample = (delegate* unmanaged<int, int, int, int, int, void>)Get("glRenderbufferStorageMultisample");
        _blitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void>)Get("glBlitFramebuffer");
        _readPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)Get("glReadPixels");
    }

    /// <summary>OpenGL ES (ANGLE on Windows, some Linux drivers) rather than desktop OpenGL.</summary>
    public bool IsEs { get; }

    public GlVersion Version { get; }

    private nint Get(string name)
    {
        var p = _gl.GetProcAddress(name);
        if (p == 0) throw new NotSupportedException($"OpenGL function {name} is missing.");
        return p;
    }

    public int GetError() => _getError();

    public int GetInteger(int name)
    {
        int v;
        _getIntegerv(name, &v);
        return v;
    }

    public void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
    public void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
    public void ClearDepth() => _gl.ClearDepth(1f);
    public void Clear(int mask) => _clear(mask);
    public void Enable(int cap) => _enable(cap);
    public void Disable(int cap) => _disable(cap);
    public void DepthFunc(int f) => _depthFunc(f);
    public void DepthMask(bool on) => _depthMask(on ? (byte)1 : (byte)0);
    public void ColorMask(bool on) => _colorMask(on ? (byte)1 : (byte)0, on ? (byte)1 : (byte)0, on ? (byte)1 : (byte)0, on ? (byte)1 : (byte)0);
    public void BlendFunc(int src, int dst) => _blendFuncSeparate(src, dst, One, OneMinusSrcAlpha);
    public void PolygonOffset(float factor, float units) => _polygonOffset(factor, units);

    public int GenBuffer()
    {
        int b;
        _genBuffers(1, &b);
        return b;
    }

    public void DeleteBuffer(int b)
    {
        if (b != 0) _deleteBuffers(1, &b);
    }

    public void BindBuffer(int target, int b) => _bindBuffer(target, b);

    /// <summary>Uploads data into the buffer bound to <paramref name="target"/>.</summary>
    public void BufferData<T>(int target, ReadOnlySpan<T> data, int usage) where T : unmanaged
    {
        fixed (T* p = data) _bufferData(target, (nint)data.Length * sizeof(T), p, usage);
    }

    public void BufferSubData<T>(int target, long offsetElements, ReadOnlySpan<T> data) where T : unmanaged
    {
        fixed (T* p = data) _bufferSubData(target, (nint)(offsetElements * sizeof(T)), (nint)data.Length * sizeof(T), p);
    }

    public int GenVertexArray()
    {
        int v;
        _genVertexArrays(1, &v);
        return v;
    }

    public void DeleteVertexArray(int v)
    {
        if (v != 0) _deleteVertexArrays(1, &v);
    }

    public void BindVertexArray(int v) => _bindVertexArray(v);

    public void VertexAttribPointer(int index, int size, int type, bool normalized, int stride, nint offset) =>
        _vertexAttribPointer(index, size, type, normalized ? (byte)1 : (byte)0, stride, offset);

    public void EnableVertexAttribArray(int index) => _enableVertexAttribArray(index);
    public void DisableVertexAttribArray(int index) => _disableVertexAttribArray(index);
    public void VertexAttrib1f(int index, float v) => _vertexAttrib1f(index, v);

    /// <summary>Compiles and links a program; attribute <c>i</c> gets the i-th name.</summary>
    /// <exception cref="InvalidOperationException">Compiling or linking failed; the message holds the driver's log.</exception>
    public int Program(string vertex, string fragment, params string[] attributes)
    {
        var vs = Compile(VertexShader, vertex);
        var fs = Compile(FragmentShader, fragment);
        var program = _createProgram();
        _attachShader(program, vs);
        _attachShader(program, fs);
        for (var i = 0; i < attributes.Length; i++)
        {
            var name = Encoding.ASCII.GetBytes(attributes[i] + "\0");
            fixed (byte* n = name) _bindAttribLocation(program, i, n);
        }
        _linkProgram(program);
        _deleteShader(vs);
        _deleteShader(fs);
        int ok;
        _getProgramiv(program, LinkStatus, &ok);
        if (ok == 0)
        {
            var log = Log(program, isProgram: true);
            _deleteProgram(program);
            throw new InvalidOperationException("Linking failed: " + log);
        }
        return program;
    }

    private int Compile(int type, string source)
    {
        var shader = _createShader(type);
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* s = bytes)
        {
            var ptr = s;
            var len = bytes.Length;
            _shaderSource(shader, 1, &ptr, &len);
        }
        _compileShader(shader);
        int ok;
        _getShaderiv(shader, CompileStatus, &ok);
        if (ok == 0)
        {
            var log = Log(shader, isProgram: false);
            _deleteShader(shader);
            throw new InvalidOperationException("Compiling failed: " + log);
        }
        return shader;
    }

    private string Log(int obj, bool isProgram)
    {
        var buffer = new byte[4096];
        int len;
        fixed (byte* b = buffer)
        {
            if (isProgram) _getProgramInfoLog(obj, buffer.Length, &len, b);
            else _getShaderInfoLog(obj, buffer.Length, &len, b);
        }
        return Encoding.UTF8.GetString(buffer, 0, Math.Clamp(len, 0, buffer.Length));
    }

    public void UseProgram(int p) => _useProgram(p);

    public void DeleteProgram(int p)
    {
        if (p != 0) _deleteProgram(p);
    }

    public int Uniform(int program, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* n = bytes) return _getUniformLocation(program, n);
    }

    public void Uniform1(int loc, float v) => _uniform1f(loc, v);
    public void Uniform1(int loc, int v) => _uniform1i(loc, v);
    public void Uniform2(int loc, float x, float y) => _uniform2f(loc, x, y);
    public void Uniform3(int loc, System.Numerics.Vector3 v) => _uniform3f(loc, v.X, v.Y, v.Z);
    public void Uniform4(int loc, System.Numerics.Vector4 v) => _uniform4f(loc, v.X, v.Y, v.Z, v.W);

    /// <summary>Sets a matrix uniform from a System.Numerics matrix (row-major, row vectors) – GL reads it as the transposed
    /// column-major matrix, which is exactly the column-vector form <c>M * v</c>.</summary>
    public void UniformMatrix(int loc, System.Numerics.Matrix4x4 m) => _uniformMatrix4fv(loc, 1, 0, (float*)&m);

    public void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);

    /// <summary>Draws from the bound element buffer with 32-bit indices, starting at index <paramref name="first"/>.</summary>
    public void DrawElements(int mode, int count, long first) => _drawElements(mode, count, UnsignedInt, (nint)(first * 4));

    public int GenTexture()
    {
        int t;
        _genTextures(1, &t);
        return t;
    }

    public void DeleteTexture(int t)
    {
        if (t != 0) _deleteTextures(1, &t);
    }

    public void BindTexture(int target, int t) => _bindTexture(target, t);
    public void ActiveTexture(int unit) => _activeTexture(unit);
    public void TexParameter(int target, int name, int value) => _texParameteri(target, name, value);
    public void PixelStore(int name, int value) => _pixelStorei(name, value);

    /// <summary>Single-channel 8-bit texture (shadow under the model).</summary>
    public void TexImageR8(int width, int height, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data) _texImage2D(Texture2D, 0, R8, width, height, 0, Red, UnsignedByte, p);
    }

    public int GenFramebuffer()
    {
        int f;
        _genFramebuffers(1, &f);
        return f;
    }

    public void DeleteFramebuffer(int f)
    {
        if (f != 0) _deleteFramebuffers(1, &f);
    }

    public void BindFramebuffer(int target, int f) => _bindFramebuffer(target, f);
    public int CheckFramebufferStatus(int target) => _checkFramebufferStatus(target);
    public void FramebufferRenderbuffer(int attachment, int rb) => _framebufferRenderbuffer(Framebuffer, attachment, Renderbuffer, rb);
    public void FramebufferTexture(int attachment, int texture) => _framebufferTexture2D(Framebuffer, attachment, Texture2D, texture, 0);

    /// <summary>Empty 24-bit depth texture (read by the occlusion pass).</summary>
    public void TexImageDepth(int width, int height) =>
        _texImage2D(Texture2D, 0, DepthComponent24, width, height, 0, DepthComponent, UnsignedInt, null);

    public int GenRenderbuffer()
    {
        int r;
        _genRenderbuffers(1, &r);
        return r;
    }

    public void DeleteRenderbuffer(int r)
    {
        if (r != 0) _deleteRenderbuffers(1, &r);
    }

    public void BindRenderbuffer(int r) => _bindRenderbuffer(Renderbuffer, r);
    public void RenderbufferStorageMultisample(int samples, int format, int w, int h) => _renderbufferStorageMultisample(Renderbuffer, samples, format, w, h);

    public void BlitFramebuffer(int w, int h) => _blitFramebuffer(0, 0, w, h, 0, 0, w, h, ColorBufferBit, Nearest);

    /// <summary>Reads the bound framebuffer as RGBA, bottom row first.</summary>
    public void ReadPixels(int w, int h, Span<byte> rgba)
    {
        fixed (byte* p = rgba) _readPixels(0, 0, w, h, Rgba, UnsignedByte, p);
    }
}
