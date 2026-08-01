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
/// The callback bridge is temporary while individual menu draw operations are
/// moved to the GPU sprite batch; presentation no longer depends on WM_PAINT.
/// </summary>
public sealed class Direct3DSurface : Control
{
    private const int NativeWidth = 640;
    private const int NativeHeight = 480;
    private readonly Action<Graphics> _renderLegacyFrame;
    private readonly Bitmap _transferBitmap = new(NativeWidth, NativeHeight, PixelFormat.Format32bppArgb);
    private IDXGISwapChain? _swapChain;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _uploadTexture;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11ShaderResourceView? _uploadView;
    private ID3D11RenderTargetView? _backBufferView;
    private ID3D11Texture2D? _nativeTarget;
    private ID3D11RenderTargetView? _nativeTargetView;
    private ID3D11ShaderResourceView? _nativeTargetResource;
    private ID3D11Buffer? _quadVertices;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11SamplerState? _pointSampler;

    public Direct3DSurface(Action<Graphics> renderLegacyFrame)
    {
        _renderLegacyFrame = renderLegacyFrame;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public void RenderAndPresent()
    {
        if (!IsHandleCreated || ClientSize.Width == 0 || ClientSize.Height == 0) return;
        EnsureDevice();

        using (var graphics = Graphics.FromImage(_transferBitmap))
        {
            _renderLegacyFrame(graphics);
        }

        var data = _transferBitmap.LockBits(
            new Rectangle(0, 0, NativeWidth, NativeHeight),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            _context!.UpdateSubresource(
                _uploadTexture!,
                0,
                null,
                data.Scan0,
                (uint)data.Stride,
                0);
        }
        finally
        {
            _transferBitmap.UnlockBits(data);
        }

        DrawTexture(_uploadView!, _nativeTargetView!);
        DrawTexture(_nativeTargetResource!, _backBufferView!);
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
            _transferBitmap.Dispose();
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
        _uploadTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = NativeWidth,
            Height = NativeHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        _uploadView = device.CreateShaderResourceView(_uploadTexture);
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
    }

    private void DrawTexture(ID3D11ShaderResourceView source, ID3D11RenderTargetView destination)
    {
        var context = _context ?? throw new InvalidOperationException("D3D11 context is unavailable.");
        context.OMSetRenderTargets(destination);
        context.RSSetViewport(new Viewport(0, 0, NativeWidth, NativeHeight));
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetInputLayout(_inputLayout);
        context.IASetVertexBuffer(0, _quadVertices!, QuadVertex.SizeInBytes);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResource(0, source);
        context.PSSetSampler(0, _pointSampler);
        context.Draw(6, 0);
        context.PSSetShaderResource(0, default!);
    }

    private void ReleaseDevice()
    {
        _context?.ClearState();
        _pointSampler?.Dispose();
        _inputLayout?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _quadVertices?.Dispose();
        _nativeTargetResource?.Dispose();
        _nativeTargetView?.Dispose();
        _nativeTarget?.Dispose();
        _backBufferView?.Dispose();
        _uploadView?.Dispose();
        _backBuffer?.Dispose();
        _uploadTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _swapChain?.Dispose();
        _backBuffer = null;
        _uploadTexture = null;
        _context = null;
        _device = null;
        _swapChain = null;
        _pointSampler = null;
        _inputLayout = null;
        _pixelShader = null;
        _vertexShader = null;
        _quadVertices = null;
        _nativeTargetResource = null;
        _nativeTargetView = null;
        _nativeTarget = null;
        _backBufferView = null;
        _uploadView = null;
    }

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
