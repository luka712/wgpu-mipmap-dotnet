using Silk.NET.WebGPU;

namespace WGPUMipmap
{
    /// <summary>
    /// The result of calls that generate texture with mipmap.
    /// </summary>
    public unsafe class TextureMipmapResult
    {
        /// <summary>
        /// The pointer to the texture.
        /// </summary>
        public Texture* Texture { get; internal set; } 

        /// <summary>
        /// The count of mip levels.
        /// </summary>
        public uint MipLevelCount { get; internal set; }
    }
}
