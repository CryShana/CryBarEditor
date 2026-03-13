using System;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using CryBarEditor.Classes;
using static Avalonia.OpenGL.GlConsts;

namespace CryBarEditor.Controls;

public class GlPreviewControl : OpenGlControlBase
{
    readonly OrbitCamera _camera = new();
    PreviewMeshData? _meshData;
    bool _meshDirty;

    // GL resources
    int _program;
    int _vao, _vbo, _ebo;
    int _uMvp, _uNormalMatrix, _uLightDir, _uColor;
    bool _glInitialized;

    // Function pointer for glUniform3f (not exposed by Avalonia's GlInterface)
    unsafe delegate* unmanaged<int, float, float, float, void> _glUniform3f;

    // Mouse tracking
    Point _lastPointerPos;
    bool _leftDragging, _rightDragging;

    // Shader bodies - version/precision preamble prepended at runtime
    const string VertexShaderBody = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uMVP;
        uniform mat4 uNormalMatrix;
        out vec3 vNormal;
        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vNormal = normalize(mat3(uNormalMatrix) * aNormal);
        }
        """;

    const string FragmentShaderBody = """
        in vec3 vNormal;
        uniform vec3 uLightDir;
        uniform vec3 uColor;
        out vec4 FragColor;
        void main() {
            float diff = max(dot(normalize(vNormal), uLightDir), 0.0);
            vec3 col = uColor * (0.15 + 0.85 * diff);
            FragColor = vec4(col, 1.0);
        }
        """;

    public void LoadMesh(PreviewMeshData meshData)
    {
        _meshData = meshData;
        _meshDirty = true;
        _camera.FitToSphere(meshData.CenterX, meshData.CenterY, meshData.CenterZ, meshData.Radius);
        RequestNextFrameRendering();
    }

    public void ClearMesh()
    {
        _meshData = null;
        _meshDirty = true;
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

        // Get proc address for glUniform3f (not in Avalonia's GlInterface)
        _glUniform3f = (delegate* unmanaged<int, float, float, float, void>)gl.GetProcAddress("glUniform3f");

        _program = CreateProgram(gl, vsPreamble + VertexShaderBody, fsPreamble + FragmentShaderBody);
        _uMvp = gl.GetUniformLocationString(_program, "uMVP");
        _uNormalMatrix = gl.GetUniformLocationString(_program, "uNormalMatrix");
        _uLightDir = gl.GetUniformLocationString(_program, "uLightDir");
        _uColor = gl.GetUniformLocationString(_program, "uColor");

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();

        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _ebo);

        // layout 0 = pos (3 floats), offset 0, stride 32
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 32, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        // layout 1 = normal (3 floats), offset 12, stride 32
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, 32, new IntPtr(12));
        gl.EnableVertexAttribArray(1);
        // layout 2 = uv (2 floats), offset 24, stride 32
        gl.VertexAttribPointer(2, 2, GL_FLOAT, 0, 32, new IntPtr(24));
        gl.EnableVertexAttribArray(2);

        gl.BindVertexArray(0);

        _glInitialized = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (!_glInitialized) return;

        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, 0);
        gl.BindVertexArray(0);
        gl.UseProgram(0);

        gl.DeleteBuffer(_vbo);
        gl.DeleteBuffer(_ebo);
        gl.DeleteVertexArray(_vao);
        gl.DeleteProgram(_program);

        _glInitialized = false;
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        // Bind Avalonia's framebuffer
        gl.BindFramebuffer(GL_FRAMEBUFFER, fb);

        // Viewport with DPI scaling
        var scaling = VisualRoot?.RenderScaling ?? 1.0;
        int w = (int)(Bounds.Width * scaling);
        int h = (int)(Bounds.Height * scaling);
        if (w <= 0 || h <= 0) return;
        gl.Viewport(0, 0, w, h);

        gl.ClearColor(0.04f, 0.04f, 0.04f, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

        var mesh = _meshData;
        if (mesh == null) return;

        gl.Enable(GL_DEPTH_TEST);

        gl.BindVertexArray(_vao);

        // Upload mesh data if dirty
        if (_meshDirty)
        {
            _meshDirty = false;
            gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
            fixed (float* ptr = mesh.Vertices)
                gl.BufferData(GL_ARRAY_BUFFER, (IntPtr)(mesh.Vertices.Length * sizeof(float)), (IntPtr)ptr, GL_STATIC_DRAW);

            gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _ebo);
            fixed (ushort* ptr = mesh.Indices)
                gl.BufferData(GL_ELEMENT_ARRAY_BUFFER, (IntPtr)(mesh.Indices.Length * sizeof(ushort)), (IntPtr)ptr, GL_STATIC_DRAW);
        }

        gl.UseProgram(_program);

        // Compute matrices
        float aspect = (float)w / h;
        var view = _camera.GetViewMatrix();
        var proj = _camera.GetProjectionMatrix(aspect);
        var mvp = view * proj;

        // Normal matrix = inverse-transpose of view.
        // Transpose(Inverse(view)) for row-major, then Transpose again for column-major upload.
        // Net result: upload Inverse(view) directly (the two transposes cancel).
        Matrix4x4.Invert(view, out var viewInv);

        // System.Numerics is row-major, row-vector: v' = v * MVP
        // GLSL is column-major, column-vector: v' = MVP * v
        // Passing row-major data to glUniformMatrix4fv(transpose=false) reinterprets rows as
        // columns, which is the exact transpose needed for the convention switch.
        gl.UniformMatrix4fv(_uMvp, 1, false, &mvp.M11);

        // Normal matrix = inverse-transpose(view). By the same row→column reinterpretation,
        // passing inverse(view) raw gives GLSL the inverse-transpose.
        gl.UniformMatrix4fv(_uNormalMatrix, 1, false, &viewInv.M11);

        // Light direction (normalized, world space) - top-right-front
        if (_glUniform3f != null)
        {
            _glUniform3f(_uLightDir, 0.3f, 0.8f, 0.5f);
            _glUniform3f(_uColor, 0.75f, 0.75f, 0.75f);
        }

        // Draw all mesh groups
        foreach (var (offset, count) in mesh.DrawGroups)
        {
            gl.DrawElements(GL_TRIANGLES, count, GL_UNSIGNED_SHORT, (IntPtr)(offset * sizeof(ushort)));
        }

        gl.BindVertexArray(0);
        gl.UseProgram(0);
        gl.Disable(GL_DEPTH_TEST);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _lastPointerPos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
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
        float dx = (float)(pos.X - _lastPointerPos.X);
        float dy = (float)(pos.Y - _lastPointerPos.Y);
        _lastPointerPos = pos;

        if (_leftDragging)
        {
            _camera.Rotate(dx * 0.3f, dy * 0.3f);
            RequestNextFrameRendering();
            e.Handled = true;
        }
        else if (_rightDragging)
        {
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

    #region GL Helpers

    static int CreateProgram(GlInterface gl, string vertexSrc, string fragmentSrc)
    {
        int vs = CompileShader(gl, GL_VERTEX_SHADER, vertexSrc);
        int fs = CompileShader(gl, GL_FRAGMENT_SHADER, fragmentSrc);

        int program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);

        // Delete shader objects (they stay attached until program is deleted)
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        return program;
    }

    static int CompileShader(GlInterface gl, int type, string source)
    {
        int shader = gl.CreateShader(type);
        gl.ShaderSourceString(shader, source);
        gl.CompileShader(shader);
        return shader;
    }

    #endregion
}
