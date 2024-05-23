# wgpu-mipmap-dotnet

The mipmap generator for WebGPU with Silk.Net bindings.

### Usage

To use simply create `Texture` using `DeviceCreateTextureWithMipmap`. 

```csharp
MipmapGenerator mipmapGenerator = new MipmapGenerator(wgpu, device);
TextureFormat format = TextureFormat.Rgba8Unorm;
Texture* texture = mipmapGenerator.CreateTexture2DWithMipmaps(yourGPUDevice, bytePtr, 1, 1, format);
```

## Known issues

---

Mipmap generator uses compute shader and storage texture to write mipmaps. This might be an issue for certain texture formats,
most notably `bgra8unorm`. To use it require feature `bgra8unorm-storage` when creating WebGPU device. Should be passed to `RequireFeatures`
property of `DeviceDescriptor`. 


## Version

---
### 0.1.0
- Initial release.

### 0.2.0
- Support for 'bgra8unorm' texture format.