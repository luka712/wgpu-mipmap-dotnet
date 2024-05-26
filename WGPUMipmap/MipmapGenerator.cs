using System.Runtime.InteropServices;
using Silk.NET.WebGPU;

namespace WGPUMipmap;

/// <summary>
/// The class responsible for generating mipmaps.
/// </summary>
public unsafe class MipmapGenerator : IDisposable
{
    private const string TEXTURE_FORMAT_KEY = "##TEXTURE_FORMAT##";

    private const string SHADER = @"@group(0) @binding(0)
        var t_base: texture_2d<f32>;
        @group(0) @binding(1)
        var t_next: texture_storage_2d<##TEXTURE_FORMAT##, write>;

        @compute @workgroup_size(8,8)
        fn generateMipmap(@builtin(global_invocation_id) gid: vec3<u32>)
        {
            // 2x2 average.
            var color = textureLoad(t_base, 2u * gid.xy, 0);
            color += textureLoad(t_base, 2u * gid.xy + vec2<u32>(1u, 0u), 0);
            color += textureLoad(t_base, 2u * gid.xy + vec2<u32>(0u, 1u), 0);
            color += textureLoad(t_base, 2u * gid.xy + vec2<u32>(1u, 1u), 0);
            color /= 4.0;

            // Write to the next level.
            textureStore(t_next, gid.xy, color);
        }";

    private Dictionary<TextureFormat, string> textureFormats = new Dictionary<TextureFormat, string>()
    {
        [TextureFormat.Rgba8Unorm] = "rgba8unorm",
        [TextureFormat.Bgra8Unorm] = "bgra8unorm"
    };

    private readonly FallbackMipmapGenerator fallbackGenerator;
    private readonly WebGPU api;
    private readonly Device* device;

    private ComputePipeline* pipeline;
    private TextureFormat textureFormat;
    private Texture* texture;
    private BindGroupLayout* bindGroupLayout;
    // Keep number of allocated bind groups and layouts to release when disposed.
    private BindGroup*[] allocatedBindGroups = new BindGroup*[10000];
    private int allocatedBindGroupsCount = 0;
    private BindGroupLayout*[] allocatedBindGroupLayouts = new BindGroupLayout*[1000];
    private int allocatedBindGroupLayoutCount = 0;


    /// <summary>
    /// The constructor.
    /// </summary>
    /// <param name="api">The <see cref="WebGPU"/> api.</param>
    /// <param name="device">The pointer to the <see cref="Device"/>.</param>
    public MipmapGenerator(WebGPU api, Device* device)
    {
        this.api = api;
        this.device = device;
        this.fallbackGenerator = new FallbackMipmapGenerator(api, device, this);
    }

    /// <summary>
    /// The sizes of mip textures.
    /// </summary>
    internal Extent3D[] TextureMipSizes { get; private set; }

    /// <summary>
    /// The texture views.
    /// </summary>
    internal TextureView*[] TextureViews { get; private set; }

    /// <summary>
    /// Generates the 2d texture with mipmaps.
    /// </summary>
    /// <param name="data">The pointer to the data.</param>
    /// <param name="width">The width of an image data owner.</param>
    /// <param name="height">The height of an image data owner.</param>
    /// <param name="textureFormat">The texture format.</param>
    /// <returns>The <see cref="TextureMipmapResult"/>.</returns>
    public TextureMipmapResult CreateTexture2DWithMipmaps(void* data, uint width, uint height, TextureFormat textureFormat)
    {
        if (!textureFormats.ContainsKey(textureFormat))
        {
            throw new ArgumentException($"Texture format {textureFormat} is currently not supported.");
        }

        // If we have unsporrted format used different generator.
        if (!api.DeviceHasFeature(device, FeatureName.Bgra8UnormStorage))
        {
            if (textureFormat == TextureFormat.Bgra8Unorm)
            {
                return this.fallbackGenerator.CreateTexture2DWithMipmaps(data, width, height, textureFormat);
            }
        }

        this.textureFormat = textureFormat;
        InitTexture(data, width, height);
        InitTextureViews();
        InitBindGroupLayout();
        InitPipeline();
        ComputePass();

        return new TextureMipmapResult
        {
            Texture = this.texture,
            // Mip level count is simply count of texture views in our case.
            MipLevelCount = (uint)this.TextureViews.Length,
        };
    }

    /// <summary>
    /// Initializes and crates the texture.
    /// </summary>
    /// <param name="data">The pointer to the data.</param>
    /// <param name="width">The width of a texture.</param>
    /// <param name="height">The height of a texture.</param>
    private void InitTexture(void* data, uint width, uint height)
    {
        // - TEXTURE
        Extent3D size = new Extent3D(width, height, 1);

        // Need to upload/save input data and read/write in shader.
        TextureUsage usage = TextureUsage.CopyDst
                             | TextureUsage.CopySrc
                             | TextureUsage.TextureBinding
                             | TextureUsage.StorageBinding;

        TextureDescriptor descriptor = new TextureDescriptor();
        descriptor.Dimension = TextureDimension.Dimension2D;
        descriptor.Size = size;
        descriptor.Format = this.textureFormat;
        descriptor.SampleCount = 1;
        descriptor.Usage = usage;

        // Find the number of mip levels. 
        uint maxDimension = Math.Max(width, height);
        uint maxMip = (uint)Math.Log2(maxDimension) + 1;
        descriptor.MipLevelCount = maxMip;

        // Save size.
        this.TextureMipSizes = new Extent3D[maxMip];
        this.TextureMipSizes[0] = size;

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
        descriptor.Format = this.textureFormat;
        descriptor.Dimension = TextureViewDimension.Dimension2D;
        descriptor.Aspect = TextureAspect.All;
        descriptor.BaseArrayLayer = 0;
        descriptor.ArrayLayerCount = 1;
        descriptor.BaseMipLevel = 0;
        descriptor.MipLevelCount = 1;

        this.TextureViews = new TextureView*[this.TextureMipSizes.Length];
        for (uint i = 0; i < this.TextureMipSizes.Length; i++)
        {
            descriptor.BaseMipLevel = i;
            descriptor.Label = (byte*)Marshal.StringToHGlobalAnsi($"Mip level {i}");

            TextureView* view = this.api.TextureCreateView(this.texture, in descriptor);

            if (view is null)
            {
                throw new InvalidOperationException($"Unable to create mip level {i}.");
            }

            this.TextureViews[i] = view;

            if (i > 0)
            {
                Extent3D previousSize = this.TextureMipSizes[i - 1];
                this.TextureMipSizes[i] = new Extent3D(
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
        descriptor.Label = (byte*)Marshal.StringToHGlobalAnsi("Mipmap Generator Bind Group Layout");
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
        entries[1].StorageTexture.Format = this.textureFormat;
        entries[1].StorageTexture.ViewDimension = TextureViewDimension.Dimension2D;

        descriptor.Entries = entries;

        this.bindGroupLayout = this.api.DeviceCreateBindGroupLayout(this.device, in descriptor);

        // Save so that it can be released later.
        this.allocatedBindGroupLayouts[allocatedBindGroupLayoutCount++] = this.bindGroupLayout;
    }

    /// <summary>
    /// Initialize the bind group for a given mipmap level.
    /// </summary>
    /// <param name="nextMipLevel">The mipmap level.</param>
    /// <returns>The <see cref="BindGroup"/> pointer.</returns>
    private BindGroup* InitBindGroup(uint nextMipLevel)
    {
        BindGroupDescriptor descriptor = new BindGroupDescriptor();
        descriptor.Label = (byte*)Marshal.StringToHGlobalAnsi("Mipmap Generator Bind Group");
        descriptor.Layout = this.bindGroupLayout;
        descriptor.EntryCount = 2;

        // - ENTRIES
        BindGroupEntry* entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry();
        entries[0].Binding = 0;
        entries[0].TextureView = this.TextureViews[nextMipLevel - 1];
        entries[0].Buffer = null;
        entries[0].Sampler = null;

        entries[1] = new BindGroupEntry();
        entries[1].Binding = 1;
        entries[1].TextureView = this.TextureViews[nextMipLevel];
        entries[1].Buffer = null;
        entries[1].Sampler = null;

        descriptor.Entries = entries;

        BindGroup* bindGroup = this.api.DeviceCreateBindGroup(this.device, in descriptor);

        // Save so that we can release it later.
        this.allocatedBindGroups[allocatedBindGroupsCount++] = bindGroup;

        return bindGroup;
    }

    /// <summary>
    /// Creates the compute pipeline.
    /// </summary>
    private void InitPipeline()
    {
        string shader = SHADER;
        shader = shader.Replace(TEXTURE_FORMAT_KEY, textureFormats[this.textureFormat]);

        // - SHADER MODULE
        ShaderModuleWGSLDescriptor shaderModuleWGSLDescriptor = new ShaderModuleWGSLDescriptor();
        shaderModuleWGSLDescriptor.Chain.Next = null;
        shaderModuleWGSLDescriptor.Chain.SType = SType.ShaderModuleWgsldescriptor;
        shaderModuleWGSLDescriptor.Code = (byte*)Marshal.StringToHGlobalAnsi(shader);

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

        for (uint nextMipLevel = 1; nextMipLevel < this.TextureMipSizes.Length; nextMipLevel++)
        {
            // -- BIND
            BindGroup* bindGroup = this.InitBindGroup(nextMipLevel);
            this.api.ComputePassEncoderSetBindGroup(computePass, 0, bindGroup, 0, 0);

            // -- DISPATCH
            Extent3D size = this.TextureMipSizes[nextMipLevel];
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

        this.api.ComputePipelineRelease(this.pipeline);
        this.api.CommandBufferRelease(commandBuffer);
        this.api.ComputePassEncoderRelease(computePass);
        this.api.CommandEncoderRelease(commandEncoder);
    }


    /// <inheritdoc />
    public void Dispose()
    {
        // - BIND GROUPS
        for (int i = 0; i < this.allocatedBindGroups.Length; i++)
        {
            if (allocatedBindGroups[i] != null)
            {
                this.api.BindGroupRelease(allocatedBindGroups[i]);
            }
        }
        allocatedBindGroupsCount = 0;

        // - BIND GROUP LAYOUTS
        for (int i = 0; i < this.allocatedBindGroupLayouts.Length; i++)
        {
            if (allocatedBindGroupLayouts[i] != null)
            {
                this.api.BindGroupLayoutRelease(allocatedBindGroupLayouts[i]);
            }
        }
        allocatedBindGroupLayoutCount = 0;
    }
}