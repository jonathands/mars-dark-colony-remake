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
/// sampled when the scale is a whole number, sharp-bilinear otherwise. The
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
    private readonly Dictionary<GpuImage, GpuTexture> _gpuImages = [];
    private readonly List<GpuTexture> _transientGpuImages = [];
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

    /// <summary>Live GPU objects, for the display-mode leak test.</summary>
    public string ResourceSummary() =>
        $"{_gpuImages.Count} cached textures, back buffer {_backBufferSize.Width}x{_backBufferSize.Height}, target {_nativeTargetSize.Width}x{_nativeTargetSize.Height}";

    public void RenderAndPresent()
    {
        if (!IsHandleCreated || ClientSize.Width == 0 || ClientSize.Height == 0) return;
        EnsureDevice();
        EnsureBackBuffer();
        EnsureNativeTarget();

        _canvas.Reset();
        if (_legacyScratch is null || _legacyScratch.Size != _logicalSize)
        {
            _legacyScratch?.Dispose();
            _legacyScratch = new Bitmap(_logicalSize.Width, _logicalSize.Height, PixelFormat.Format32bppArgb);
        }
        using (var graphics = Graphics.FromImage(_legacyScratch))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            _renderFrame(graphics, _canvas);
        }

        try
        {
            var context = _context!;
            context.ClearRenderTargetView(_nativeTargetView!, new Color4(0f, 0f, 0f, 1f));
            DrawCommands(_canvas.Commands);
            DrawCommands(_canvas.ForegroundCommands);
            context.ClearRenderTargetView(_backBufferView!, new Color4(0f, 0f, 0f, 1f));
            var layout = PictureLayout;
            if (!layout.Destination.IsEmpty)
            {
                if (layout.PixelExact) DrawTexture(_nativeTargetResource!, _backBufferView!, layout.Destination, alphaBlend: false);
                else DrawSharpBilinear(layout);
            }
            var tearing = !VSync && _tearingSupported;
            var result = _swapChain!.Present(VSync ? 1u : 0u, tearing ? PresentFlags.AllowTearing : PresentFlags.None);
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
        if (!_gpuImages.Remove(image, out var gpuImage)) return;
        gpuImage.View.Dispose();
        gpuImage.Texture.Dispose();
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

    protected override void OnPaint(PaintEventArgs eventArgs) => RenderAndPresent();

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
            _swapChain.Dispose();
        }
        _swapChainFlags = (allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None);
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
        CreateBackBufferView();
    }

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

    private void DrawCommands(IReadOnlyList<SpriteCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command.Destination.Width <= 0 || command.Destination.Height <= 0) continue;
            DrawTexture(GetGpuImage(command.Image), _nativeTargetView!, command.Destination, alphaBlend: true);
        }
    }

    private unsafe ID3D11ShaderResourceView GetGpuImage(GpuImage image)
    {
        if (_gpuImages.TryGetValue(image, out var cached)) return cached.View;
        var gpuImage = CreateGpuImage(image);
        if (image.IsTransient)
        {
            _transientGpuImages.Add(gpuImage);
            return gpuImage.View;
        }
        _gpuImages.Add(image, gpuImage);
        return gpuImage.View;
    }

    private unsafe GpuTexture CreateGpuImage(GpuImage image)
    {
        var device = _device ?? throw new InvalidOperationException("D3D11 device is unavailable.");
        var texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)image.Width,
            Height = (uint)image.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        var bgra = new byte[image.Rgba.Length];
        for (var index = 0; index < image.Rgba.Length; index += 4)
        {
            bgra[index] = image.Rgba[index + 2];
            bgra[index + 1] = image.Rgba[index + 1];
            bgra[index + 2] = image.Rgba[index];
            bgra[index + 3] = image.Rgba[index + 3];
        }
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            _context!.UpdateSubresource(texture, 0, null, handle.AddrOfPinnedObject(), (uint)(image.Width * 4), 0);
        }
        finally
        {
            handle.Free();
        }
        var view = device.CreateShaderResourceView(texture);
        return new GpuTexture(texture, view);
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
        foreach (var image in _gpuImages.Values)
        {
            image.View.Dispose();
            image.Texture.Dispose();
        }
        _gpuImages.Clear();
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
