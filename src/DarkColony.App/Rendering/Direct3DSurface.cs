using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace DarkColony.App.Rendering;

/// <summary>
/// D3D11 presentation surface for the fixed 640x480 game framebuffer.
/// Presented layers are emitted as ordered GPU commands. The callback retains
/// a scratch <see cref="Graphics"/> only for isolated fallback code; its
/// contents are never uploaded or presented as a full-frame texture.
/// </summary>
public sealed class Direct3DSurface : Control
{
    private const int NativeWidth = 640;
    private const int NativeHeight = 480;
    private readonly Action<Graphics, GameCanvas> _renderFrame;
    private readonly GameCanvas _canvas = new();
    private readonly Bitmap _legacyScratch = new(NativeWidth, NativeHeight, PixelFormat.Format32bppArgb);
    private IDXGISwapChain? _swapChain;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _backBufferView;
    private ID3D11Texture2D? _nativeTarget;
    private ID3D11RenderTargetView? _nativeTargetView;
    private ID3D11ShaderResourceView? _nativeTargetResource;
    private ID3D11Buffer? _quadVertices;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11SamplerState? _pointSampler;
    private ID3D11BlendState? _alphaBlend;
    private readonly Dictionary<GpuImage, GpuTexture> _gpuImages = [];

    public Direct3DSurface(Action<Graphics, GameCanvas> renderFrame)
    {
        _renderFrame = renderFrame;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public void RenderAndPresent()
    {
        if (!IsHandleCreated || ClientSize.Width == 0 || ClientSize.Height == 0) return;
        EnsureDevice();

        _canvas.Reset();
        using (var graphics = Graphics.FromImage(_legacyScratch))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            _renderFrame(graphics, _canvas);
        }

        _context!.ClearRenderTargetView(_nativeTargetView!, new Color4(0f, 0f, 0f, 1f));
        DrawCommands(_canvas.Commands);

        DrawCommands(_canvas.ForegroundCommands);
        DrawTexture(_nativeTargetResource!, _backBufferView!, new Rectangle(0, 0, NativeWidth, NativeHeight), alphaBlend: false);
        _swapChain!.Present(1, PresentFlags.None);
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
            _legacyScratch.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs eventArgs) => RenderAndPresent();

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
    }

    private void EnsureDevice()
    {
        if (_device is not null) return;

        var description = new SwapChainDescription
        {
            BufferCount = 2,
            BufferDescription = new ModeDescription(NativeWidth, NativeHeight, Format.B8G8R8A8_UNorm),
            BufferUsage = Usage.RenderTargetOutput,
            OutputWindow = Handle,
            SampleDescription = new SampleDescription(1, 0),
            Windowed = true,
            SwapEffect = SwapEffect.Discard,
        };
        var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11CreateDeviceAndSwapChain(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            description,
            out _swapChain,
            out _device,
            out _,
            out _context).CheckError();

        var swapChain = _swapChain ?? throw new InvalidOperationException("D3D11 did not return a swap chain.");
        var device = _device ?? throw new InvalidOperationException("D3D11 did not return a device.");
        _backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = device.CreateRenderTargetView(_backBuffer);

        _nativeTarget = device.CreateTexture2D(new Texture2DDescription
        {
            Width = NativeWidth,
            Height = NativeHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        _nativeTargetView = device.CreateRenderTargetView(_nativeTarget);
        _nativeTargetResource = device.CreateShaderResourceView(_nativeTarget);

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

        var shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "Sprite.hlsl");
        var shaderFlags = ShaderFlags.EnableStrictness;
#if DEBUG
        shaderFlags |= ShaderFlags.Debug;
#else
        shaderFlags |= ShaderFlags.OptimizationLevel3;
#endif
        var vertexBytecode = Compiler.CompileFromFile(shaderPath, "VSMain", "vs_4_0", shaderFlags);
        var pixelBytecode = Compiler.CompileFromFile(shaderPath, "PSMain", "ps_4_0", shaderFlags);
        _vertexShader = device.CreateVertexShader(vertexBytecode.Span);
        _pixelShader = device.CreatePixelShader(pixelBytecode.Span);
        _inputLayout = device.CreateInputLayout(QuadVertex.InputElements, vertexBytecode.Span);
        _pointSampler = device.CreateSamplerState(SamplerDescription.PointClamp);
        _alphaBlend = device.CreateBlendState(BlendDescription.NonPremultiplied);
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
        _gpuImages.Add(image, new GpuTexture(texture, view));
        return view;
    }

    private unsafe void DrawTexture(ID3D11ShaderResourceView source, ID3D11RenderTargetView destination, Rectangle destinationBounds, bool alphaBlend)
    {
        var context = _context ?? throw new InvalidOperationException("D3D11 context is unavailable.");
        context.OMSetRenderTargets(destination);
        context.RSSetViewport(new Viewport(destinationBounds.X, destinationBounds.Y, destinationBounds.Width, destinationBounds.Height));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_inputLayout);
        context.IASetVertexBuffer(0, _quadVertices!, QuadVertex.SizeInBytes);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResource(0, source);
        context.PSSetSampler(0, _pointSampler);
        context.OMSetBlendState(alphaBlend ? _alphaBlend : null, null, uint.MaxValue);
        context.Draw(6, 0);
        context.PSSetShaderResource(0, default!);
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
        _alphaBlend?.Dispose();
        _pointSampler?.Dispose();
        _inputLayout?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _quadVertices?.Dispose();
        _nativeTargetResource?.Dispose();
        _nativeTargetView?.Dispose();
        _nativeTarget?.Dispose();
        _backBufferView?.Dispose();
        _backBuffer?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _swapChain?.Dispose();
        _backBuffer = null;
        _context = null;
        _device = null;
        _swapChain = null;
        _pointSampler = null;
        _alphaBlend = null;
        _inputLayout = null;
        _pixelShader = null;
        _vertexShader = null;
        _quadVertices = null;
        _nativeTargetResource = null;
        _nativeTargetView = null;
        _nativeTarget = null;
        _backBufferView = null;
    }

    private sealed record GpuTexture(ID3D11Texture2D Texture, ID3D11ShaderResourceView View);

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
