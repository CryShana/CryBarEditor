using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using CryBarEditor.Classes;
using static Avalonia.OpenGL.GlConsts;

namespace CryBarEditor.Controls;

public class GlPreviewControl : OpenGlControlBase, ICustomHitTest
{
    // GL constants Avalonia's GlConsts doesn't expose; named here so call sites are searchable.
    const int GL_LINES                    = 0x0001;
    const int GL_LESS                     = 0x0201;
    const int GL_GREATER                  = 0x0204;
    const int GL_BLEND                    = 0x0BE2;
    const int GL_SRC_ALPHA                = 0x0302;
    const int GL_ONE_MINUS_SRC_ALPHA      = 0x0303;
    const int GL_UNSIGNED_BYTE            = 0x1401;
    const int GL_UNSIGNED_INT             = 0x1405;
    const int GL_FLOAT_TYPE               = 0x1406;
    const int GL_DEPTH_COMPONENT          = 0x1902;
    const int GL_LINEAR                   = 0x2601;
    const int GL_LINEAR_MIPMAP_LINEAR     = 0x2703;
    const int GL_TEXTURE_MAG_FILTER       = 0x2800;
    const int GL_TEXTURE_MIN_FILTER       = 0x2801;
    const int GL_TEXTURE_WRAP_S           = 0x2802;
    const int GL_TEXTURE_WRAP_T           = 0x2803;
    const int GL_REPEAT                   = 0x2901;
    const int GL_TEXTURE0                 = 0x84C0;
    const int GL_TEXTURE1                 = 0x84C1;
    const int GL_DYNAMIC_DRAW             = 0x88E8;
    const int GL_DEPTH_COMPONENT24        = 0x81A6;
    const int GL_MAX_RENDERBUFFER_SIZE    = 0x84E8;
    // OpenGlControlBase has no background, so implement ICustomHitTest for pointer events
    bool ICustomHitTest.HitTest(Point point) => Bounds.Contains(point);

    readonly OrbitCamera _camera = new();
    PreviewMeshData? _meshData;
    bool _meshDirty;

    // GL resources
    int _program;
    int _vao, _vbo, _ebo;
    int _uMvp, _uLightDir, _uColor;
    bool _glInitialized;

    // Textured shader (TBN normal mapping)
    int _texturedProgram;
    int _uTexMvp, _uTexLightDir, _uTexBaseSampler, _uTexNormalSampler;
    bool _useTextured;
    PreviewTextureSet? _activeTextures;

    // Public API: gizmo label projection (consumed by overlay canvas)
    public readonly record struct GizmoLabel(int Axis, string Letter, double X, double Y, bool Hovered);

    public event Action<IReadOnlyList<GizmoLabel>>? GizmoLabelsProjected;
    readonly List<GizmoLabel> _gizmoLabelBuffer = new(3);

    // Public API: marker label projection (consumed by overlay canvas)
    public readonly record struct MarkerLabel(string Name, double X, double Y, bool Visible, bool Occluded);

    public event Action<IReadOnlyList<MarkerLabel>>? MarkersProjected;
    readonly List<MarkerLabel> _markerLabelBuffer = new();

    // Fires when the GL context tears down; hosts must drop cached GL handles here.
    public event Action? GlContextLost;

    // Gizmo GL resources
    int _gizmoProgram;
    int _gizmoVao, _gizmoVbo;
    int _gizmoVertexCount;
    int _uGizmoView, _uGizmoColor;
    const int GizmoSizePx = 96;
    const int GizmoMarginPx = 8;

    // Six axis colors for the gizmo: +X, -X, +Y, -Y, +Z, -Z. Hovered axis brightens 1.3x, clamped.
    static readonly (float r, float g, float b)[] _gizmoAxisColors =
    {
        (1.0f, 0.20f, 0.20f), (0.5f, 0.10f, 0.10f),
        (0.20f, 1.0f, 0.20f), (0.10f, 0.5f, 0.10f),
        (0.30f, 0.50f, 1.0f), (0.15f, 0.25f, 0.5f),
    };

    // Positive-axis label specs: (gizmoAxisIndex, letter, ax, ay, az). Indices match _hoveredGizmoAxis.
    static readonly (int axis, string letter, float ax, float ay, float az)[] _gizmoPositiveAxes =
    {
        (0, "X", 1, 0, 0),
        (2, "Y", 0, 1, 0),
        (4, "Z", 0, 0, 1),
    };

    // Markers GL resources. Buffer layout: pos(3) + color(3) per vertex; uploaded once per mesh.
    int _markersProgram;
    int _markersVao, _markersVbo;
    int _uMarkersMvp, _uMarkersAlpha;
    int _markersAttachVertexCount; // number of vertices for attachment markers (drawn first)
    int _markersImpactVertexCount; // number of vertices for impact-point markers
    bool _markersDirty;

    bool _showMarkers = true;
    public bool ShowMarkers
    {
        get => _showMarkers;
        set
        {
            if (_showMarkers == value) return;
            _showMarkers = value;
            RequestNextFrameRendering();
        }
    }

    // Ground grid GL resources
    int _gridProgram;
    int _gridVao, _gridVbo;
    int _uGridMvp, _uGridColor;
    int _gridVertexCount;

    bool _showGroundGrid = true;
    public bool ShowGroundGrid
    {
        get => _showGroundGrid;
        set
        {
            if (_showGroundGrid == value) return;
            _showGroundGrid = value;
            RequestNextFrameRendering();
        }
    }

    public bool UseTexturedMode
    {
        get => _useTextured;
        set
        {
            if (_useTextured == value) return;
            _useTextured = value;
            RequestNextFrameRendering();
        }
    }

    /// <summary>Hand a built texture set to the control. Pass null to clear.</summary>
    public void SetActiveTextures(PreviewTextureSet? textures)
    {
        _activeTextures = textures;
        RequestNextFrameRendering();
    }

    // Drained at the start of OnOpenGlRender; lets the host run GL ops without owning a context.
    readonly ConcurrentQueue<Action<GlInterface>> _glActionQueue = new();

    /// <summary>Schedules an action to run on the GL render thread before the next frame is drawn.</summary>
    public void QueueGlAction(Action<GlInterface> action)
    {
        _glActionQueue.Enqueue(action);
        RequestNextFrameRendering();
    }

    /// <summary>Returns the currently loaded mesh, or null if no mesh has been loaded yet.</summary>
    public PreviewMeshData? GetMeshData() => _meshData;

    public readonly record struct CaptureResult(byte[] Rgba, int Width, int Height);

    TaskCompletionSource<CaptureResult>? _pendingCapture;

    /// <summary>
    /// Renders the current mesh (no grid/markers/gizmo) into an offscreen framebuffer at the
    /// given pixel size and returns the RGBA pixels, top row first. Uses the current camera.
    /// </summary>
    public Task<CaptureResult> CaptureScreenshotAsync(int width, int height, bool transparentBackground)
    {
        if (_meshData == null)
            return Task.FromException<CaptureResult>(new InvalidOperationException("No model loaded"));
        if (_pendingCapture != null)
            return Task.FromException<CaptureResult>(new InvalidOperationException("Capture already in progress"));

        var tcs = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCapture = tcs;
        QueueGlAction(gl =>
        {
            _pendingCapture = null;
            try { tcs.TrySetResult(RenderCapture(gl, width, height, transparentBackground)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    unsafe CaptureResult RenderCapture(GlInterface gl, int width, int height, bool transparent)
    {
        var mesh = _meshData ?? throw new InvalidOperationException("No model loaded");
        if (!_glInitialized || _glReadPixels == null)
            throw new InvalidOperationException("GL not initialized");

        gl.GetIntegerv(GL_MAX_RENDERBUFFER_SIZE, out int maxSize);
        width = Math.Clamp(width, 1, maxSize);
        height = Math.Clamp(height, 1, maxSize);

        gl.GetIntegerv(GL_FRAMEBUFFER_BINDING, out int prevFb);

        int fbo = gl.GenFramebuffer();
        int colorRb = gl.GenRenderbuffer();
        int depthRb = gl.GenRenderbuffer();
        try
        {
            gl.BindRenderbuffer(GL_RENDERBUFFER, colorRb);
            gl.RenderbufferStorage(GL_RENDERBUFFER, GL_RGBA8, width, height);
            gl.BindRenderbuffer(GL_RENDERBUFFER, depthRb);
            gl.RenderbufferStorage(GL_RENDERBUFFER, GL_DEPTH_COMPONENT24, width, height);
            gl.BindRenderbuffer(GL_RENDERBUFFER, 0);

            gl.BindFramebuffer(GL_FRAMEBUFFER, fbo);
            gl.FramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, colorRb);
            gl.FramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_RENDERBUFFER, depthRb);
            if (gl.CheckFramebufferStatus(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
                throw new InvalidOperationException("Offscreen framebuffer incomplete");

            gl.Viewport(0, 0, width, height);
            if (transparent) gl.ClearColor(0f, 0f, 0f, 0f);
            else gl.ClearColor(0.04f, 0.04f, 0.04f, 1f);
            gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

            float aspect = (float)width / height;
            var view = _camera.GetViewMatrix(out var eye);
            var proj = _camera.GetProjectionMatrix(aspect);
            var mvp = view * proj;
            var target = new Vector3(_camera.TargetX, _camera.TargetY, _camera.TargetZ);
            var lightDir = Vector3.Normalize(eye - target);

            gl.Enable(GL_DEPTH_TEST);
            gl.BindVertexArray(_vao);
            EnsureMeshUploaded(gl, mesh);
            DrawMeshPass(gl, mesh, mvp, lightDir);
            gl.BindVertexArray(0);
            gl.UseProgram(0);
            gl.Disable(GL_DEPTH_TEST);

            var pixels = new byte[(long)width * height * 4];
            fixed (byte* p = pixels)
                _glReadPixels(0, 0, width, height, (uint)GL_RGBA, (uint)GL_UNSIGNED_BYTE, p);
            ScreenshotHelpers.FlipRowsInPlace(pixels, width, height);

            return new CaptureResult(pixels, width, height);
        }
        finally
        {
            gl.BindFramebuffer(GL_FRAMEBUFFER, prevFb);
            gl.DeleteFramebuffer(fbo);
            gl.DeleteRenderbuffer(colorRb);
            gl.DeleteRenderbuffer(depthRb);
        }
    }

    /// <summary>
    /// Uploads RGBA8 pixels to a fresh GL texture and returns the handle.
    /// Must be called on the GL render thread (e.g., from a queued action processed in OnOpenGlRender).
    /// </summary>
    public unsafe int UploadTexture(GlInterface gl, int width, int height, ReadOnlySpan<byte> rgba)
    {
        int tex = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, tex);
        fixed (byte* p = rgba)
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, (IntPtr)p);
        SetTextureSamplerStateAndMipmap(gl);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return tex;
    }

    /// <summary>
    /// Allocates a fresh RGBA8 GL texture sized [width x height] without uploading pixels.
    /// Use UploadTextureRows to write horizontal strips, then BindTexture(0) to release.
    /// Allows chunked uploads that avoid host-side contiguous-buffer copies.
    /// </summary>
    public int CreateEmptyTexture(GlInterface gl, int width, int height)
    {
        int tex = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, tex);
        gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0, GL_RGBA, GL_UNSIGNED_BYTE, IntPtr.Zero);
        return tex;
    }

    /// <summary>Writes a horizontal strip of RGBA8 pixels to the currently bound texture.</summary>
    public unsafe void UploadTextureRows(GlInterface gl, int yOffset, int width, int rowCount, ReadOnlySpan<byte> rgba)
    {
        if (_glTexSubImage2D == null) return; // Should never happen on a valid GL 3+ context.
        fixed (byte* p = rgba)
            _glTexSubImage2D((uint)GL_TEXTURE_2D, 0, 0, yOffset, width, rowCount, (uint)GL_RGBA, (uint)GL_UNSIGNED_BYTE, p);
    }

    /// <summary>Applies sampler state and generates mipmaps for the currently bound 2D texture.</summary>
    public void FinalizeTexture(GlInterface gl)
    {
        SetTextureSamplerStateAndMipmap(gl);
        gl.BindTexture(GL_TEXTURE_2D, 0);
    }

    unsafe void SetTextureSamplerStateAndMipmap(GlInterface gl)
    {
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR_MIPMAP_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_REPEAT);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_REPEAT);
        if (_glGenerateMipmap != null)
            _glGenerateMipmap(GL_TEXTURE_2D);
    }

    /// <summary>Convenience for hosts: callback they can invoke on the GL thread to delete a handle.</summary>
    public void DeleteTexture(GlInterface gl, int handle) => gl.DeleteTexture(handle);

    // Gizmo hover / animation state
    int _hoveredGizmoAxis = -1;
    bool _animActive;
    float _animStartAz, _animStartEl, _animEndAz, _animEndEl;
    double _animStartTimeMs;
    const double AnimDurationMs = 200.0;
    readonly System.Diagnostics.Stopwatch _animClock = new();

    // Function pointers not exposed by Avalonia's GlInterface; resolved in OnOpenGlInit.
    unsafe delegate* unmanaged<int, float, float, float, void> _glUniform3f;
    unsafe delegate* unmanaged<int, float, float, float, float, void> _glUniform4f;
    unsafe delegate* unmanaged<int, int, byte, float*, void> _glUniformMatrix3fv;
    unsafe delegate* unmanaged<float, void> _glLineWidth;
    unsafe delegate* unmanaged<int, void> _glGenerateMipmap;
    unsafe delegate* unmanaged<uint, uint, void> _glBlendFunc;
    unsafe delegate* unmanaged<int, int, int, int, uint, uint, void*, void> _glReadPixels;
    unsafe delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> _glTexSubImage2D;

    // Mouse tracking
    Point _lastPointerPos;
    bool _leftDragging, _rightDragging;

    // Shader bodies - version/precision preamble prepended at runtime
    const string VertexShaderBody = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uMVP;
        out vec3 vNormal;
        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vNormal = normalize(aNormal);
        }
        """;

    const string FragmentShaderBody = """
        in vec3 vNormal;
        uniform vec3 uLightDir;
        uniform vec3 uColor;
        out vec4 FragColor;
        void main() {
            float diff = max(dot(normalize(vNormal), uLightDir), 0.0);
            vec3 col = uColor * (0.25 + 0.75 * diff);
            FragColor = vec4(col, 1.0);
        }
        """;

    const string TexturedVertexShaderBody = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aTangent;
        uniform mat4 uMVP;
        out vec3 vNormal;
        out vec3 vTangent;
        out vec3 vBitangent;
        out vec2 vUv;
        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vec3 n = normalize(aNormal);
            vec3 t = normalize(aTangent.xyz);
            vec3 b = cross(n, t) * aTangent.w;
            vNormal = n;
            vTangent = t;
            vBitangent = b;
            vUv = aUv;
        }
        """;

    const string TexturedFragmentShaderBody = """
        in vec3 vNormal;
        in vec3 vTangent;
        in vec3 vBitangent;
        in vec2 vUv;
        uniform vec3 uLightDir;
        uniform sampler2D uBaseColor;
        uniform sampler2D uNormalMap;
        out vec4 FragColor;
        void main() {
            vec3 sampledN = texture(uNormalMap, vUv).rgb * 2.0 - 1.0;
            mat3 tbn = mat3(normalize(vTangent), normalize(vBitangent), normalize(vNormal));
            vec3 worldN = normalize(tbn * sampledN);
            float diff = max(dot(worldN, uLightDir), 0.0);
            vec3 base = texture(uBaseColor, vUv).rgb;
            FragColor = vec4(base * (0.25 + 0.75 * diff), 1.0);
        }
        """;

    const string MarkersVertexShaderBody = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMVP;
        out vec3 vColor;
        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vColor = aColor;
        }
        """;

    const string MarkersFragmentShaderBody = """
        in vec3 vColor;
        uniform float uAlpha;
        out vec4 FragColor;
        void main() { FragColor = vec4(vColor, uAlpha); }
        """;

    const string GridVertexShaderBody = """
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMVP;
        void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
        """;

    const string GridFragmentShaderBody = """
        uniform vec4 uColor;
        out vec4 FragColor;
        void main() { FragColor = uColor; }
        """;

    const string GizmoVertexShaderBody = """
        layout(location = 0) in vec3 aPos;
        uniform mat3 uViewRot;
        void main() {
            vec3 v = uViewRot * aPos;
            // Orthographic projection into clip space; gizmo viewport is square
            gl_Position = vec4(v.x, v.y, -v.z * 0.001, 1.0);
        }
        """;

    const string GizmoFragmentShaderBody = """
        uniform vec3 uColor;
        out vec4 FragColor;
        void main() {
            FragColor = vec4(uColor, 1.0);
        }
        """;

    public void LoadMesh(PreviewMeshData meshData, bool resetCamera = false)
    {
        _meshDirty = true;
        _markersDirty = true;
        if (_meshData == null || resetCamera)
            _camera.FitToSphere(meshData.CenterX, meshData.CenterY, meshData.CenterZ, meshData.Radius);
        _meshData = meshData;
        RequestNextFrameRendering();
    }

    public void ClearMesh()
    {
        _meshData = null;
        _meshDirty = true;
        _markersDirty = true;
        RequestNextFrameRendering();
    }

    public void ResetCamera()
    {
        if (_meshData != null)
            _camera.FitToSphere(_meshData.CenterX, _meshData.CenterY, _meshData.CenterZ, _meshData.Radius);
        RequestNextFrameRendering();
    }

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        bool isGles = gl.ContextInfo.Version.Type == GlProfileType.OpenGLES;
        string vsPreamble = isGles ? "#version 300 es\n" : "#version 330 core\n";
        string fsPreamble = isGles ? "#version 300 es\nprecision mediump float;\n" : "#version 330 core\n";

        _glUniform3f = (delegate* unmanaged<int, float, float, float, void>)gl.GetProcAddress("glUniform3f");
        _glUniform4f = (delegate* unmanaged<int, float, float, float, float, void>)gl.GetProcAddress("glUniform4f");
        _glUniformMatrix3fv = (delegate* unmanaged<int, int, byte, float*, void>)gl.GetProcAddress("glUniformMatrix3fv");
        _glLineWidth = (delegate* unmanaged<float, void>)gl.GetProcAddress("glLineWidth");
        _glGenerateMipmap = (delegate* unmanaged<int, void>)gl.GetProcAddress("glGenerateMipmap");
        _glBlendFunc = (delegate* unmanaged<uint, uint, void>)gl.GetProcAddress("glBlendFunc");
        _glReadPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)gl.GetProcAddress("glReadPixels");
        _glTexSubImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)gl.GetProcAddress("glTexSubImage2D");

        _program = CreateProgram(gl, vsPreamble + VertexShaderBody, fsPreamble + FragmentShaderBody);
        _uMvp = gl.GetUniformLocationString(_program, "uMVP");
        _uLightDir = gl.GetUniformLocationString(_program, "uLightDir");
        _uColor = gl.GetUniformLocationString(_program, "uColor");

        _texturedProgram     = CreateProgram(gl, vsPreamble + TexturedVertexShaderBody, fsPreamble + TexturedFragmentShaderBody);
        _uTexMvp             = gl.GetUniformLocationString(_texturedProgram, "uMVP");
        _uTexLightDir        = gl.GetUniformLocationString(_texturedProgram, "uLightDir");
        _uTexBaseSampler     = gl.GetUniformLocationString(_texturedProgram, "uBaseColor");
        _uTexNormalSampler   = gl.GetUniformLocationString(_texturedProgram, "uNormalMap");

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();

        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _ebo);

        const int stride = PreviewMeshData.VertexStrideBytes;
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, stride, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, stride, new IntPtr(PreviewMeshData.VertexNormalByteOffset));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 2, GL_FLOAT, 0, stride, new IntPtr(PreviewMeshData.VertexUvByteOffset));
        gl.EnableVertexAttribArray(2);
        // Tangent at layout 3; solid shader ignores it, textured shader needs it for TBN.
        gl.VertexAttribPointer(3, 4, GL_FLOAT, 0, stride, new IntPtr(PreviewMeshData.VertexTangentByteOffset));
        gl.EnableVertexAttribArray(3);

        gl.BindVertexArray(0);

        InitGroundGridResources(gl, vsPreamble, fsPreamble);
        InitMarkersResources(gl, vsPreamble, fsPreamble);
        InitGizmoResources(gl, vsPreamble, fsPreamble);

        _glInitialized = true;
    }

    unsafe void InitGroundGridResources(GlInterface gl, string vsPreamble, string fsPreamble)
    {
        _gridProgram = CreateProgram(gl,
            vsPreamble + GridVertexShaderBody,
            fsPreamble + GridFragmentShaderBody);
        _uGridMvp   = gl.GetUniformLocationString(_gridProgram, "uMVP");
        _uGridColor = gl.GetUniformLocationString(_gridProgram, "uColor");

        // Build a 20x20 grid of XZ lines centered at origin in the y=0 plane.
        // Each line runs full extent in X or Z; lines spaced 1 unit apart.
        const int half = 10;            // lines from -half..+half
        const float step = 1.0f;
        const float extent = half * step;
        int lineCount = (half * 2 + 1) * 2; // X-direction + Z-direction
        int floatCount = lineCount * 2 * 3; // 2 verts per line, 3 floats per vert
        var verts = new float[floatCount];
        int vi = 0;
        for (int i = -half; i <= half; i++)
        {
            float z = i * step;
            verts[vi++] = -extent; verts[vi++] = 0; verts[vi++] = z;
            verts[vi++] =  extent; verts[vi++] = 0; verts[vi++] = z;
            float x = i * step;
            verts[vi++] = x; verts[vi++] = 0; verts[vi++] = -extent;
            verts[vi++] = x; verts[vi++] = 0; verts[vi++] =  extent;
        }
        _gridVertexCount = lineCount * 2;

        _gridVao = gl.GenVertexArray();
        gl.BindVertexArray(_gridVao);
        _gridVbo = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _gridVbo);
        fixed (float* p = verts)
            gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(verts.Length * sizeof(float)), (IntPtr)p, GL_STATIC_DRAW);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 12, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.BindVertexArray(0);
    }

    unsafe void InitGizmoResources(GlInterface gl, string vsPreamble, string fsPreamble)
    {
        _gizmoProgram = CreateProgram(gl,
            vsPreamble + GizmoVertexShaderBody,
            fsPreamble + GizmoFragmentShaderBody);
        _uGizmoView  = gl.GetUniformLocationString(_gizmoProgram, "uViewRot");
        _uGizmoColor = gl.GetUniformLocationString(_gizmoProgram, "uColor");

        // 6 axis lines (origin to +-X/Y/Z), each as 2 vertices = 12 vertices.
        // Pack as one buffer so a single draw call covers it; we set color uniforms per-pair.
        var axes = new float[]
        {
            0,0,0,  1,0,0,
            0,0,0, -1,0,0,
            0,0,0,  0,1,0,
            0,0,0,  0,-1,0,
            0,0,0,  0,0,1,
            0,0,0,  0,0,-1,
        };
        _gizmoVertexCount = axes.Length / 3;

        _gizmoVao = gl.GenVertexArray();
        gl.BindVertexArray(_gizmoVao);
        _gizmoVbo = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _gizmoVbo);
        fixed (float* p = axes)
            gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(axes.Length * sizeof(float)), (IntPtr)p, GL_STATIC_DRAW);

        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 12, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.BindVertexArray(0);
    }

    void InitMarkersResources(GlInterface gl, string vsPreamble, string fsPreamble)
    {
        _markersProgram = CreateProgram(gl,
            vsPreamble + MarkersVertexShaderBody,
            fsPreamble + MarkersFragmentShaderBody);
        _uMarkersMvp   = gl.GetUniformLocationString(_markersProgram, "uMVP");
        _uMarkersAlpha = gl.GetUniformLocationString(_markersProgram, "uAlpha");

        // Vertex layout: pos.xyz | color.rgb (6 floats per vertex, 24 bytes stride)
        _markersVao = gl.GenVertexArray();
        gl.BindVertexArray(_markersVao);
        _markersVbo = gl.GenBuffer();
        gl.BindBuffer(GL_ARRAY_BUFFER, _markersVbo);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 24, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, 24, new IntPtr(12));
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (!_glInitialized) return;

        // Notify hosts first so they drop their GL-bound caches before tear-down.
        GlContextLost?.Invoke();

        _pendingCapture?.TrySetException(new InvalidOperationException("GL context lost"));
        _pendingCapture = null;

        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, 0);
        gl.BindVertexArray(0);
        gl.UseProgram(0);

        gl.DeleteBuffer(_vbo);
        gl.DeleteBuffer(_ebo);
        gl.DeleteVertexArray(_vao);
        gl.DeleteProgram(_program);

        gl.DeleteBuffer(_gridVbo);
        gl.DeleteVertexArray(_gridVao);
        gl.DeleteProgram(_gridProgram);

        gl.DeleteBuffer(_markersVbo);
        gl.DeleteVertexArray(_markersVao);
        gl.DeleteProgram(_markersProgram);

        gl.DeleteProgram(_texturedProgram);

        gl.DeleteBuffer(_gizmoVbo);
        gl.DeleteVertexArray(_gizmoVao);
        gl.DeleteProgram(_gizmoProgram);

        // Handles in the host's LRU cache mirror these but are invalid once the context dies.
        if (_activeTextures != null)
        {
            _activeTextures.DisposeGl(h => gl.DeleteTexture(h));
            _activeTextures = null;
        }

        _glInitialized = false;
        _meshDirty = true; // force re-upload when reattached
        _markersDirty = true;
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        // Drain queued GL-thread actions (texture uploads, handle deletions) before doing anything else.
        while (_glActionQueue.TryDequeue(out var pending))
            pending(gl);

        // Bind Avalonia's framebuffer
        gl.BindFramebuffer(GL_FRAMEBUFFER, fb);

        // Viewport with DPI scaling
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = (int)(Bounds.Width * scaling);
        int h = (int)(Bounds.Height * scaling);
        if (w <= 0 || h <= 0) return;

        UpdateGizmoAnimation();

        gl.Viewport(0, 0, w, h);

        gl.ClearColor(0.04f, 0.04f, 0.04f, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

        // Compute matrices early so the grid pass (which runs before the mesh) can use them.
        // GetViewMatrix returns eye position to avoid recomputing for the light direction.
        float aspect = (float)w / h;
        var view = _camera.GetViewMatrix(out var eye);
        var proj = _camera.GetProjectionMatrix(aspect);
        var mvp = view * proj;

        // Grid draws first so the mesh occludes it.
        DrawGroundGrid(gl, mvp);

        var mesh = _meshData;
        if (mesh == null) return;

        gl.Enable(GL_DEPTH_TEST);

        gl.BindVertexArray(_vao);

        EnsureMeshUploaded(gl, mesh);

        // Light direction follows camera so the visible side is always well-lit
        var target = new Vector3(_camera.TargetX, _camera.TargetY, _camera.TargetZ);
        var lightDir = Vector3.Normalize(eye - target);

        DrawMeshPass(gl, mesh, mvp, lightDir);

        gl.BindVertexArray(0);
        gl.UseProgram(0);
        gl.Disable(GL_DEPTH_TEST);

        DrawMarkers(gl, mvp);
        ProjectAndEmitMarkers(mvp, scaling, w, h);
        DrawGizmo(gl, w, h, scaling);
        ProjectAndEmitGizmoLabels(scaling, w, h);
    }

    unsafe void EnsureMeshUploaded(GlInterface gl, PreviewMeshData mesh)
    {
        if (!_meshDirty) return;
        _meshDirty = false;

        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        fixed (float* ptr = mesh.Vertices)
            gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(mesh.Vertices.Length * sizeof(float)), (IntPtr)ptr, GL_STATIC_DRAW);

        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _ebo);
        fixed (uint* ptr = mesh.Indices)
            gl.BufferData(GL_ELEMENT_ARRAY_BUFFER, (IntPtr)(mesh.Indices.Length * sizeof(uint)), (IntPtr)ptr, GL_STATIC_DRAW);
    }

    unsafe void DrawMeshPass(GlInterface gl, PreviewMeshData mesh, in Matrix4x4 mvp, Vector3 lightDir)
    {
        // Snapshot once; the UI thread can null _activeTextures between this read and the deref below.
        var activeTextures = _activeTextures;
        bool textured = _useTextured && activeTextures != null;

        // System.Numerics rows reinterpreted as GLSL columns IS the transpose GLSL expects,
        // so pass transpose=false to glUniformMatrix4fv throughout.
        if (textured)
        {
            gl.UseProgram(_texturedProgram);
            var mvpCopy = mvp;
            gl.UniformMatrix4fv(_uTexMvp, 1, false, &mvpCopy.M11);
            if (_glUniform3f != null) _glUniform3f(_uTexLightDir, lightDir.X, lightDir.Y, lightDir.Z);
            gl.Uniform1i(_uTexBaseSampler, 0);
            gl.Uniform1i(_uTexNormalSampler, 1);
        }
        else
        {
            BindSolidProgram(gl, mvp, lightDir);
        }

        // In textured mode, fall back to solid for groups with no basecolor so geometry never vanishes.
        bool solidProgramActive = !textured;
        for (int g = 0; g < mesh.DrawGroups.Length; g++)
        {
            var (offset, count) = mesh.DrawGroups[g];

            if (textured && activeTextures!.MeshGroupBindings.TryGetValue(g, out var binding) && binding.BaseColor.HasValue)
            {
                if (solidProgramActive)
                {
                    gl.UseProgram(_texturedProgram);
                    solidProgramActive = false;
                }
                gl.ActiveTexture(GL_TEXTURE0);
                gl.BindTexture(GL_TEXTURE_2D, binding.BaseColor.Value);
                gl.ActiveTexture(GL_TEXTURE1);
                gl.BindTexture(GL_TEXTURE_2D, binding.Normal ?? binding.BaseColor.Value);
                gl.DrawElements(GL_TRIANGLES, count, GL_UNSIGNED_INT, (IntPtr)(offset * sizeof(uint)));
            }
            else
            {
                if (!solidProgramActive)
                {
                    BindSolidProgram(gl, mvp, lightDir);
                    solidProgramActive = true;
                }
                gl.DrawElements(GL_TRIANGLES, count, GL_UNSIGNED_INT, (IntPtr)(offset * sizeof(uint)));
            }
        }
    }

    unsafe void BindSolidProgram(GlInterface gl, in Matrix4x4 mvp, Vector3 lightDir)
    {
        gl.UseProgram(_program);
        var mvpCopy = mvp;
        gl.UniformMatrix4fv(_uMvp, 1, false, &mvpCopy.M11);
        if (_glUniform3f != null)
        {
            _glUniform3f(_uLightDir, lightDir.X, lightDir.Y, lightDir.Z);
            _glUniform3f(_uColor, 0.75f, 0.75f, 0.75f);
        }
    }

    unsafe void DrawGroundGrid(GlInterface gl, in Matrix4x4 mvp)
    {
        if (!_showGroundGrid) return;

        gl.UseProgram(_gridProgram);
        var mvpCopy = mvp;
        gl.UniformMatrix4fv(_uGridMvp, 1, false, &mvpCopy.M11);

        // Solid dim gray; the mesh occludes it via depth testing.
        if (_glUniform4f != null)
            _glUniform4f(_uGridColor, 0.20f, 0.20f, 0.20f, 1.0f);

        // Render lines under the mesh: enable depth test, write depth so the mesh occludes.
        gl.Enable(GL_DEPTH_TEST);
        gl.DepthMask(1);

        gl.BindVertexArray(_gridVao);
        gl.DrawArrays(GL_LINES, 0, _gridVertexCount);
        gl.BindVertexArray(0);

        gl.UseProgram(0);
    }

    // Per-vertex attachment-axis colors: red/green/blue at 6 vertices per marker.
    static readonly (float r, float g, float b)[] _attachmentSegmentColors =
    {
        (1.0f, 0.20f, 0.20f), (0.20f, 1.0f, 0.20f), (0.30f, 0.50f, 1.0f),
    };
    static readonly (float r, float g, float b)[] _impactSegmentColors =
    {
        (0.7f, 0.4f, 0.4f), (0.4f, 0.7f, 0.4f), (0.4f, 0.5f, 0.7f),
    };

    unsafe void EnsureMarkersUploaded(GlInterface gl)
    {
        if (!_markersDirty) return;
        _markersDirty = false;

        var mesh = _meshData;
        if (mesh == null)
        {
            _markersAttachVertexCount = 0;
            _markersImpactVertexCount = 0;
            return;
        }

        float markerSize = MathF.Max(mesh.Radius * 0.04f, 0.05f);
        float impactSize = markerSize * 0.5f;

        int attachVertices = mesh.Attachments.Length * 6; // 3 segments * 2 verts
        int impactVertices = mesh.ImpactPoints.Length * 6;
        _markersAttachVertexCount = attachVertices;
        _markersImpactVertexCount = impactVertices;
        if (attachVertices + impactVertices == 0) return;

        // 6 floats per vertex: pos.xyz + color.rgb
        const int floatsPerVertex = 6;
        int totalFloats = (attachVertices + impactVertices) * floatsPerVertex;
        var heap = System.Buffers.ArrayPool<float>.Shared.Rent(totalFloats);
        try
        {
            var verts = heap.AsSpan(0, totalFloats);
            int vi = 0;

            for (int i = 0; i < mesh.Attachments.Length; i++)
            {
                var m = mesh.Attachments[i];
                var p = m.Position;
                WriteSegment(verts, ref vi, p, p + m.AxisX * markerSize, _attachmentSegmentColors[0]);
                WriteSegment(verts, ref vi, p, p + m.AxisY * markerSize, _attachmentSegmentColors[1]);
                WriteSegment(verts, ref vi, p, p + m.AxisZ * markerSize, _attachmentSegmentColors[2]);
            }
            for (int i = 0; i < mesh.ImpactPoints.Length; i++)
            {
                var m = mesh.ImpactPoints[i];
                var p = m.Position;
                WriteSegment(verts, ref vi, p, p + m.AxisX * impactSize, _impactSegmentColors[0]);
                WriteSegment(verts, ref vi, p, p + m.AxisY * impactSize, _impactSegmentColors[1]);
                WriteSegment(verts, ref vi, p, p + m.AxisZ * impactSize, _impactSegmentColors[2]);
            }

            gl.BindVertexArray(_markersVao);
            gl.BindBuffer(GL_ARRAY_BUFFER, _markersVbo);
            fixed (float* p = verts)
                gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(totalFloats * sizeof(float)), (IntPtr)p, GL_STATIC_DRAW);
            gl.BindVertexArray(0);
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(heap);
        }
    }

    static void WriteSegment(Span<float> verts, ref int vi, Vector3 a, Vector3 b, (float r, float g, float b) c)
    {
        verts[vi++] = a.X; verts[vi++] = a.Y; verts[vi++] = a.Z;
        verts[vi++] = c.r; verts[vi++] = c.g; verts[vi++] = c.b;
        verts[vi++] = b.X; verts[vi++] = b.Y; verts[vi++] = b.Z;
        verts[vi++] = c.r; verts[vi++] = c.g; verts[vi++] = c.b;
    }

    unsafe void DrawMarkers(GlInterface gl, in Matrix4x4 mvp)
    {
        var mesh = _meshData;
        if (mesh == null) return;
        if (mesh.Attachments.Length == 0 && mesh.ImpactPoints.Length == 0) return;
        if (!_showMarkers) return;

        EnsureMarkersUploaded(gl);
        if (_markersAttachVertexCount + _markersImpactVertexCount == 0) return;

        gl.UseProgram(_markersProgram);
        var mvpCopy = mvp;
        gl.UniformMatrix4fv(_uMarkersMvp, 1, false, &mvpCopy.M11);

        gl.BindVertexArray(_markersVao);
        if (_glLineWidth != null) _glLineWidth(2.0f);

        // Two-pass: visible parts first (depth LESS, alpha 1.0), then occluded parts (depth GREATER, alpha 0.7) with blending.
        gl.Enable(GL_DEPTH_TEST);
        gl.DepthMask(0);

        int totalVerts = _markersAttachVertexCount + _markersImpactVertexCount;

        // Pass 1: visible
        gl.DepthFunc(GL_LESS);
        gl.Disable(GL_BLEND);
        gl.Uniform1f(_uMarkersAlpha, 1.0f);
        gl.DrawArrays(GL_LINES, 0, totalVerts);

        // Pass 2: occluded
        gl.DepthFunc(GL_GREATER);
        gl.Enable(GL_BLEND);
        if (_glBlendFunc != null) _glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
        gl.Uniform1f(_uMarkersAlpha, 0.7f);
        gl.DrawArrays(GL_LINES, 0, totalVerts);

        // Restore default state.
        gl.DepthFunc(GL_LESS);
        if (_glLineWidth != null) _glLineWidth(1.0f);
        gl.DepthMask(1);
        gl.Disable(GL_DEPTH_TEST);
        gl.Disable(GL_BLEND);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    void ProjectAndEmitGizmoLabels(double scaling, int viewportW, int viewportH)
    {
        if (GizmoLabelsProjected == null) return;
        _gizmoLabelBuffer.Clear();

        // Logical pixels (Avalonia coords). Mirrors the math in HitTestGizmo.
        double size   = GizmoSizePx;
        double margin = GizmoMarginPx;
        double cx = (viewportW / scaling) - size * 0.5 - margin;
        double cy = margin + size * 0.5;
        double r  = size * 0.5 - 4;

        var rot = GetCameraViewRotation();

        foreach (var (axisIdx, letter, ax, ay, az) in _gizmoPositiveAxes)
        {
            float vx = ax * rot.M11 + ay * rot.M21 + az * rot.M31;
            float vy = ax * rot.M12 + ay * rot.M22 + az * rot.M32;
            double sx = cx + vx * r;
            double sy = cy - vy * r;
            bool hovered = _hoveredGizmoAxis == axisIdx;
            _gizmoLabelBuffer.Add(new GizmoLabel(axisIdx, letter, sx, sy, hovered));
        }
        GizmoLabelsProjected.Invoke(_gizmoLabelBuffer);
    }

    unsafe void ProjectAndEmitMarkers(in System.Numerics.Matrix4x4 mvp, double scaling, int viewportW, int viewportH)
    {
        if (MarkersProjected == null) return;
        var mesh = _meshData;
        _markerLabelBuffer.Clear();
        if (mesh == null || !_showMarkers)
        {
            MarkersProjected.Invoke(_markerLabelBuffer);
            return;
        }

        // Pass 1: project to screen, capture NDC depth and pixel coords for inside markers.
        int totalMarkers = mesh.Attachments.Length + mesh.ImpactPoints.Length;
        Span<float> markerDepths = stackalloc float[Math.Min(Math.Max(totalMarkers, 1), 256)];
        Span<int> markerPx = stackalloc int[markerDepths.Length];
        Span<int> markerPy = stackalloc int[markerDepths.Length];
        int markerIdx = 0;

        foreach (var m in mesh.Attachments)
        {
            var p = new System.Numerics.Vector4(m.Position, 1.0f);
            var clip = System.Numerics.Vector4.Transform(p, mvp);
            if (clip.W <= 0)
            {
                _markerLabelBuffer.Add(new MarkerLabel(m.Name, 0, 0, false, false));
                if (markerIdx < markerDepths.Length) { markerDepths[markerIdx] = 1.0f; markerPx[markerIdx] = -1; markerPy[markerIdx] = -1; markerIdx++; }
                continue;
            }
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float ndcZ = clip.Z / clip.W;
            bool inside = ndcX >= -1 && ndcX <= 1 && ndcY >= -1 && ndcY <= 1;
            double sx = (ndcX * 0.5 + 0.5) * (viewportW / scaling);
            double sy = (1.0 - (ndcY * 0.5 + 0.5)) * (viewportH / scaling);
            // Pixel position in framebuffer coords (origin at bottom-left, full-resolution px not logical).
            int px = (int)((ndcX * 0.5 + 0.5) * viewportW);
            int py = (int)((ndcY * 0.5 + 0.5) * viewportH);
            _markerLabelBuffer.Add(new MarkerLabel(m.Name, sx, sy, inside, false));
            if (markerIdx < markerDepths.Length) { markerDepths[markerIdx] = ndcZ * 0.5f + 0.5f; markerPx[markerIdx] = inside ? px : -1; markerPy[markerIdx] = inside ? py : -1; markerIdx++; }
        }
        foreach (var m in mesh.ImpactPoints)
        {
            var p = new System.Numerics.Vector4(m.Position, 1.0f);
            var clip = System.Numerics.Vector4.Transform(p, mvp);
            if (clip.W <= 0)
            {
                _markerLabelBuffer.Add(new MarkerLabel(m.Name, 0, 0, false, false));
                if (markerIdx < markerDepths.Length) { markerDepths[markerIdx] = 1.0f; markerPx[markerIdx] = -1; markerPy[markerIdx] = -1; markerIdx++; }
                continue;
            }
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float ndcZ = clip.Z / clip.W;
            bool inside = ndcX >= -1 && ndcX <= 1 && ndcY >= -1 && ndcY <= 1;
            double sx = (ndcX * 0.5 + 0.5) * (viewportW / scaling);
            double sy = (1.0 - (ndcY * 0.5 + 0.5)) * (viewportH / scaling);
            int px = (int)((ndcX * 0.5 + 0.5) * viewportW);
            int py = (int)((ndcY * 0.5 + 0.5) * viewportH);
            _markerLabelBuffer.Add(new MarkerLabel(m.Name, sx, sy, inside, false));
            if (markerIdx < markerDepths.Length) { markerDepths[markerIdx] = ndcZ * 0.5f + 0.5f; markerPx[markerIdx] = inside ? px : -1; markerPy[markerIdx] = inside ? py : -1; markerIdx++; }
        }

        // Pass 2: depth-buffer readback per inside marker to determine occlusion.
        // Skip while the camera is moving (drag/anim) - glReadPixels stalls the GPU pipeline,
        // and stale Occluded values during a drag are imperceptible. The frame after movement
        // stops re-runs through here naturally because RequestNextFrameRendering is fired.
        bool cameraMoving = _leftDragging || _rightDragging || _animActive;
        if (_glReadPixels != null && !cameraMoving)
        {
            for (int i = 0; i < markerIdx && i < _markerLabelBuffer.Count; i++)
            {
                if (markerPx[i] < 0) continue;
                float depth = 0;
                _glReadPixels(markerPx[i], markerPy[i], 1, 1, GL_DEPTH_COMPONENT, GL_FLOAT_TYPE, &depth);
                // If the depth buffer at this pixel is closer than the marker, the marker is behind something.
                bool occluded = depth + 0.0001f < markerDepths[i];
                var existing = _markerLabelBuffer[i];
                _markerLabelBuffer[i] = existing with { Occluded = occluded };
            }
        }

        MarkersProjected.Invoke(_markerLabelBuffer);
    }

    Matrix4x4 GetCameraViewRotation()
    {
        // Strip translation from the view matrix to get pure rotation.
        var view = _camera.GetViewMatrix();
        view.M41 = 0; view.M42 = 0; view.M43 = 0;
        return view;
    }

    unsafe void DrawGizmo(GlInterface gl, int viewportW, int viewportH, double scaling)
    {
        int gizmoSize = (int)(GizmoSizePx * scaling);
        int margin    = (int)(GizmoMarginPx * scaling);
        int gx = viewportW - gizmoSize - margin;
        int gy = viewportH - gizmoSize - margin;

        gl.Viewport(gx, gy, gizmoSize, gizmoSize);
        gl.Clear(GL_DEPTH_BUFFER_BIT);
        gl.Disable(GL_DEPTH_TEST);

        gl.UseProgram(_gizmoProgram);

        var rot = GetCameraViewRotation();
        // Pass the upper 3x3 of the view rotation as a mat3.
        Span<float> m3 = stackalloc float[9]
        {
            rot.M11, rot.M12, rot.M13,
            rot.M21, rot.M22, rot.M23,
            rot.M31, rot.M32, rot.M33
        };
        if (_glUniformMatrix3fv != null)
        {
            fixed (float* mp = m3)
                _glUniformMatrix3fv(_uGizmoView, 1, 0, mp);
        }

        gl.BindVertexArray(_gizmoVao);

        if (_glLineWidth != null) _glLineWidth(2.0f);
        if (_glUniform3f != null)
        {
            for (int i = 0; i < 6; i++)
            {
                var c = _gizmoAxisColors[i];
                if (i == _hoveredGizmoAxis)
                {
                    c.r = MathF.Min(1.0f, c.r * 1.3f);
                    c.g = MathF.Min(1.0f, c.g * 1.3f);
                    c.b = MathF.Min(1.0f, c.b * 1.3f);
                }
                _glUniform3f(_uGizmoColor, c.r, c.g, c.b);
                gl.DrawArrays(GL_LINES, i * 2, 2);
            }
        }
        if (_glLineWidth != null) _glLineWidth(1.0f);

        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    // Six gizmo axis end points in local space; stored once because HitTestGizmo runs on every pointer move.
    static readonly (float x, float y, float z)[] _gizmoAxisEnds =
    {
        ( 1, 0, 0), (-1, 0, 0),
        ( 0, 1, 0), ( 0,-1, 0),
        ( 0, 0, 1), ( 0, 0,-1),
    };

    int HitTestGizmo(Point screenPos)
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0) return -1;

        double margin = GizmoMarginPx;
        double size   = GizmoSizePx;
        // Gizmo occupies a square in the top-right. In Avalonia coords, top-left is (0,0).
        double left = w - size - margin;
        double top  = margin;
        if (screenPos.X < left || screenPos.X > left + size) return -1;
        if (screenPos.Y < top  || screenPos.Y > top  + size) return -1;

        // Project the six axis end points into the gizmo's local screen space.
        // Local space: x right, y up, origin at gizmo center.
        var rot = GetCameraViewRotation();

        double cx = left + size * 0.5;
        double cy = top  + size * 0.5;
        double r  = size * 0.5 - 4;

        int best = -1;
        double bestDistSq = 18 * 18; // 18 px hit radius - widened so axis lines are easier to click

        for (int i = 0; i < 6; i++)
        {
            var a = _gizmoAxisEnds[i];
            // (rot * a) using the 3x3 rotation: row-vector * row-major matrix
            float vx = a.x * rot.M11 + a.y * rot.M21 + a.z * rot.M31;
            float vy = a.x * rot.M12 + a.y * rot.M22 + a.z * rot.M32;
            // Clip y is flipped to screen y in Avalonia.
            double sx = cx + vx * r;
            double sy = cy - vy * r;
            double dx = screenPos.X - sx;
            double dy = screenPos.Y - sy;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestDistSq)
            {
                bestDistSq = d2;
                best = i;
            }
        }
        return best;
    }

    static (float Azimuth, float Elevation) GetGizmoTarget(int axis) => axis switch
    {
        // Azimuth = 0 looks down -Z (camera at +Z). Each axis-end click positions
        // the camera at that axis end, looking toward the origin.
        0 => (90f, 0f),    // +X
        1 => (-90f, 0f),   // -X
        2 => (0f, 89f),    // +Y (clamped to elevation limit)
        3 => (0f, -89f),   // -Y
        4 => (0f, 0f),     // +Z
        5 => (180f, 0f),   // -Z
        _ => (0f, 0f)
    };

    static float ShortestAzimuthDelta(float from, float to)
    {
        float d = (to - from) % 360f;
        if (d > 180f) d -= 360f;
        if (d < -180f) d += 360f;
        return d;
    }

    void StartGizmoTween(int axis)
    {
        var (targetAz, targetEl) = GetGizmoTarget(axis);
        _animStartAz = _camera.Azimuth;
        _animStartEl = _camera.Elevation;
        _animEndAz   = _animStartAz + ShortestAzimuthDelta(_animStartAz, targetAz);
        _animEndEl   = targetEl;
        if (!_animClock.IsRunning) _animClock.Start();
        _animStartTimeMs = _animClock.Elapsed.TotalMilliseconds;
        _animActive = true;
        RequestNextFrameRendering();
    }

    void UpdateGizmoAnimation()
    {
        if (!_animActive) return;
        double now = _animClock.Elapsed.TotalMilliseconds;
        double t = (now - _animStartTimeMs) / AnimDurationMs;
        if (t >= 1.0)
        {
            _camera.Azimuth = _animEndAz;
            _camera.Elevation = _animEndEl;
            _animActive = false;
            return;
        }
        // Ease in / out (quadratic)
        float eased = (float)(t < 0.5
            ? 2.0 * t * t
            : 1.0 - System.Math.Pow(-2.0 * t + 2.0, 2.0) / 2.0);
        _camera.Azimuth   = _animStartAz + (_animEndAz - _animStartAz) * eased;
        _camera.Elevation = _animStartEl + (_animEndEl - _animStartEl) * eased;
        RequestNextFrameRendering();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsLeftButtonPressed)
        {
            int axis = HitTestGizmo(pos);
            if (axis >= 0 && !_animActive)
            {
                StartGizmoTween(axis);
                e.Handled = true;
                return;
            }
        }

        _lastPointerPos = pos;
        if (props.IsLeftButtonPressed) _leftDragging = true;
        if (props.IsRightButtonPressed) _rightDragging = true;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) _leftDragging = false;
        if (!props.IsRightButtonPressed) _rightDragging = false;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (!_leftDragging && !_rightDragging)
        {
            int axis = _animActive ? -1 : HitTestGizmo(pos);
            if (axis != _hoveredGizmoAxis)
            {
                _hoveredGizmoAxis = axis;
                Cursor = axis >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                RequestNextFrameRendering();
            }
        }

        float dx = (float)(pos.X - _lastPointerPos.X);
        float dy = (float)(pos.Y - _lastPointerPos.Y);
        _lastPointerPos = pos;

        if (_leftDragging)
        {
            // Manual drag wins over animation
            _animActive = false;
            _camera.Rotate(-dx * 0.3f, dy * 0.3f);
            RequestNextFrameRendering();
            e.Handled = true;
        }
        else if (_rightDragging)
        {
            _animActive = false;
            _camera.Pan(-dx * 0.003f, dy * 0.003f);
            RequestNextFrameRendering();
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _camera.Zoom((float)e.Delta.Y);
        RequestNextFrameRendering();
        e.Handled = true;
    }

    static int CreateProgram(GlInterface gl, string vertexSrc, string fragmentSrc)
        => GlShaderHelpers.CreateProgram(gl, vertexSrc, fragmentSrc);
}
