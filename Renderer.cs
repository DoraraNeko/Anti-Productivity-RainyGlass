using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace RainyGlass;

[StructLayout(LayoutKind.Sequential)]
struct Globals
{
    public Vector2 Screen;
    public float Time;
    public float Scale;
    public Vector4 Mul;
    public float Refract;
    public float BlurLod;
    public float Darken;
    public float Pad;
}

sealed class Renderer : IDisposable
{
    public readonly int W, H;
    public float Refract = 1f;
    public float BlurLod = 0.6f;
    public float Darken = 0.85f;
    public float DecaySmallPerSec = 0.985f;  // 小さな水滴の蒸発
    public float DecayTrailPerSec = 0.55f;   // 流れ跡が消える速さ

    readonly ID3D11Device _dev;
    readonly ID3D11DeviceContext _ctx;
    readonly IDXGIAdapter1 _adapter;
    readonly IDXGIOutput? _output;

    IDXGIOutputDuplication? _dup;
    IDXGISwapChain1? _swap;
    IDCompositionDevice? _dcomp;
    IDCompositionTarget? _dtarget;
    IDCompositionVisual? _dvisual;
    ID3D11Texture2D? _offscreen;

    readonly ID3D11Texture2D _bgTex;
    readonly ID3D11ShaderResourceView _bgSrv;
    readonly ID3D11Texture2D _bigTex, _smallTex;
    readonly ID3D11RenderTargetView _bigRtv, _smallRtv;
    readonly ID3D11ShaderResourceView _bigSrv, _smallSrv;

    readonly ID3D11VertexShader _vsDrop, _vsSeg, _vsFull;
    readonly ID3D11PixelShader _psDrop, _psStamp, _psErase, _psMul, _psComp;
    readonly ID3D11BlendState _bsMax, _bsMul, _bsOpaque;
    readonly ID3D11RasterizerState _rs;
    readonly ID3D11SamplerState _samp;
    readonly ID3D11Buffer _cb;
    readonly InstBuffer _dropBuf, _eraseBuf, _stampBuf;

    float _time;
    bool _bgReady;

    public bool CaptureOk => _dup != null;

    Renderer(IDXGIAdapter1 adapter, IDXGIOutput? output, int w, int h)
    {
        W = w; H = h;
        _adapter = adapter;
        _output = output;

        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _dev!, out _ctx!).CheckError();

        // 背景 (画面キャプチャ) テクスチャ: ミップを作ってぼかしサンプリングに使う
        _bgTex = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 0, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _bgSrv = _dev.CreateShaderResourceView(_bgTex);
        using (var rtv = _dev.CreateRenderTargetView(_bgTex))
            _ctx.ClearRenderTargetView(rtv, new Color4(0.5f, 0.5f, 0.5f, 1));

        (_bigTex, _bigRtv, _bigSrv) = MakeTarget(w, h, Format.R16G16_Float);
        (_smallTex, _smallRtv, _smallSrv) = MakeTarget(w, h, Format.R16G16_Float);
        _ctx.ClearRenderTargetView(_smallRtv, new Color4(0, 0, 0, 0));

        string src = LoadShaderSource();
        _vsDrop = _dev.CreateVertexShader(Compile(src, "VS_Drop", "vs_5_0"));
        _vsSeg = _dev.CreateVertexShader(Compile(src, "VS_Seg", "vs_5_0"));
        _vsFull = _dev.CreateVertexShader(Compile(src, "VS_Full", "vs_5_0"));
        _psDrop = _dev.CreatePixelShader(Compile(src, "PS_Drop", "ps_5_0"));
        _psStamp = _dev.CreatePixelShader(Compile(src, "PS_Stamp", "ps_5_0"));
        _psErase = _dev.CreatePixelShader(Compile(src, "PS_Erase", "ps_5_0"));
        _psMul = _dev.CreatePixelShader(Compile(src, "PS_Mul", "ps_5_0"));
        _psComp = _dev.CreatePixelShader(Compile(src, "PS_Composite", "ps_5_0"));

        _bsMax = MakeBlend(Blend.One, Blend.One, BlendOperation.Max);
        _bsMul = MakeBlend(Blend.Zero, Blend.SourceColor, BlendOperation.Add);
        _bsOpaque = _dev.CreateBlendState(BlendDescription.Opaque);
        _rs = _dev.CreateRasterizerState(RasterizerDescription.CullNone);
        _samp = _dev.CreateSamplerState(SamplerDescription.LinearClamp);
        _cb = _dev.CreateBuffer(new BufferDescription((uint)Unsafe.SizeOf<Globals>(), BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic, CpuAccessFlags.Write));

        _dropBuf = new InstBuffer(_dev, 8192);
        _eraseBuf = new InstBuffer(_dev, 8192);
        _stampBuf = new InstBuffer(_dev, 65536);
    }

    // ---- 生成 -------------------------------------------------------------

    /// <summary>指定ウィンドウに重ねて表示するライブモード。</summary>
    public static Renderer CreateLive(IntPtr hwnd, Rectangle monitorBounds)
    {
        var (adapter, output) = FindOutput(monitorBounds);
        var r = new Renderer(adapter, output, monitorBounds.Width, monitorBounds.Height);
        r.InitComposition(hwnd);
        r.InitDuplication();
        return r;
    }

    /// <summary>画像を背景にしてオフスクリーンで描画する確認用モード。</summary>
    public static Renderer CreateOffscreen(int w, int h)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1(0, out var adapter).CheckError();
        var r = new Renderer(adapter, null, w, h);
        r._offscreen = r._dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        });
        return r;
    }

    static (IDXGIAdapter1, IDXGIOutput) FindOutput(Rectangle bounds)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? firstAdapter = null;
        IDXGIOutput? firstOutput = null;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            bool keepAdapter = false;
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var rc = output.Description.DesktopCoordinates;
                if (rc.Left == bounds.Left && rc.Top == bounds.Top)
                {
                    firstOutput?.Dispose();
                    if (firstAdapter != null && firstAdapter != adapter) firstAdapter.Dispose();
                    return (adapter, output);
                }
                if (firstOutput == null) { firstOutput = output; firstAdapter = adapter; keepAdapter = true; }
                else output.Dispose();
            }
            if (!keepAdapter) adapter.Dispose();
        }
        if (firstOutput == null || firstAdapter == null) throw new InvalidOperationException("No display output found.");
        return (firstAdapter, firstOutput);
    }

    void InitComposition(IntPtr hwnd)
    {
        using var factory = _adapter.GetParent<IDXGIFactory2>();
        _swap = factory.CreateSwapChainForComposition(_dev, new SwapChainDescription1
        {
            Width = (uint)W, Height = (uint)H,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Scaling = Scaling.Stretch,
        });
        using var dxgiDev = _dev.QueryInterface<IDXGIDevice>();
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDev);
        _dcomp.CreateTargetForHwnd(hwnd, true, out _dtarget).CheckError();
        _dcomp.CreateVisual(out _dvisual).CheckError();
        _dvisual!.SetContent(_swap);
        _dtarget!.SetRoot(_dvisual);
        _dcomp.Commit();
    }

    void InitDuplication()
    {
        _dup?.Dispose();
        _dup = null;
        if (_output == null) return;
        try
        {
            using var o1 = _output.QueryInterface<IDXGIOutput1>();
            _dup = o1.DuplicateOutput(_dev);
        }
        catch (Exception ex)
        {
            Log.Write("DuplicateOutput failed: " + ex.Message);
        }
    }

    // ---- 毎フレーム -------------------------------------------------------

    int _dupRetry;

    void UpdateCapture()
    {
        if (_dup == null)
        {
            if (_output != null && ++_dupRetry % 60 == 0) InitDuplication();
            return;
        }
        var res = _dup.AcquireNextFrame(0, out var info, out var resource);
        if (res.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return;
        if (res.Failure)
        {
            Log.Write($"AcquireNextFrame: 0x{res.Code:X8}");
            InitDuplication();
            return;
        }
        try
        {
            if (info.LastPresentTime != 0 || !_bgReady)
            {
                using var tex = resource!.QueryInterface<ID3D11Texture2D>();
                var d = tex.Description;
                if (d.Width == W && d.Height == H)
                {
                    _ctx.CopySubresourceRegion(_bgTex, 0, 0, 0, 0, tex, 0);
                    _ctx.GenerateMips(_bgSrv);
                    _bgReady = true;
                }
            }
        }
        finally
        {
            resource?.Dispose();
            _dup.ReleaseFrame();
        }
    }

    public void SetBackground(byte[] bgra, int stride)
    {
        _ctx.UpdateSubresource(bgra, _bgTex, 0, (uint)stride, 0);
        _ctx.GenerateMips(_bgSrv);
        _bgReady = true;
    }

    public void ClearGlass()
    {
        _ctx.ClearRenderTargetView(_smallRtv, new Color4(0, 0, 0, 0));
    }

    public unsafe void Render(RainSim sim, float dt)
    {
        _time += dt;
        if (_output != null) UpdateCapture();

        var g = new Globals
        {
            Screen = new Vector2(W, H), Time = _time, Scale = sim.S,
            Mul = new Vector4(MathF.Pow(DecaySmallPerSec, dt), MathF.Pow(DecayTrailPerSec, dt), 1, 1),
            Refract = Refract, BlurLod = BlurLod, Darken = Darken,
        };
        var m = _ctx.Map(_cb, MapMode.WriteDiscard);
        Unsafe.Write((void*)m.DataPointer, g);
        _ctx.Unmap(_cb, 0);

        _ctx.RSSetState(_rs);
        _ctx.RSSetViewport(0, 0, W, H);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _ctx.VSSetConstantBuffer(0, _cb);
        _ctx.PSSetConstantBuffer(0, _cb);

        // 1) 小さな水滴/流れ跡テクスチャ: 減衰 → 拭き取り → スタンプ
        _ctx.OMSetRenderTargets(_smallRtv);
        _ctx.OMSetBlendState(_bsMul);
        _ctx.VSSetShader(_vsFull);
        _ctx.PSSetShader(_psMul);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.Draw(3, 0);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

        DrawInstances(_eraseBuf, sim.EraseInst, _vsSeg, _psErase);
        _ctx.OMSetBlendState(_bsMax);
        DrawInstances(_stampBuf, sim.StampInst, _vsSeg, _psStamp);

        // 2) 大きい水滴
        _ctx.OMSetRenderTargets(_bigRtv);
        _ctx.ClearRenderTargetView(_bigRtv, new Color4(0, 0, 0, 0));
        DrawInstances(_dropBuf, sim.DropInst, _vsDrop, _psDrop);

        // 3) 合成
        ID3D11Texture2D target;
        bool ownsTarget = false;
        if (_swap != null) { target = _swap.GetBuffer<ID3D11Texture2D>(0); ownsTarget = true; }
        else target = _offscreen!;
        using (var rtv = _dev.CreateRenderTargetView(target))
        {
            _ctx.OMSetRenderTargets(rtv);
            _ctx.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 0));
            _ctx.OMSetBlendState(_bsOpaque);
            _ctx.VSSetShader(_vsFull);
            _ctx.PSSetShader(_psComp);
            _ctx.PSSetShaderResources(0, new[] { _bigSrv, _smallSrv, _bgSrv });
            _ctx.PSSetSampler(0, _samp);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _ctx.Draw(3, 0);
            _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null!, null! });
        }
        if (ownsTarget) target.Dispose();

        _swap?.Present(1, PresentFlags.None);
    }

    void DrawInstances(InstBuffer buf, List<Inst> list, ID3D11VertexShader vs, ID3D11PixelShader ps)
    {
        if (list.Count == 0) return;
        _ctx.VSSetShader(vs);
        _ctx.PSSetShader(ps);
        var span = CollectionsMarshal.AsSpan(list);
        while (span.Length > 0)
        {
            int n = Math.Min(span.Length, buf.Capacity);
            buf.Upload(_ctx, span[..n]);
            _ctx.VSSetShaderResource(0, buf.Srv);
            _ctx.DrawInstanced(4, (uint)n, 0, 0);
            span = span[n..];
        }
        _ctx.VSSetShaderResource(0, null!);
    }

    /// <summary>オフスクリーン描画結果を背景と合成した BGRA を返す。</summary>
    public byte[] ReadComposited(byte[] bgBgra)
    {
        using var staging = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)W, Height = (uint)H, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _ctx.CopyResource(staging, _offscreen!);
        var map = _ctx.Map(staging, 0, MapMode.Read);
        var outBuf = new byte[W * H * 4];
        unsafe
        {
            byte* src = (byte*)map.DataPointer;
            for (int y = 0; y < H; y++)
            {
                byte* row = src + y * map.RowPitch;
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    float a = row[x * 4 + 3] / 255f;
                    for (int c = 0; c < 3; c++)
                        outBuf[i + c] = (byte)Math.Clamp(row[x * 4 + c] + bgBgra[i + c] * (1 - a), 0, 255);
                    outBuf[i + 3] = 255;
                }
            }
        }
        _ctx.Unmap(staging, 0);
        return outBuf;
    }

    // ---- ヘルパー ---------------------------------------------------------

    (ID3D11Texture2D, ID3D11RenderTargetView, ID3D11ShaderResourceView) MakeTarget(int w, int h, Format f)
    {
        var tex = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        return (tex, _dev.CreateRenderTargetView(tex), _dev.CreateShaderResourceView(tex));
    }

    ID3D11BlendState MakeBlend(Blend src, Blend dst, BlendOperation op)
    {
        var desc = new BlendDescription();
        desc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = src, DestinationBlend = dst, BlendOperation = op,
            SourceBlendAlpha = src, DestinationBlendAlpha = dst == Blend.SourceColor ? Blend.SourceAlpha : dst, BlendOperationAlpha = op,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        return _dev.CreateBlendState(desc);
    }

    static string LoadShaderSource()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Shaders.hlsl")!;
        using var r = new StreamReader(s);
        // D3DCompiler には ASCII で渡す (日本語コメントで長さがずれてソースが切れるため)
        return new string(r.ReadToEnd().Select(c => c < 128 ? c : ' ').ToArray());
    }

    static byte[] Compile(string src, string entry, string profile)
    {
        var res = Compiler.Compile(src, entry, "Shaders.hlsl", profile, out Blob blob, out Blob? err);
        if (res.Failure || blob == null)
        {
            string msg = err?.AsString() ?? res.ToString();
            err?.Dispose();
            throw new InvalidOperationException($"Shader {entry}: {msg}");
        }
        err?.Dispose();
        var bytes = blob.AsBytes();
        blob.Dispose();
        return bytes;
    }

    public void Dispose()
    {
        _ctx.ClearState();
        _ctx.Flush();
        _dropBuf.Dispose(); _eraseBuf.Dispose(); _stampBuf.Dispose();
        _cb.Dispose(); _samp.Dispose(); _rs.Dispose();
        _bsMax.Dispose(); _bsMul.Dispose(); _bsOpaque.Dispose();
        _vsDrop.Dispose(); _vsSeg.Dispose(); _vsFull.Dispose();
        _psDrop.Dispose(); _psStamp.Dispose(); _psErase.Dispose(); _psMul.Dispose(); _psComp.Dispose();
        _bigRtv.Dispose(); _bigSrv.Dispose(); _bigTex.Dispose();
        _smallRtv.Dispose(); _smallSrv.Dispose(); _smallTex.Dispose();
        _bgSrv.Dispose(); _bgTex.Dispose();
        _offscreen?.Dispose();
        _dup?.Dispose();
        _dvisual?.Dispose(); _dtarget?.Dispose(); _dcomp?.Dispose();
        _swap?.Dispose();
        _output?.Dispose();
        _ctx.Dispose(); _dev.Dispose();
        _adapter.Dispose();
    }
}

sealed class InstBuffer : IDisposable
{
    public readonly int Capacity;
    public readonly ID3D11Buffer Buffer;
    public readonly ID3D11ShaderResourceView Srv;

    public InstBuffer(ID3D11Device dev, int capacity)
    {
        Capacity = capacity;
        uint stride = (uint)Unsafe.SizeOf<Inst>();
        Buffer = dev.CreateBuffer(new BufferDescription(stride * (uint)capacity, BindFlags.ShaderResource,
            ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, stride));
        Srv = dev.CreateShaderResourceView(Buffer);
    }

    public unsafe void Upload(ID3D11DeviceContext ctx, ReadOnlySpan<Inst> data)
    {
        var m = ctx.Map(Buffer, MapMode.WriteDiscard);
        data.CopyTo(new Span<Inst>((void*)m.DataPointer, Capacity));
        ctx.Unmap(Buffer, 0);
    }

    public void Dispose() { Srv.Dispose(); Buffer.Dispose(); }
}

static class Log
{
    static readonly string PathName = System.IO.Path.Combine(AppContext.BaseDirectory, "aprg.log");
    public static void Write(string s)
    {
        try { File.AppendAllText(PathName, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); } catch { }
    }
}
