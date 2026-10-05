using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using DarkColony.App.Diagnostics;
using DarkColony.Presentation;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using Size = System.Drawing.Size;

namespace DarkColony.App.Rendering;

/// <summary>
/// D3D11 presentation surface. Every frame is drawn at the logical size
/// (640x480, or a larger gameplay view) into an offscreen target, which one
/// quad then places on the back buffer by <see cref="DisplayLayout"/>: point
/// sampled when the scale is a whole number, sharp-bilinear otherwise.
/// Sprites are drawn as a batch: images up to <see cref="LargestAtlasImage"/>
/// pixels share atlas pages (<see cref="AtlasPacker"/>), the frame's quads go
/// to the GPU in one vertex buffer, and each run of quads from one texture is
/// one draw call. The
/// back buffer always matches the client area in physical pixels, so Windows
/// never stretches the window. Mouse events are raised in logical
/// coordinates. The callback's scratch <see cref="Graphics"/> serves only
/// isolated fallback code; it is never presented.
/// </summary>
public sealed class Direct3DSurface : Control
{
    private readonly Action<Graphics, GameCanvas> _renderFrame;
    private readonly GameCanvas _canvas = new();
    private Bitmap? _legacyScratch;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIFactory2? _factory;
    private IDXGISwapChain1? _swapChain;
    private SwapChainFlags _swapChainFlags;
    private IntPtr _frameLatencyHandle;
    private double _lastRenderedAt = double.NegativeInfinity;
    private bool _tearingSupported;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _backBufferView;
    private Size _backBufferSize;
    private ID3D11Texture2D? _nativeTarget;
    private ID3D11RenderTargetView? _nativeTargetView;
    private ID3D11ShaderResourceView? _nativeTargetResource;
    private Size _nativeTargetSize;
    private ID3D11Buffer? _quadVertices;
    private ID3D11Buffer? _scalingConstants;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11PixelShader? _sharpPixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11SamplerState? _pointSampler;
    private ID3D11SamplerState? _linearSampler;
    private ID3D11BlendState? _alphaBlend;
    private const int AtlasPageSize = 2048;
    private const int LargestAtlasImage = 256;
    private readonly AtlasPacker _atlas = new(AtlasPageSize, LargestAtlasImage);
    private readonly List<GpuTexture> _atlasPages = [];
    // Atlas area given back by released images; past half a page the atlas is
    // repacked from scratch (the images upload again as they are next drawn).
    private long _atlasReleasedArea;
    private readonly Dictionary<GpuImage, GpuPlacement> _gpuImages = [];
    private readonly List<GpuTexture> _transientGpuImages = [];
    private ID3D11Buffer? _batchVertices;
    private ID3D11Buffer? _batchIndices;
    private ID3D11Buffer? _batchConstants;
    private int _batchQuadCapacity;
    private ID3D11VertexShader? _batchVertexShader;
    private ID3D11InputLayout? _batchInputLayout;
    private BatchVertex[] _batchQuads = new BatchVertex[4 * 1024];
    private readonly List<(ID3D11ShaderResourceView View, int FirstQuad, int QuadCount)> _batchRuns = [];
    private Size _logicalSize = DisplaySettings.ClassicSize;
    private ScaleMode _scaleMode = ScaleMode.Integer;

    public Direct3DSurface(Action<Graphics, GameCanvas> renderFrame)
    {
        _renderFrame = renderFrame;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        PictureLayout = DisplayLayout.Compute(_logicalSize, new Size(640, 480), _scaleMode);
    }

    /// <summary>Where the logical picture lands on the client area, and the pointer mapping.</summary>
    public DisplayLayout PictureLayout { get; private set; }

    /// <summary>Raised when <see cref="PictureLayout"/> changes.</summary>
    public event EventHandler? PictureLayoutChanged;

    /// <summary>The size the frame is drawn at: 640x480, or the gameplay view.</summary>
    public Size LogicalSize
    {
        get => _logicalSize;
        set
        {
            if (value.Width <= 0 || value.Height <= 0) throw new ArgumentOutOfRangeException(nameof(value), value, "The logical size must not be empty.");
            if (value == _logicalSize) return;
            _logicalSize = value;
            UpdateLayout();
        }
    }

    public ScaleMode ScaleMode
    {
        get => _scaleMode;
        set
        {
            if (value == _scaleMode) return;
            _scaleMode = value;
            UpdateLayout();
        }
    }

    /// <summary>Waits for the vertical blank when presenting. Off presents at once, tearing where allowed.</summary>
    public bool VSync { get; set; } = true;

    /// <summary>
    /// The swap chain's frame latency object (one frame): signaled when it can
    /// take the next frame without blocking, so <see cref="GameLoop"/> waits
    /// on it before each frame. Zero before the device exists.
    /// </summary>
    public IntPtr FrameWaitHandle => _frameLatencyHandle;

    /// <summary>Live GPU objects, for the display-mode leak test.</summary>
    public string ResourceSummary() =>
        $"{_gpuImages.Count} cached textures ({_atlasPages.Count} atlas pages), back buffer {_backBufferSize.Width}x{_backBufferSize.Height}, target {_nativeTargetSize.Width}x{_nativeTargetSize.Height}";

    public void RenderAndPresent()
    {
        if (!IsHandleCreated || ClientSize.Width == 0 || ClientSize.Height == 0) return;
        FrameProfiler.BeginFrame();
        _lastRenderedAt = GameLoop.Milliseconds;
        EnsureDevice();
        EnsureBackBuffer();
        EnsureNativeTarget();

        _canvas.Reset();
        if (_legacyScratch is null || _legacyScratch.Size != _logicalSize)
        {
            _legacyScratch?.Dispose();
            _legacyScratch = new Bitmap(_logicalSize.Width, _logicalSize.Height, PixelFormat.Format32bppArgb);
        }
        var buildStarted = FrameProfiler.Begin();
        // The scratch is never presented, so it is not cleared: clearing a
        // 1920x1080 bitmap through GDI+ cost 2.5 ms a frame.
        using (var graphics = Graphics.FromImage(_legacyScratch))
            _renderFrame(graphics, _canvas);
        FrameProfiler.End(FrameProfiler.Stage.Build, buildStarted);
        FrameProfiler.Sprites(_canvas.Commands.Count + _canvas.ForegroundCommands.Count);

        try
        {
            var context = _context!;
            var submitStarted = FrameProfiler.Begin();
            context.ClearRenderTargetView(_nativeTargetView!, new Color4(0f, 0f, 0f, 1f));
            DrawBatch(_canvas.Commands, _canvas.ForegroundCommands);
            context.ClearRenderTargetView(_backBufferView!, new Color4(0f, 0f, 0f, 1f));
            var layout = PictureLayout;
            if (!layout.Destination.IsEmpty)
            {
                if (layout.PixelExact) DrawTexture(_nativeTargetResource!, _backBufferView!, layout.Destination, alphaBlend: false);
                else DrawSharpBilinear(layout);
            }
            FrameProfiler.End(FrameProfiler.Stage.Submit, submitStarted);
            var tearing = !VSync && _tearingSupported;
            var presentStarted = FrameProfiler.Begin();
            var result = _swapChain!.Present(VSync ? 1u : 0u, tearing ? PresentFlags.AllowTearing : PresentFlags.None);
            FrameProfiler.End(FrameProfiler.Stage.Present, presentStarted);
            if (result.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || result.Code == Vortice.DXGI.ResultCode.DeviceReset.Code)
            {
                // The device is gone (driver update, GPU reset). Every texture
                // is rebuilt from its GpuImage on the next frame.
                RuntimeLog.Info($"D3D11 device lost ({result.Code:X8}, removed reason {_device?.DeviceRemovedReason.Code:X8}); recreating it.");
                ReleaseDevice();
            }
            else result.CheckError();
        }
        finally
        {
            foreach (var image in _transientGpuImages)
            {
                image.View.Dispose();
                image.Texture.Dispose();
            }
            _transientGpuImages.Clear();
            FrameProfiler.EndFrame();
        }
    }

    /// <summary>
    /// Releases a long-lived decoded image whose application-side owner has
    /// discarded it (for example, terrain from a previous scenario). The
    /// normal cache remains reference-identity based for stable assets.
    /// </summary>
    public void ReleaseGpuImage(GpuImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!_gpuImages.Remove(image, out var placement)) return;
        if (placement.Owned is { } owned)
        {
            owned.View.Dispose();
            owned.Texture.Dispose();
            return;
        }
        _atlasReleasedArea += (long)image.Width * image.Height;
        if (_atlasReleasedArea > AtlasPageSize * AtlasPageSize / 2) ResetAtlas();
    }

    /// <summary>
    /// Forgets every image packed in the atlas. The pages stay; the images
    /// are packed and uploaded again from their pixels when next drawn.
    /// </summary>
    private void ResetAtlas()
    {
        foreach (var image in _gpuImages.Where(pair => pair.Value.Owned is null).Select(pair => pair.Key).ToArray()) _gpuImages.Remove(image);
        _atlas.Reset();
        _atlasReleasedArea = 0;
    }

    protected override void OnHandleDestroyed(EventArgs eventArgs)
    {
        ReleaseDevice();
        base.OnHandleDestroyed(eventArgs);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseDevice();
            _legacyScratch?.Dispose();
        }

        base.Dispose(disposing);
    }

    // The game loop draws continuously; a paint only draws when the loop has
    // not drawn lately (a modal loop, such as a dialog, holds it).
    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        if (GameLoop.Milliseconds - _lastRenderedAt < 100) return;
        RenderAndPresent();
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
    }

    protected override void OnSizeChanged(EventArgs eventArgs)
    {
        base.OnSizeChanged(eventArgs);
        UpdateLayout();
        Invalidate();
    }

    // Mouse events reach the app in logical coordinates. A point on a bar
    // maps to the nearest picture edge.
    protected override void OnMouseMove(MouseEventArgs eventArgs) => base.OnMouseMove(ToLogical(eventArgs));

    protected override void OnMouseDown(MouseEventArgs eventArgs) => base.OnMouseDown(ToLogical(eventArgs));

    protected override void OnMouseUp(MouseEventArgs eventArgs) => base.OnMouseUp(ToLogical(eventArgs));

    protected override void OnMouseClick(MouseEventArgs eventArgs) => base.OnMouseClick(ToLogical(eventArgs));

    protected override void OnMouseDoubleClick(MouseEventArgs eventArgs) => base.OnMouseDoubleClick(ToLogical(eventArgs));

    protected override void OnMouseWheel(MouseEventArgs eventArgs) => base.OnMouseWheel(ToLogical(eventArgs));

    private MouseEventArgs ToLogical(MouseEventArgs eventArgs)
    {
        var point = PictureLayout.ToLogical(eventArgs.Location);
        return new MouseEventArgs(eventArgs.Button, eventArgs.Clicks, point.X, point.Y, eventArgs.Delta);
    }

    /// <summary>The screen rectangle the picture covers, for confining the pointer.</summary>
    public Rectangle PictureScreenBounds() => RectangleToScreen(PictureLayout.Destination);

    private void UpdateLayout()
    {
        var layout = DisplayLayout.Compute(_logicalSize, ClientSize, _scaleMode);
        if (layout == PictureLayout) return;
        PictureLayout = layout;
        PictureLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureDevice()
    {
        if (_device is not null) return;

        var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels, out _device, out _context).CheckError();
        var device = _device ?? throw new InvalidOperationException("D3D11 did not return a device.");
        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
            _factory = adapter.GetParent<IDXGIFactory2>();
        using (var factory5 = _factory.QueryInterfaceOrNull<IDXGIFactory5>())
            _tearingSupported = factory5?.PresentAllowTearing == true;
        CreateSwapChain(allowTearing: _tearingSupported);
        // The app toggles fullscreen itself (Alt+Enter goes through its settings).
        _factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAltEnter).CheckError();
        RuntimeLog.Info($"D3D11 device created: feature level {device.FeatureLevel}, flip-model swap chain {_backBufferSize.Width}x{_backBufferSize.Height}, tearing {(_tearingSupported ? "supported" : "unsupported")}.");

        ReadOnlySpan<QuadVertex> vertices =
        [
            new(new Vector3(-1, 1, 0), new Vector2(0, 0)),
            new(new Vector3(1, 1, 0), new Vector2(1, 0)),
            new(new Vector3(1, -1, 0), new Vector2(1, 1)),
            new(new Vector3(-1, 1, 0), new Vector2(0, 0)),
            new(new Vector3(1, -1, 0), new Vector2(1, 1)),
            new(new Vector3(-1, -1, 0), new Vector2(0, 1)),
        ];
        _quadVertices = device.CreateBuffer(vertices, BindFlags.VertexBuffer);
        _scalingConstants = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ScalingConstants>(), BindFlags.ConstantBuffer));

        var shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "Sprite.hlsl");
        var shaderFlags = ShaderFlags.EnableStrictness;
#if DEBUG
        shaderFlags |= ShaderFlags.Debug;
#else
        shaderFlags |= ShaderFlags.OptimizationLevel3;
#endif
        var vertexBytecode = Compiler.CompileFromFile(shaderPath, "VSMain", "vs_4_0", shaderFlags);
        var pixelBytecode = Compiler.CompileFromFile(shaderPath, "PSMain", "ps_4_0", shaderFlags);
        var sharpBytecode = Compiler.CompileFromFile(shaderPath, "PSSharpBilinear", "ps_4_0", shaderFlags);
        _vertexShader = device.CreateVertexShader(vertexBytecode.Span);
        _pixelShader = device.CreatePixelShader(pixelBytecode.Span);
        _sharpPixelShader = device.CreatePixelShader(sharpBytecode.Span);
        _inputLayout = device.CreateInputLayout(QuadVertex.InputElements, vertexBytecode.Span);
        var batchBytecode = Compiler.CompileFromFile(shaderPath, "VSBatch", "vs_4_0", shaderFlags);
        _batchVertexShader = device.CreateVertexShader(batchBytecode.Span);
        _batchInputLayout = device.CreateInputLayout(BatchVertex.InputElements, batchBytecode.Span);
        _batchConstants = device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<ScalingConstants>(), BindFlags.ConstantBuffer));
        _pointSampler = device.CreateSamplerState(SamplerDescription.PointClamp);
        _linearSampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        _alphaBlend = device.CreateBlendState(BlendDescription.NonPremultiplied);
    }

    /// <summary>
    /// Creates the flip-model swap chain at the client area's size. With
    /// AllowTearing, presenting without vsync may tear instead of waiting.
    /// </summary>
    private void CreateSwapChain(bool allowTearing)
    {
        _backBufferView?.Dispose();
        _backBuffer?.Dispose();
        _backBufferView = null;
        _backBuffer = null;
        if (_swapChain is not null)
        {
            _context!.ClearState();
            _context.Flush();
            CloseFrameLatencyHandle();
            _swapChain.Dispose();
        }
        _swapChainFlags = (allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None) | SwapChainFlags.FrameLatencyWaitableObject;
        _backBufferSize = new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        _swapChain = _factory!.CreateSwapChainForHwnd(_device!, Handle, new SwapChainDescription1
        {
            Width = (uint)_backBufferSize.Width,
            Height = (uint)_backBufferSize.Height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = _swapChainFlags,
        }, new SwapChainFullscreenDescription { Windowed = true }, null);
        using (var swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>())
        {
            if (swapChain2 is not null)
            {
                swapChain2.MaximumFrameLatency = 1;
                _frameLatencyHandle = swapChain2.FrameLatencyWaitableObject;
            }
        }
        CreateBackBufferView();
    }

    private void CloseFrameLatencyHandle()
    {
        if (_frameLatencyHandle == IntPtr.Zero) return;
        CloseHandle(_frameLatencyHandle);
        _frameLatencyHandle = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Resizes the flip-model buffers to the client area.</summary>
    private void EnsureBackBuffer()
    {
        var size = new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        if (size == _backBufferSize) return;
        _backBufferView?.Dispose();
        _backBuffer?.Dispose();
        _backBufferView = null;
        _backBuffer = null;
        _context!.ClearState();
        _context.Flush();
        _swapChain!.ResizeBuffers(2, (uint)size.Width, (uint)size.Height, Format.Unknown, _swapChainFlags).CheckError();
        _backBufferSize = size;
        CreateBackBufferView();
    }

    private void CreateBackBufferView()
    {
        _backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = _device!.CreateRenderTargetView(_backBuffer);
    }

    /// <summary>(Re)creates the offscreen target at the logical size.</summary>
    private void EnsureNativeTarget()
    {
        if (_nativeTarget is not null && _nativeTargetSize == _logicalSize) return;
        _nativeTargetResource?.Dispose();
        _nativeTargetView?.Dispose();
        _nativeTarget?.Dispose();
        _nativeTarget = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_logicalSize.Width,
            Height = (uint)_logicalSize.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _nativeTargetView = _device.CreateRenderTargetView(_nativeTarget);
        _nativeTargetResource = _device.CreateShaderResourceView(_nativeTarget);
        _nativeTargetSize = _logicalSize;
    }

    /// <summary>
    /// Draws both command lists, background first, as one batch: every quad
    /// goes into one vertex buffer, and each run of quads from the same
    /// texture (an atlas page, or an image of its own) is one draw call.
    /// </summary>
    private unsafe void DrawBatch(IReadOnlyList<SpriteCommand> background, IReadOnlyList<SpriteCommand> foreground)
    {
        var context = _context!;
        EnsureBatchCapacity(background.Count + foreground.Count);
        _batchRuns.Clear();
        var quads = 0;
        foreach (var commands in (ReadOnlySpan<IReadOnlyList<SpriteCommand>>)[background, foreground])
        {
            for (var index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                var destination = command.Destination;
                if (destination.Width <= 0 || destination.Height <= 0) continue;
                var placement = GetGpuImage(command.Image);
                var vertex = quads * 4;
                var (left, top, right, bottom) = (destination.Left, destination.Top, destination.Right, destination.Bottom);
                var uv = placement.Uv;
                _batchQuads[vertex] = new BatchVertex(new Vector2(left, top), new Vector2(uv.X, uv.Y));
                _batchQuads[vertex + 1] = new BatchVertex(new Vector2(right, top), new Vector2(uv.Z, uv.Y));
                _batchQuads[vertex + 2] = new BatchVertex(new Vector2(right, bottom), new Vector2(uv.Z, uv.W));
                _batchQuads[vertex + 3] = new BatchVertex(new Vector2(left, bottom), new Vector2(uv.X, uv.W));
                if (_batchRuns.Count > 0 && ReferenceEquals(_batchRuns[^1].View, placement.View))
                    _batchRuns[^1] = _batchRuns[^1] with { QuadCount = _batchRuns[^1].QuadCount + 1 };
                else
                    _batchRuns.Add((placement.View, quads, 1));
                quads++;
            }
        }
        if (quads == 0) return;

        var mapped = context.Map(_batchVertices!, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            new ReadOnlySpan<BatchVertex>(_batchQuads, 0, quads * 4).CopyTo(new Span<BatchVertex>((void*)mapped.DataPointer, quads * 4));
        }
        finally
        {
            context.Unmap(_batchVertices!, 0);
        }
        context.UpdateSubresource(new ScalingConstants(new Vector2(_nativeTargetSize.Width, _nativeTargetSize.Height), Vector2.Zero), _batchConstants!);
        context.OMSetRenderTargets(_nativeTargetView!);
        context.RSSetViewport(new Viewport(0, 0, _nativeTargetSize.Width, _nativeTargetSize.Height));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_batchInputLayout);
        context.IASetVertexBuffer(0, _batchVertices!, BatchVertex.SizeInBytes);
        context.IASetIndexBuffer(_batchIndices, Format.R32_UInt, 0);
        context.VSSetShader(_batchVertexShader);
        context.VSSetConstantBuffer(1, _batchConstants);
        context.PSSetShader(_pixelShader);
        context.PSSetSampler(0, _pointSampler);
        context.OMSetBlendState(_alphaBlend, null, uint.MaxValue);
        foreach (var (view, firstQuad, quadCount) in _batchRuns)
        {
            context.PSSetShaderResource(0, view);
            context.DrawIndexed((uint)(quadCount * 6), (uint)(firstQuad * 6), 0);
        }
        context.PSSetShaderResource(0, default!);
        FrameProfiler.DrawCalls(_batchRuns.Count);
    }

    /// <summary>Grows the batch's buffers to hold at least <paramref name="quads"/> quads.</summary>
    private void EnsureBatchCapacity(int quads)
    {
        if (_batchVertices is not null && quads <= _batchQuadCapacity) return;
        var capacity = Math.Max(4096, _batchQuadCapacity);
        while (capacity < quads) capacity *= 2;
        _batchVertices?.Dispose();
        _batchIndices?.Dispose();
        _batchVertices = _device!.CreateBuffer(new BufferDescription((uint)(capacity * 4 * BatchVertex.SizeInBytes), BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        var indices = new uint[capacity * 6];
        for (var quad = 0; quad < capacity; quad++)
        {
            var first = (uint)(quad * 4);
            indices[quad * 6] = first;
            indices[quad * 6 + 1] = first + 1;
            indices[quad * 6 + 2] = first + 2;
            indices[quad * 6 + 3] = first;
            indices[quad * 6 + 4] = first + 2;
            indices[quad * 6 + 5] = first + 3;
        }
        _batchIndices = _device.CreateBuffer(indices, BindFlags.IndexBuffer);
        _batchQuadCapacity = capacity;
        if (_batchQuads.Length < capacity * 4) _batchQuads = new BatchVertex[capacity * 4];
    }

    /// <summary>
    /// Where an image is on the GPU, uploading it the first time: a slot in
    /// an atlas page, or a texture of its own when it is large or transient.
    /// </summary>
    private GpuPlacement GetGpuImage(GpuImage image)
    {
        if (_gpuImages.TryGetValue(image, out var cached)) return cached;
        if (image.IsTransient)
        {
            var transient = CreateTexture(image);
            _transientGpuImages.Add(transient);
            return new GpuPlacement(transient.View, new Vector4(0, 0, 1, 1), null);
        }
        GpuPlacement placement;
        if (_atlas.TryPack(image.Width, image.Height, out var slot))
        {
            while (_atlasPages.Count <= slot.Page) _atlasPages.Add(CreateAtlasPage());
            var page = _atlasPages[slot.Page];
            FrameProfiler.Upload(image.Rgba.Length);
            _context!.UpdateSubresource((ReadOnlySpan<byte>)image.Rgba, page.Texture, 0, (uint)(image.Width * 4), 0,
                new Box(slot.X, slot.Y, 0, slot.X + image.Width, slot.Y + image.Height, 1));
            const float size = AtlasPageSize;
            placement = new GpuPlacement(page.View,
                new Vector4(slot.X / size, slot.Y / size, (slot.X + image.Width) / size, (slot.Y + image.Height) / size), null);
        }
        else
        {
            var owned = CreateTexture(image);
            placement = new GpuPlacement(owned.View, new Vector4(0, 0, 1, 1), owned);
        }
        _gpuImages.Add(image, placement);
        return placement;
    }

    private GpuTexture CreateAtlasPage()
    {
        var device = _device ?? throw new InvalidOperationException("D3D11 device is unavailable.");
        var texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = AtlasPageSize,
            Height = AtlasPageSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        return new GpuTexture(texture, device.CreateShaderResourceView(texture));
    }

    /// <summary>A texture of the image's own: its RGBA pixels upload as they are.</summary>
    private GpuTexture CreateTexture(GpuImage image)
    {
        var device = _device ?? throw new InvalidOperationException("D3D11 device is unavailable.");
        var texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)image.Width,
            Height = (uint)image.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        FrameProfiler.Upload(image.Rgba.Length);
        _context!.UpdateSubresource((ReadOnlySpan<byte>)image.Rgba, texture, 0, (uint)(image.Width * 4), 0);
        return new GpuTexture(texture, device.CreateShaderResourceView(texture));
    }

    private unsafe void DrawTexture(ID3D11ShaderResourceView source, ID3D11RenderTargetView destination, Rectangle destinationBounds, bool alphaBlend)
    {
        var context = _context ?? throw new InvalidOperationException("D3D11 context is unavailable.");
        PrepareQuad(context, destination, destinationBounds);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResource(0, source);
        context.PSSetSampler(0, _pointSampler);
        context.OMSetBlendState(alphaBlend ? _alphaBlend : null, null, uint.MaxValue);
        context.Draw(6, 0);
        context.PSSetShaderResource(0, default!);
    }

    /// <summary>
    /// Places the frame at a non-whole scale: each logical pixel is enlarged
    /// by the whole prescale, and only the seams between pixels are blended,
    /// so pixels stay even and edges stay sharp.
    /// </summary>
    private unsafe void DrawSharpBilinear(DisplayLayout layout)
    {
        var context = _context!;
        context.UpdateSubresource(new ScalingConstants(
            new Vector2(layout.Logical.Width, layout.Logical.Height),
            new Vector2(layout.Prescale.Width, layout.Prescale.Height)), _scalingConstants!);
        PrepareQuad(context, _backBufferView!, layout.Destination);
        context.PSSetShader(_sharpPixelShader);
        context.PSSetConstantBuffer(0, _scalingConstants);
        context.PSSetShaderResource(0, _nativeTargetResource!);
        context.PSSetSampler(0, _linearSampler);
        context.OMSetBlendState(null, null, uint.MaxValue);
        context.Draw(6, 0);
        context.PSSetShaderResource(0, default!);
    }

    private void PrepareQuad(ID3D11DeviceContext context, ID3D11RenderTargetView destination, Rectangle bounds)
    {
        context.OMSetRenderTargets(destination);
        context.RSSetViewport(new Viewport(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_inputLayout);
        context.IASetVertexBuffer(0, _quadVertices!, QuadVertex.SizeInBytes);
        context.VSSetShader(_vertexShader);
    }

    private void ReleaseDevice()
    {
        _context?.ClearState();
        foreach (var placement in _gpuImages.Values)
        {
            placement.Owned?.View.Dispose();
            placement.Owned?.Texture.Dispose();
        }
        _gpuImages.Clear();
        foreach (var page in _atlasPages)
        {
            page.View.Dispose();
            page.Texture.Dispose();
        }
        _atlasPages.Clear();
        _atlas.Reset();
        _atlasReleasedArea = 0;
        _batchVertices?.Dispose();
        _batchIndices?.Dispose();
        _batchConstants?.Dispose();
        _batchInputLayout?.Dispose();
        _batchVertexShader?.Dispose();
        _batchVertices = null;
        _batchIndices = null;
        _batchConstants = null;
        _batchInputLayout = null;
        _batchVertexShader = null;
        _batchQuadCapacity = 0;
        foreach (var image in _transientGpuImages)
        {
            image.View.Dispose();
            image.Texture.Dispose();
        }
        _transientGpuImages.Clear();
        _alphaBlend?.Dispose();
        _linearSampler?.Dispose();
        _pointSampler?.Dispose();
        _inputLayout?.Dispose();
        _sharpPixelShader?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _scalingConstants?.Dispose();
        _quadVertices?.Dispose();
        _nativeTargetResource?.Dispose();
        _nativeTargetView?.Dispose();
        _nativeTarget?.Dispose();
        _backBufferView?.Dispose();
        _backBuffer?.Dispose();
        CloseFrameLatencyHandle();
        _swapChain?.Dispose();
        _factory?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _backBuffer = null;
        _backBufferView = null;
        _backBufferSize = Size.Empty;
        _context = null;
        _device = null;
        _swapChain = null;
        _factory = null;
        _pointSampler = null;
        _linearSampler = null;
        _alphaBlend = null;
        _inputLayout = null;
        _pixelShader = null;
        _sharpPixelShader = null;
        _vertexShader = null;
        _quadVertices = null;
        _scalingConstants = null;
        _nativeTargetResource = null;
        _nativeTargetView = null;
        _nativeTarget = null;
        _nativeTargetSize = Size.Empty;
    }

    private sealed record GpuTexture(ID3D11Texture2D Texture, ID3D11ShaderResourceView View);

    /// <summary>An image on the GPU: the view it samples, its corners there (u0, v0, u1, v1), and its own texture when it has one.</summary>
    private sealed record GpuPlacement(ID3D11ShaderResourceView View, Vector4 Uv, GpuTexture? Owned);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct BatchVertex(Vector2 Position, Vector2 TextureCoordinate)
    {
        public static uint SizeInBytes => (uint)Marshal.SizeOf<BatchVertex>();

        public static InputElementDescription[] InputElements =>
        [
            new("POSITION", 0, Format.R32G32_Float, 0, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
        ];
    }

    /// <summary>The sharp-bilinear shader's constants: the source size and the whole prescale.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct ScalingConstants(Vector2 SourceSize, Vector2 Prescale);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct QuadVertex(Vector3 Position, Vector2 TextureCoordinate)
    {
        public static uint SizeInBytes => (uint)Marshal.SizeOf<QuadVertex>();

        public static InputElementDescription[] InputElements =>
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
        ];
    }
}
