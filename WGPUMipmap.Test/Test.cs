using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;

namespace WGPUMipmap.Test;

public unsafe class Test
{
    private WebGPU wgpu;
    private Instance* instance;
    private Adapter* adapter;
    private Device* device;
    
    private void InitWGPU()
    {
        // - API
        wgpu = WebGPU.GetApi();
        Wgpu nativeWgpu = new Wgpu(wgpu.Context);
        
        // - INSTANCE
        InstanceDescriptor instanceDescriptor = new InstanceDescriptor();
        instance = wgpu.CreateInstance(in instanceDescriptor);
        
        // - ADAPTER
        PfnRequestAdapterCallback pfnRequestAdapterCallback = PfnRequestAdapterCallback.From((status, adapter, msg, userData) =>
        {
            if (status == RequestAdapterStatus.Success)
            {
                this.adapter = adapter;
            }
            else
            {
                string strMsg = Marshal.PtrToStringUTF8((IntPtr)msg)!;
                Debug.Print(strMsg);
            }
        });

        RequestAdapterOptions requestAdapterOptions = new RequestAdapterOptions();
        requestAdapterOptions.BackendType = BackendType.Vulkan; // Vulkan works for Windows/Linux.
        requestAdapterOptions.CompatibleSurface = null;
        requestAdapterOptions.PowerPreference = PowerPreference.HighPerformance;

        wgpu.InstanceRequestAdapter(instance, &requestAdapterOptions, pfnRequestAdapterCallback, null);
        
        // - DEVICE
        PfnRequestDeviceCallback pfnRequestDeviceCallback = PfnRequestDeviceCallback.From((status, device, msg, userData) =>
        {
            if (status == RequestDeviceStatus.Success)
            {
                this.device = device;
            }
            else
            {
                string strMsg = Marshal.PtrToStringUTF8((IntPtr)msg)!;
                Debug.Print(strMsg);
            }
        });

        DeviceDescriptor deviceDescriptor = new DeviceDescriptor();
        deviceDescriptor.Label = (byte*)Marshal.StringToHGlobalAnsi("Device descriptor.");

        wgpu.AdapterRequestDevice(adapter, in deviceDescriptor, pfnRequestDeviceCallback, null);
    }
    
    [Fact]
    public void Create_rgba8unorm_format()
    {
        InitWGPU();
        
        MipmapGenerator mipmapGenerator = new MipmapGenerator(wgpu, device);

        byte[] bytes = new byte[] { 
            255, 255, 255, 255,
            255, 255, 255, 255,
            255, 255, 255, 255,
            255, 255, 255, 255,
        };
        fixed (byte* bytePtr = bytes)
        {
            TextureMipmapResult result = mipmapGenerator.CreateTexture2DWithMipmaps(bytePtr, 2, 2, TextureFormat.Rgba8Unorm);
            Assert.True(result.Texture != null);
            Assert.Equal<uint>(2, result.MipLevelCount);
        }
        
        mipmapGenerator.Dispose();
    }
    
    [Fact]
    public void Create_bgra8unorm_format()
    {
        InitWGPU();

        MipmapGenerator mipmapGenerator = new MipmapGenerator(wgpu, device);

        byte[] bytes = new byte[] {
            255, 255, 255, 255,
            255, 255, 255, 255,
            255, 255, 255, 255,
            255, 255, 255, 255,
        };
        fixed (byte* bytePtr = bytes)
        {
            TextureMipmapResult result = mipmapGenerator.CreateTexture2DWithMipmaps(bytePtr, 2, 2, TextureFormat.Bgra8Unorm);
            Assert.True(result.Texture != null);
            Assert.Equal<uint>(2, result.MipLevelCount);
        }

        mipmapGenerator.Dispose();
    }
}