# wgpu-mipmap-dotnet

The mipmap generator for WebGPU with Silk.Net bindings.

### Usage

To use simply create `Texture` using `CreateTexture2DWithMipmaps`. 

```csharp
MipmapGenerator mipmapGenerator = new MipmapGenerator(wgpu, device);
TextureFormat format = TextureFormat.Rgba8Unorm;
TextureMipmapResult result = mipmapGenerator.CreateTexture2DWithMipmaps(bytePtr, 2, 2, TextureFormat.Rgba8Unorm);
Texture* texture = result.Texture;
uint mipLevelCount = result.MipLevelCount;
```

## Known issues

---

Mipmap generator uses compute shader and storage texture to write mipmaps. This might be an issue for certain texture formats,
most notably `bgra8unorm`. To use it require feature `bgra8unorm-storage` when creating WebGPU device. Should be passed to `RequireFeatures`
property of `DeviceDescriptor`. 
If `bgra8unorm-storage` is not available pass takes somewhat longer due to fallback solution which creates texture with supported formats first and then copies it.


## Version

---
### 0.1.0
- Initial release.

### 0.2.0
- Support for 'bgra8unorm' texture format.

### 0.3.0
- rename of methods to 'CreateTexture2DWithMipmaps'.
- better support for formats not suppored by 'texture_storage_2d'.