using Silk.NET.WebGPU;
using System.Runtime.InteropServices;
using WGPUBuffer = Silk.NET.WebGPU.Buffer;

// - FALLBACK is used only for unsupported formats, because we cannot use texture_storage_2d for those and generate textures with Compute shaders.
// - here first 'rgba8unorm' texture is generated with mipmaps.
// - it is used to create some other texture format.

namespace WGPUMipmap
{
    /// <summary>
    /// Generates images for a given texture. 
    /// Because we commonly use TextureStorage textures which have some unsupported format, we use fallback which 
    /// has workaround by creating 'rgba8unorm' and then from that copying to 'bgra8unorm' or other formats.
    /// </summary>
    internal unsafe class FallbackMipmapGenerator
    {
        /// <summary>
        /// Private class to just pass data around.
        /// </summary>
        class BufferDataDto
        {
            /// <summary>
            /// The bytes data. Set as bytes since it's easier to work with.
            /// </summary>
            internal uint[] Bytes { get; set; }

            /// <summary>
            /// The width of an image pixels.
            /// </summary>
            internal uint Width { get; set; }

            /// <summary>
            /// The height of an image pixels.
            /// </summary>
            internal uint Height { get; set; }

        }

        private readonly WebGPU api;
        private readonly Device* device;
        private readonly MipmapGenerator mipmapGenerator;
        private TextureFormat textureFormat;
        private List<BufferDataDto> bufferDataList = new List<BufferDataDto>();

        /// <summary>
        /// The constructor.
        /// </summary>
        /// <param name="api">The <see cref="WebGPU"/> api.</param>
        /// <param name="device">The pointer to the <see cref="Device"/>.</param>
        /// <param name="mipmapGenerator">The mipmap generator.</param>
        internal FallbackMipmapGenerator(WebGPU api, Device* device, MipmapGenerator mipmapGenerator)
        {
            this.api = api;
            this.device = device;
            this.mipmapGenerator = mipmapGenerator;
        }

        /// <summary>
        /// Reads the data into the <see cref="bufferDataList"/>.
        /// </summary>
        /// <param name="rgba8Texture">The generated rgba8 texture with mipmaps.</param>
        private void ReadData(Texture* rgba8Texture)
        {
            Queue* queue = api.DeviceGetQueue(device);

            for (uint i = 0; i < mipmapGenerator.TextureViews.Length; i++)
            {
                Extent3D size = mipmapGenerator.TextureMipSizes[i];

                // - SET INFO 
                uint bytesPerRow = 4 * size.Width;
                uint paddedBytesPerRow = Math.Max(bytesPerRow, 256);
                if (paddedBytesPerRow > 256)
                {
                    paddedBytesPerRow = paddedBytesPerRow + (256 - (paddedBytesPerRow % 256)); // must be multiple of 256
                }

                // - BUFFER TO GET PIXELS
                BufferDescriptor bufferDescriptor = new BufferDescriptor();
                bufferDescriptor.Label = (byte*)Marshal.StringToHGlobalAnsi($"Mip level {i} buffer");
                bufferDescriptor.Size = paddedBytesPerRow * size.Height;
                bufferDescriptor.Usage = BufferUsage.CopyDst | BufferUsage.MapRead;
                bufferDescriptor.MappedAtCreation = false;
                WGPUBuffer* pixelsBuffer = api.DeviceCreateBuffer(device, bufferDescriptor);

                // - START ENCODING
                CommandEncoder* commandEncoder = api.DeviceCreateCommandEncoder(device, null);

                // - COPY TEXTURE TO BUFFER
                ImageCopyTexture source = new ImageCopyTexture();
                source.Texture = rgba8Texture;
                source.MipLevel = i;
                source.Origin = new Origin3D(0, 0, 0);

                ImageCopyBuffer destination = new ImageCopyBuffer();
                destination.Buffer = pixelsBuffer;
                destination.Layout.BytesPerRow = paddedBytesPerRow;
                destination.Layout.RowsPerImage = size.Height;

                this.api.CommandEncoderCopyTextureToBuffer(commandEncoder, source, destination, size);

                // Submit 
                CommandBuffer* buffer = api.CommandEncoderFinish(commandEncoder, null);
                api.QueueSubmit(queue, 1, &buffer);

                // - MAP AND READ PIXELS
                bool read = false;
                PfnBufferMapCallback callback = PfnBufferMapCallback.From((status, userData) =>
                {
                    read = true;
                    uint* pixels = (uint*)api.BufferGetMappedRange(pixelsBuffer, 0, paddedBytesPerRow * size.Height);

                    BufferDataDto bufferDataEntry = new BufferDataDto()
                    {
                        Bytes = new uint[size.Width * size.Height],
                        Width = size.Width,
                        Height = size.Height,
                    };

                    // We do not want to copy padding.
                    for (int y = 0; y < size.Height; y++)
                    {
                        int row = (int)size.Height * y;
                        // Divice paddedBytesPerRow with 4, since we do not care about byte size.
                        int paddedRow = (int)paddedBytesPerRow / 4 * y;
                        for (int x = 0; x < size.Width; x++)
                        {
                            bufferDataEntry.Bytes[row + x] = pixels[paddedRow + x];
                        }
                    }

                    bufferDataList.Add(bufferDataEntry);

                    // - UNMAP
                    this.api.BufferUnmap(pixelsBuffer);

                });
                api.BufferMapAsync(pixelsBuffer, MapMode.Read, 0, paddedBytesPerRow * size.Height, callback, null);

                while (!read)
                {

                    // Submit queu
                    this.api.QueueSubmit(queue, 0, null);
                }

                this.api.BufferDestroy(pixelsBuffer);
            }
        }

        /// <summary>
        /// Creates the texture with mipmaps.
        /// </summary>
        /// <returns>The pointer to the <see cref="Texture"/>.</returns>
        private Texture* CreateTexture()
        {
            Queue* queue = api.DeviceGetQueue(device);

            // - CREATE TEXTURE
            TextureDescriptor textureDescriptor = new TextureDescriptor();
            textureDescriptor.Usage = TextureUsage.CopyDst | TextureUsage.TextureBinding;
            textureDescriptor.Dimension = TextureDimension.Dimension2D;
            textureDescriptor.Format = textureFormat;
            textureDescriptor.MipLevelCount = (uint)mipmapGenerator.TextureMipSizes.Length;
            textureDescriptor.SampleCount = 1;
            textureDescriptor.Size = new Extent3D(bufferDataList[0].Width, bufferDataList[0].Height, 1);

            Texture* finalTexture = this.api.DeviceCreateTexture(device, in textureDescriptor);

            // - HANDLE MIPMAPS
            for (uint i = 0; i < mipmapGenerator.TextureMipSizes.Length; i++)
            {
                // - COPY DATA
                ImageCopyTexture destination = new ImageCopyTexture();
                destination.Texture = finalTexture;
                destination.Origin = new Origin3D(0, 0, 0);
                destination.Aspect = TextureAspect.All;
                destination.MipLevel = i;

                BufferDataDto bufferData = bufferDataList[(int)i];
                Extent3D size = mipmapGenerator.TextureMipSizes[i];

                uint bytesPerRow = 4 * bufferData.Width;

                TextureDataLayout source = new TextureDataLayout();
                source.Offset = 0;
                source.BytesPerRow = bytesPerRow;
                source.RowsPerImage = bufferData.Height;

                uint dataSize = bytesPerRow * bufferData.Height;

                fixed (uint* bytePtr = bufferData.Bytes)
                {
                    this.api.QueueWriteTexture(queue, in destination, bytePtr, dataSize, in source, in size);
                }
            }

            return finalTexture;
        }

        /// <summary>
        /// Generates the 2d texture with mipmaps.
        /// </summary>
        /// <param name="data">The pointer to the data.</param>
        /// <param name="width">The width of an image data owner.</param>
        /// <param name="height">The height of an image data owner.</param>
        /// <returns>The <see cref="TextureMipmapResult"/>.</returns>
        internal TextureMipmapResult CreateTexture2DWithMipmaps(void* data, uint width, uint height, TextureFormat textureFormat)
        {
            // - PREPARE
            this.textureFormat = textureFormat;
            bufferDataList.Clear();

            // First generate a texture with a common format supported by storage_texture_2d.
            TextureMipmapResult rgba8TextureResult = this.mipmapGenerator.CreateTexture2DWithMipmaps(data, width, height, TextureFormat.Rgba8Unorm);
            Texture* rgba8Texture = rgba8TextureResult.Texture;

            // - READ FROM RGBA8 Texture
            // Now we read texture data for each mipmap.
            ReadData(rgba8Texture); 

            // - CREATE
            // from read data create a new texture.
            Texture* texture = CreateTexture();

            // - RELEASE
            this.mipmapGenerator.Dispose();
            this.api.TextureDestroy(rgba8Texture);

            // Return result.
            return new TextureMipmapResult()
            {
                Texture = texture,
                MipLevelCount = rgba8TextureResult.MipLevelCount,
            };
        }
    }
}
