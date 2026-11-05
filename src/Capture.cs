using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace AmbientDesktop
{
    /// <summary>
    /// Grabs a tiny downscaled copy of one monitor into MonitorState.LowPixels (BGRA, LW x LH).
    /// Prefers DXGI Desktop Duplication (GPU mipmaps do the downscale, near-zero CPU, and it tells us when
    /// nothing changed). Falls back to GDI StretchBlt, which works everywhere but stalls on vsync.
    /// </summary>
    internal sealed class MonitorCapture : IDisposable
    {
        private readonly MonitorState _m;
        private DxgiCapture _dx;
        private double _retryDxAt;
        private int _dxFails;
        public string Kind = "none";

        public MonitorCapture(MonitorState m) { _m = m; }

        /// <returns>1 = new pixels, 0 = unchanged since last call, -1 = nothing usable this frame</returns>
        public int Capture(IntPtr screenDc, double now)
        {
            if (!_m.ForceGdi && _dx == null && now >= _retryDxAt)
            {
                _dx = DxgiCapture.TryCreate(_m.Bounds, _m.MipLevel, _m.LW, _m.LH);
                if (_dx == null) _retryDxAt = now + 20;
                SetKind(_dx != null ? "dxgi" : "gdi");
            }

            if (_dx != null)
            {
                int r = _dx.Capture(_m.LowPixels);
                if (r >= 0) { _dxFails = 0; return r; }
                if (++_dxFails < 45) return -1;
                Log.Write("DXGI capture keeps failing on " + _m.Bounds + ", using GDI for a while");
                _dx.Dispose();
                _dx = null;
                _dxFails = 0;
                _retryDxAt = now + 20;
                SetKind("gdi");
            }

            // GDI fallback
            if (!Native.StretchBlt(_m.Low.Dc, 0, 0, _m.LW, _m.LH, screenDc, _m.Bounds.X, _m.Bounds.Y,
                    _m.LW * _m.Cell, _m.LH * _m.Cell, Native.SRCCOPY))
                return -1;
            Native.GdiFlush();
            Marshal.Copy(_m.Low.Bits, _m.LowPixels, 0, _m.LowPixels.Length);
            return 1;
        }

        private void SetKind(string k)
        {
            if (k == Kind) return;
            Kind = k;
            Log.Write("capture for monitor " + _m.Bounds + ": " + k);
        }

        public void Dispose()
        {
            if (_dx != null) _dx.Dispose();
            _dx = null;
        }
    }

    /// <summary>Raw-COM Desktop Duplication. No external libraries, just vtable calls.</summary>
    internal sealed class DxgiCapture : IDisposable
    {
        // ---- interop types ----------------------------------------------------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public Native.RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public int PointerX, PointerY, PointerVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TEX2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MAPPED
        {
            public IntPtr pData;
            public uint RowPitch, DepthPitch;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputsFn(IntPtr self, uint index, out IntPtr output);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutputGetDescFn(IntPtr self, out DXGI_OUTPUT_DESC desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int DuplicateOutputFn(IntPtr self, IntPtr device, out IntPtr dupl);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AcquireNextFrameFn(IntPtr self, uint timeoutMs, out FRAME_INFO info, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseFrameFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DFn(IntPtr self, ref TEX2D_DESC desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSrvFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr srv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopySubresourceRegionFn(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, IntPtr box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void GenerateMipsFn(IntPtr self, IntPtr srv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FlushFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(IntPtr self, IntPtr res, uint sub, int mapType, uint flags, out MAPPED mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void TexGetDescFn(IntPtr self, out TEX2D_DESC desc);

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        private static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private const int FORMAT_B8G8R8A8_UNORM = 87;
        private const int FORMAT_B8G8R8A8_UNORM_SRGB = 91;

        // ---- state ------------------------------------------------------------------
        private IntPtr _device, _context, _output1, _dupl, _mipTex, _srv;
        private readonly IntPtr[] _staging = new IntPtr[2]; // double-buffered so we never wait on the GPU
        private int _writeSlot, _pendingSlot = -1;
        private readonly int _w, _h, _level, _lw, _lh;
        private bool _hasData, _validated, _broken;

        private AcquireNextFrameFn _acquire;
        private ReleaseFrameFn _releaseFrame;
        private CopySubresourceRegionFn _copy;
        private GenerateMipsFn _genMips;
        private FlushFn _flush;
        private MapFn _map;
        private UnmapFn _unmap;

        private DxgiCapture(Rectangle bounds, int level, int lw, int lh)
        {
            _w = bounds.Width; _h = bounds.Height; _level = level; _lw = lw; _lh = lh;
        }

        public static DxgiCapture TryCreate(Rectangle bounds, int level, int lw, int lh)
        {
            DxgiCapture c = null;
            try
            {
                c = new DxgiCapture(bounds, level, lw, lh);
                if (c.Init(bounds)) return c;
            }
            catch (Exception ex) { Log.Write("DXGI init error: " + ex.Message); }
            if (c != null) c.Dispose();
            return null;
        }

        private static IntPtr Slot(IntPtr obj, int i) { return Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), i * IntPtr.Size); }
        private static T Fn<T>(IntPtr obj, int i) where T : class
        {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Slot(obj, i), typeof(T));
        }
        private static void Release(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            Fn<ReleaseFn>(p, 2)(p);
            p = IntPtr.Zero;
        }
        private static IntPtr QI(IntPtr obj, Guid iid)
        {
            IntPtr r;
            return Fn<QueryInterfaceFn>(obj, 0)(obj, ref iid, out r) >= 0 ? r : IntPtr.Zero;
        }

        private bool Init(Rectangle bounds)
        {
            IntPtr factory;
            Guid iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0) return false;
            try
            {
                for (uint a = 0; a < 16; a++)
                {
                    IntPtr adapter;
                    if (Fn<EnumAdapters1Fn>(factory, 12)(factory, a, out adapter) < 0) break;
                    try
                    {
                        for (uint o = 0; o < 16; o++)
                        {
                            IntPtr output;
                            if (Fn<EnumOutputsFn>(adapter, 7)(adapter, o, out output) < 0) break;
                            try
                            {
                                DXGI_OUTPUT_DESC d;
                                if (Fn<OutputGetDescFn>(output, 7)(output, out d) < 0) continue;
                                Native.RECT r = d.DesktopCoordinates;
                                if (d.AttachedToDesktop == 0 || r.Left != bounds.Left || r.Top != bounds.Top ||
                                    r.Width != bounds.Width || r.Height != bounds.Height) continue;
                                if (d.Rotation > 1)
                                {
                                    Log.Write("monitor " + bounds + " is rotated; using GDI capture");
                                    return false;
                                }
                                return Setup(adapter, output);
                            }
                            finally { Release(ref output); }
                        }
                    }
                    finally { Release(ref adapter); }
                }
            }
            finally { Release(ref factory); }
            return false;
        }

        private bool Setup(IntPtr adapter, IntPtr output)
        {
            int featureLevel;
            int hr = D3D11CreateDevice(adapter, 0 /* UNKNOWN, adapter given */, IntPtr.Zero, 0x20 /* BGRA */,
                IntPtr.Zero, 0, 7, out _device, out featureLevel, out _context);
            if (hr < 0) { Log.Write("D3D11CreateDevice failed 0x" + hr.ToString("X8")); return false; }

            _output1 = QI(output, IID_IDXGIOutput1);
            if (_output1 == IntPtr.Zero) return false;

            var mip = new TEX2D_DESC
            {
                Width = (uint)_w, Height = (uint)_h, MipLevels = (uint)(_level + 1), ArraySize = 1,
                Format = FORMAT_B8G8R8A8_UNORM, SampleCount = 1, Usage = 0 /* DEFAULT */,
                BindFlags = 0x8 | 0x20 /* SHADER_RESOURCE | RENDER_TARGET */, MiscFlags = 0x1 /* GENERATE_MIPS */
            };
            hr = Fn<CreateTexture2DFn>(_device, 5)(_device, ref mip, IntPtr.Zero, out _mipTex);
            if (hr < 0) { Log.Write("mip texture failed 0x" + hr.ToString("X8")); return false; }
            hr = Fn<CreateSrvFn>(_device, 7)(_device, _mipTex, IntPtr.Zero, out _srv);
            if (hr < 0) { Log.Write("SRV failed 0x" + hr.ToString("X8")); return false; }

            var st = new TEX2D_DESC
            {
                Width = (uint)_lw, Height = (uint)_lh, MipLevels = 1, ArraySize = 1,
                Format = FORMAT_B8G8R8A8_UNORM, SampleCount = 1, Usage = 3 /* STAGING */, CPUAccessFlags = 0x20000 /* READ */
            };
            for (int i = 0; i < 2; i++)
            {
                hr = Fn<CreateTexture2DFn>(_device, 5)(_device, ref st, IntPtr.Zero, out _staging[i]);
                if (hr < 0) { Log.Write("staging texture failed 0x" + hr.ToString("X8")); return false; }
            }

            _copy = Fn<CopySubresourceRegionFn>(_context, 46);
            _map = Fn<MapFn>(_context, 14);
            _unmap = Fn<UnmapFn>(_context, 15);
            _genMips = Fn<GenerateMipsFn>(_context, 54);
            _flush = Fn<FlushFn>(_context, 111); // ID3D11DeviceContext::Flush

            return CreateDuplication();
        }

        private bool CreateDuplication()
        {
            Release(ref _dupl);
            int hr = Fn<DuplicateOutputFn>(_output1, 22)(_output1, _device, out _dupl);
            if (hr < 0)
            {
                _dupl = IntPtr.Zero;
                return false;
            }
            _acquire = Fn<AcquireNextFrameFn>(_dupl, 8);
            _releaseFrame = Fn<ReleaseFrameFn>(_dupl, 14);
            _hasData = false;
            return true;
        }

        /// <summary>
        /// Non-blocking: a new desktop image is downscaled on the GPU into one staging texture, and read back on a
        /// later call once the GPU has finished (about one frame of latency, invisible behind the color easing).
        /// </summary>
        /// <returns>1 = new pixels written to dst, 0 = unchanged, -1 = nothing usable yet</returns>
        public int Capture(byte[] dst)
        {
            if (_broken) return -1;
            if (_dupl == IntPtr.Zero && !CreateDuplication()) return -1;

            // 1) Collect the image queued on an earlier call (the GPU has had a whole frame to finish it).
            int result = TryRead(dst);

            // 2) Queue the newest desktop image, if there is one, for the next call.
            FRAME_INFO info;
            IntPtr resource;
            int hr = _acquire(_dupl, 0, out info, out resource);
            if (hr == DXGI_ERROR_ACCESS_LOST) { Release(ref _dupl); _pendingSlot = -1; return result; }
            if (hr >= 0)
            {
                try
                {
                    // LastPresentTime == 0: mouse-only update, or the blank first frame - no new image.
                    if (info.LastPresentTime != 0) Submit(resource);
                }
                finally
                {
                    Release(ref resource);
                    _releaseFrame(_dupl);
                }
            }
            return result;
        }

        /// <summary>Queue: desktop -> mip chain -> small staging texture. Nothing here waits.</summary>
        private bool Submit(IntPtr resource)
        {
            IntPtr tex = QI(resource, IID_ID3D11Texture2D);
            if (tex == IntPtr.Zero) return false;
            try
            {
                if (!_validated)
                {
                    TEX2D_DESC d;
                    Fn<TexGetDescFn>(tex, 10)(tex, out d);
                    if (d.Width != _w || d.Height != _h ||
                        (d.Format != FORMAT_B8G8R8A8_UNORM && d.Format != FORMAT_B8G8R8A8_UNORM_SRGB))
                    {
                        Log.Write("unexpected desktop texture " + d.Width + "x" + d.Height + " fmt " + d.Format);
                        _broken = true;
                        return false;
                    }
                    _validated = true;
                }
                _copy(_context, _mipTex, 0, 0, 0, 0, tex, 0, IntPtr.Zero);
            }
            finally { Release(ref tex); }

            _genMips(_context, _srv);
            if (_pendingSlot == _writeSlot) _writeSlot ^= 1;
            _copy(_context, _staging[_writeSlot], 0, 0, 0, 0, _mipTex, (uint)_level, IntPtr.Zero);
            _flush(_context); // hand the work to the GPU now, so it's done by the time we read it back
            _pendingSlot = _writeSlot;
            _writeSlot ^= 1;
            return true;
        }

        private int TryRead(byte[] dst)
        {
            if (_pendingSlot < 0) return _hasData ? 0 : -1;
            IntPtr staging = _staging[_pendingSlot];
            // This slot was queued (and flushed) a frame ago, so the GPU is long done and Map returns at once.
            // (MAP_FLAG_DO_NOT_WAIT would be the textbook choice, but some drivers report "still drawing" forever.)
            MAPPED mapped;
            int hr = _map(_context, staging, 0, 1 /* READ */, 0, out mapped);
            if (hr < 0)
            {
                if (AmbientEngine.Debug) Log.Write("debug: staging map failed 0x" + hr.ToString("X8"));
                return -1;
            }
            try
            {
                int rowBytes = _lw * 4;
                for (int y = 0; y < _lh; y++)
                    Marshal.Copy(new IntPtr(mapped.pData.ToInt64() + (long)y * mapped.RowPitch), dst, y * rowBytes, rowBytes);
            }
            finally { _unmap(_context, staging, 0); }
            _pendingSlot = -1;
            _hasData = true;
            return 1;
        }

        public void Dispose()
        {
            Release(ref _dupl);
            Release(ref _staging[0]);
            Release(ref _staging[1]);
            Release(ref _srv);
            Release(ref _mipTex);
            Release(ref _output1);
            Release(ref _context);
            Release(ref _device);
        }
    }
}
