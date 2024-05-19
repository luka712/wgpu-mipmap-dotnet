using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Silk.NET.WebGPU;

namespace WGPUMipmap;

/// <summary>
/// WebGPU extensions.
/// </summary>
public static class WebGPUApiExtensions
{
    /// <summary>
    /// Creates the 2D texture with mipmaps.
    /// </summary>
    /// <param name="api">The <see cref="WebGPU"/>.</param>
    /// <param name="device">The pointer to the <see cref="Device"/>.</param>
    /// <param name="data">The pointer to the data.</param>
    /// <param name="width">The width of a data.</param>
    /// <param name="height">The height of a data.</param>
    /// <returns>The pointer to the <see cref="Texture"/>.</returns>
    public static unsafe Texture* DeviceCreateTextureWithMipmap(this WebGPU api, Device* device, void* data, uint width, uint height)
    {
        using MipmapGenerator generator = new MipmapGenerator(api, device);
        return generator.GenerateMipmaps(data, width, height);
    }
}

/// <summary>
/// The class responsible for generating mipmaps.
/// </summary>
public unsafe class MipmapGenerator : IDisposable
{
    private const string SHADER = @"
        @group(0) @binding(0)
        var t_base: texture_2d<f32>;
        @group(0) @binding(1)
        var t_next: texture_storage_2d<rgba8unorm, write>;

        @compute @workgroup_size(8,8)
        fn generateMipmap(@builtin(global_invocation_id) gid: vec3<u32>)
        {
            // 2x2 average.
            var color = textureLoad(t_base, 2u * gid.xy, 0);
            color += textureLoad(t_base, 2u * gid.xy + vec2<u32>(1, 0), 0);
            color += textureLoad(t_base, 2u * gid.xy + vec2<u32>(0, 1), 0);
            color += textureLoad(t_base, 2u * gid.xy + vec2<u32>(1, 1), 0);
            color /= 4.0;

            // Write to the next level.
            textureStore(t_next, gid.xy, color);
        }";
    
    private readonly WebGPU api;
    private readonly Device* device;

    private ComputePipeline* pipeline;
    private Texture* texture;
    private BindGroupLayout* bindGroupLayout;
    private BindGroup* bindGroup;

    private Extent3D[] textureMipSizes;
    private TextureView*[] textureViews;

    /// <summary>
    /// The constructor.
    /// </summary>
    /// <param name="api">The <see cref="WebGPU"/> api.</param>
    /// <param name="device">The pointer to the <see cref="Device"/>.</param>
    public MipmapGenerator(WebGPU api, Device* device)
    {
        this.api = api;
        this.device = device;
    }
    
    /// <summary>
    /// Generates the 2d texture with mipmaps.
    /// </summary>
    /// <param name="data">The pointer to the data.</param>
    /// <param name="width">The width of an image data owner.</param>
    /// <param name="height">The height of an image data owner.</param>
    /// <returns>The pointer to the <see cref="Texture"/>.</returns>
    public Texture* GenerateMipmaps(void* data, uint width, uint height)
    {
        InitTexture(data, width, height);
        InitTextureViews();
        InitBindGroupLayout();
        InitPipeline();
        ComputePass();

        return this.texture;
    }

    private void InitTexture(void* data, uint width, uint height)
    {
        // - TEXTURE
        Extent3D size = new Extent3D(width, height, 1);

        // Need to upload/save input data and read/write in shader.
        TextureUsage usage = TextureUsage.CopyDst
                             | TextureUsage.CopySrc
                             | TextureUsage.TextureBinding
                             | TextureUsage.RenderAttachment;

        TextureDescriptor descriptor = new TextureDescriptor();
        descriptor.Dimension = TextureDimension.Dimension2D;
        descriptor.Size = size;
        descriptor.Format = TextureFormat.Rgba8Unorm;
        descriptor.SampleCount = 1;
        descriptor.Usage = usage;

        // Find the number of mip levels. 
        uint maxDimension = Math.Max(width, height);
        uint maxMip = (uint)Math.Log2(maxDimension) + 1;
        descriptor.MipLevelCount = maxMip;

        // Save size.
        this.textureMipSizes = new Extent3D[maxMip];
        this.textureMipSizes[0] = size;

        // Create the texture.
        this.texture = this.api.DeviceCreateTexture(device, in descriptor);

        // - COPY DATA
        ImageCopyTexture destination = new ImageCopyTexture();
        destination.Texture = this.texture;
        destination.Origin = new Origin3D(0, 0, 0);
        destination.Aspect = TextureAspect.All;
        destination.MipLevel = 0;

        TextureDataLayout source = new TextureDataLayout();
        source.Offset = 0;
        source.BytesPerRow = 4 * width;
        source.RowsPerImage = height;

        Queue* queue = this.api.DeviceGetQueue(this.device);

        uint dataSize = width * height * 4;
        this.api.QueueWriteTexture(queue, in destination, data, dataSize, in source, in size);
    }

    /// <summary>
    /// Initialize the texture views for mipmaps.
    /// </summary>
    /// <exception cref="InvalidOperationException">If texture view could not be created.</exception>
    private void InitTextureViews()
    {
        TextureViewDescriptor descriptor = new TextureViewDescriptor();
        descriptor.Format = TextureFormat.Rgba8Unorm;
        descriptor.Dimension = TextureViewDimension.Dimension2D;
        descriptor.Aspect = TextureAspect.All;
        descriptor.BaseArrayLayer = 0;
        descriptor.ArrayLayerCount = 1;
        descriptor.BaseMipLevel = 0;
        descriptor.MipLevelCount = 1;

        this.textureViews = new TextureView*[this.textureMipSizes.Length];
        for (uint i = 0; i < this.textureMipSizes.Length; i++)
        {
            descriptor.BaseMipLevel = i;
            descriptor.Label = (byte*)Marshal.StringToHGlobalAnsi($"Mip level {i}");

            TextureView* view = this.api.TextureCreateView(this.texture, in descriptor);

            if (view is null)
            {
                throw new InvalidOperationException($"Unable to create mip level {i}.");
            }

            this.textureViews[i] = view;

            if (i > 0)
            {
                Extent3D previousSize = this.textureMipSizes[i - 1];
                this.textureMipSizes[i] = new Extent3D(
                    width: Math.Max(1, previousSize.Width / 2),
                    height: Math.Max(1, previousSize.Height / 2),
                    depthOrArrayLayers: 1
                );
            }
        }
    }

    /// <summary>
    /// Initialize the bind group layout for pipeline.
    /// </summary>
    private void InitBindGroupLayout()
    {
        // Release layout if it's not null.
        if (this.bindGroupLayout != null)
        {
            this.api.BindGroupLayoutRelease(this.bindGroupLayout);
        }

        BindGroupLayoutDescriptor descriptor = new BindGroupLayoutDescriptor();
        descriptor.EntryCount = 2;

        BindGroupLayoutEntry* entries = stackalloc BindGroupLayoutEntry[2];

        // - ENTRIES 
        // -- BASE TEXTURE - to read from
        entries[0] = new BindGroupLayoutEntry();
        entries[0].Binding = 0;
        entries[0].Visibility = ShaderStage.Compute;
        entries[0].Texture.SampleType = TextureSampleType.Float;
        entries[0].Texture.ViewDimension = TextureViewDimension.Dimension2D;

        // -- WRITE TEXTURE - to write to
        entries[1] = new BindGroupLayoutEntry();
        entries[1].Binding = 1;
        entries[1].Visibility = ShaderStage.Compute;
        entries[1].StorageTexture.Access = StorageTextureAccess.WriteOnly;
        entries[1].StorageTexture.Format = TextureFormat.Rgba8Unorm;
        entries[1].StorageTexture.ViewDimension = TextureViewDimension.Dimension2D;

        descriptor.Entries = entries;

        this.bindGroupLayout = this.api.DeviceCreateBindGroupLayout(this.device, in descriptor);
    }

    private void InitBindGroup(uint nextMipLevel)
    {
        if (this.bindGroup != null)
        {
            this.api.BindGroupRelease(this.bindGroup);
        }
        
        BindGroupDescriptor descriptor = new BindGroupDescriptor();
        descriptor.Layout = this.bindGroupLayout;

        // - ENTRIES
        BindGroupEntry* entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry();
        entries[0].Binding = 0;
        entries[0].TextureView = this.textureViews[nextMipLevel - 1];

        entries[0] = new BindGroupEntry();
        entries[0].Binding = 1;
        entries[0].TextureView = this.textureViews[nextMipLevel];

        this.bindGroup = this.api.DeviceCreateBindGroup(this.device, in descriptor);
    }

    /// <summary>
    /// Creates the compute pipeline.
    /// </summary>
    private void InitPipeline()
    {
        // - SHADER MODULE
        ShaderModuleWGSLDescriptor shaderModuleWGSLDescriptor = new ShaderModuleWGSLDescriptor();
        shaderModuleWGSLDescriptor.Chain.Next = null;
        shaderModuleWGSLDescriptor.Chain.SType = SType.ShaderModuleWgsldescriptor;
        shaderModuleWGSLDescriptor.Code = (byte*)Marshal.StringToHGlobalAnsi(SHADER);

        ShaderModuleDescriptor shaderModuleDescriptor = new ShaderModuleDescriptor();
        shaderModuleDescriptor.Label = (byte*)Marshal.StringToHGlobalAnsi("Mipmap generator shader module");
        shaderModuleDescriptor.NextInChain = (ChainedStruct*)&shaderModuleWGSLDescriptor;
        
        ShaderModule* shaderModule = this.api.DeviceCreateShaderModule(this.device, in shaderModuleDescriptor);
        
        // - PIPELINE LAYOUT
        PipelineLayoutDescriptor pipelineLayoutDescriptor = new PipelineLayoutDescriptor();
        BindGroupLayout** layouts = stackalloc BindGroupLayout*[1];
        layouts[0] = this.bindGroupLayout;
        pipelineLayoutDescriptor.BindGroupLayouts = layouts;
        pipelineLayoutDescriptor.BindGroupLayoutCount = 1;

        PipelineLayout* layout = this.api.DeviceCreatePipelineLayout(this.device, in pipelineLayoutDescriptor);
        
        // - PIPELINE
        ComputePipelineDescriptor descriptor = new ComputePipelineDescriptor();
        descriptor.Layout = layout;
        descriptor.Compute.Module = shaderModule;
        descriptor.Compute.EntryPoint = (byte*)Marshal.StringToHGlobalAnsi("generateMipmap");

        this.pipeline = this.api.DeviceCreateComputePipeline(this.device, in descriptor);
        
        this.api.PipelineLayoutRelease(layout);
        this.api.ShaderModuleRelease(shaderModule);
    }

    /// <summary>
    /// The compute pass.
    /// </summary>
    private void ComputePass()
    {
        Queue* queue = this.api.DeviceGetQueue(this.device);
        
        // - COMMAND ENCODER
        CommandEncoder* commandEncoder = this.api.DeviceCreateCommandEncoder(this.device, null);
        
        // - COMPUTE PASS
        ComputePassEncoder* computePass = this.api.CommandEncoderBeginComputePass(commandEncoder, null);
        this.api.ComputePassEncoderSetPipeline(computePass, this.pipeline);

        for (uint nextMipLevel = 1; nextMipLevel < this.textureMipSizes.Length; nextMipLevel++)
        {
            // -- BIND
            this.InitBindGroup(nextMipLevel);
            this.api.ComputePassEncoderSetBindGroup(computePass, 0, this.bindGroup, 0, 0);
            
            // -- DISPATCH
            Extent3D size = this.textureMipSizes[nextMipLevel];
            uint workgroupSizePerDimension = 8;
            uint workgroupCountX = (size.Width * workgroupSizePerDimension - 1) / workgroupSizePerDimension;
            uint workgroupCountY = (size.Height + workgroupSizePerDimension - 1) / workgroupSizePerDimension;
            
            this.api.ComputePassEncoderDispatchWorkgroups(computePass, workgroupCountX, workgroupCountY, 1);
        }
        
        // - END PASS
        this.api.ComputePassEncoderEnd(computePass);
        
        // - SUBMIT
        CommandBuffer* commandBuffer = this.api.CommandEncoderFinish(commandEncoder, null);
        this.api.QueueSubmit(queue, 1, ref commandBuffer);
        
        this.api.CommandBufferRelease(commandBuffer);
        this.api.ComputePassEncoderRelease(computePass);
        this.api.CommandEncoderRelease(commandEncoder);
    }
    

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.bindGroup != null)
        {
            this.api.BindGroupRelease(this.bindGroup);
        }
        
        if (this.bindGroupLayout != null)
        {
            this.api.BindGroupLayoutRelease(this.bindGroupLayout);
        }
    }
}