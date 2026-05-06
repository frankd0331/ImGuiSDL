using System.Runtime.CompilerServices;
using System.Text;
using System.Runtime.InteropServices;
using System.Numerics;
using ImGuiNET;
using SDL;
using static SDL.SDL3;

namespace ImGuiSDL;

/// <summary>
/// Renders ImGui using SDL GPU
/// </summary>
public unsafe class ImGuiRenderer : IDisposable
{
	/// <summary>
	/// Custom ImGui User-Callback
	/// </summary>
	public delegate void UserCallback(ImDrawListPtr parentList, ImDrawCmdPtr cmd);

	/// <summary>
	/// SDL GPU Device
	/// </summary>
	public readonly SDL_GPUDevice* Device;

	/// <summary>
	/// SDL Window
	/// </summary>
	public readonly SDL_Window* Window;

	/// <summary>
	/// ImGui Context
	/// </summary>
	public readonly nint Context;

	/// <summary>
	/// Scales all of the ImGui Content by this amount
	/// </summary>
	public float Scale = 1.0f;

	private readonly SDL_GPUShader*           vertexShader;
	private readonly SDL_GPUShader*           fragmentShader;
	private readonly GpuBuffer                vertexBuffer;
	private readonly GpuBuffer                indexBuffer;
	private readonly SDL_GPUGraphicsPipeline* pipeline;
	private readonly SDL_GPUTexture*          fontTexture;
	private readonly SDL_GPUSampler*          sampler;

	private ImDrawVert[] vertices = [];
	private ushort[] indices = [];
	private readonly List<UserCallback> callbacks = [];

	public ImGuiRenderer(SDL_GPUDevice* sdlGpuDevice, SDL_Window* sdlWindow, nint imGuiContext)
	{
		var io = ImGui.GetIO();

		Device  = sdlGpuDevice;
		Window  = sdlWindow;
		Context = imGuiContext;

		// default imgui display size & scale
		{
			var display = SDL_GetDisplayForWindow(Window);
			if (display != 0)
				Scale = SDL_GetDisplayContentScale(display);
			int width, height;
			SDL_GetWindowSizeInPixels(Window, &width, &height);
			io.DisplaySize = new Vector2(width, height);
		}

		// get shader language
		var formats = SDL_GetGPUShaderFormats(Device);
		SDL_GPUShaderFormat shaderFormat;
		if      ((formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL)     != 0)
			shaderFormat = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL;
		else if ((formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV)   != 0)
			shaderFormat = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV;
		else if ((formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL)    != 0)
			shaderFormat = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL;
		else if ((formats & SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_PRIVATE) != 0)
			shaderFormat = SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_PRIVATE;
		else
			throw new NotSupportedException("No supported GPU shader format available.");

		var shaderExt = shaderFormat switch
		{
			SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_PRIVATE => "spv",
			SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_SPIRV   => "spv",
			SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_DXIL    => "dxil",
			SDL_GPUShaderFormat.SDL_GPU_SHADERFORMAT_MSL     => "msl",
			_ => throw new NotImplementedException($"Unimplemented Shader Format '{shaderFormat}'")
		};

		// create Vertex and Fragment shaders
		// Shaders are stored as Embedded files (see ImGuiSDL.csproj)
		{
			var vertexCode  = GetEmbeddedBytes($"ImGui.vertex.{shaderExt}");
			var vertexEntry = Encoding.UTF8.GetBytes("vertex_main\0");

			var fragmentCode  = GetEmbeddedBytes($"ImGui.fragment.{shaderExt}");
			var fragmentEntry = Encoding.UTF8.GetBytes("fragment_main\0");

			fixed (byte* vertexCodePtr = vertexCode)
			fixed (byte* vertexEntryPtr = vertexEntry)
			{
				var info = new SDL_GPUShaderCreateInfo
				{
					code_size          = (nuint)vertexCode.Length,
					code               = vertexCodePtr,
					entrypoint         = vertexEntryPtr,
					format             = shaderFormat,
					stage              = SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_VERTEX,
					num_samplers       = 0,
					num_storage_textures = 0,
					num_storage_buffers  = 0,
					num_uniform_buffers  = 1
				};
				vertexShader = SDL_CreateGPUShader(Device, &info);
				if (vertexShader == null)
					throw new Exception($"{nameof(SDL_CreateGPUShader)} Failed: {SDL_GetError()}");
			}

			fixed (byte* fragmentCodePtr = fragmentCode)
			fixed (byte* fragmentEntryPtr = fragmentEntry)
			{
				var info = new SDL_GPUShaderCreateInfo
				{
					code_size          = (nuint)fragmentCode.Length,
					code               = fragmentCodePtr,
					entrypoint         = fragmentEntryPtr,
					format             = shaderFormat,
					stage              = SDL_GPUShaderStage.SDL_GPU_SHADERSTAGE_FRAGMENT,
					num_samplers       = 1,
					num_storage_textures = 0,
					num_storage_buffers  = 0,
					num_uniform_buffers  = 0
				};
				fragmentShader = SDL_CreateGPUShader(Device, &info);
				if (fragmentShader == null)
					throw new Exception($"{nameof(SDL_CreateGPUShader)} Failed: {SDL_GetError()}");
			}
		}

		// create graphics pipeline
		{
			var colorTargetDesc = new SDL_GPUColorTargetDescription {
				format = SDL_GetGPUSwapchainTextureFormat(Device, Window),
				blend_state = new()
				{
					src_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_SRC_ALPHA,
					dst_color_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
					color_blend_op        = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
					src_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE,
					dst_alpha_blendfactor = SDL_GPUBlendFactor.SDL_GPU_BLENDFACTOR_ONE_MINUS_SRC_ALPHA,
					alpha_blend_op        = SDL_GPUBlendOp.SDL_GPU_BLENDOP_ADD,
					enable_blend          = true,
					enable_color_write_mask = false,
				}
			};

			var vertexBuffDesc = new SDL_GPUVertexBufferDescription {
				slot               = 0,
				pitch              = (uint)Marshal.SizeOf<ImDrawVert>(),
				input_rate         = SDL_GPUVertexInputRate.SDL_GPU_VERTEXINPUTRATE_VERTEX,
				instance_step_rate = 0
			};

			var vertexAttr = stackalloc SDL_GPUVertexAttribute[3] {
				// Position : float2
				new() {
					format   = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2,
					location = 0,
					offset   = 0
				},
				// TexCoord : float2
				new() {
					format   = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_FLOAT2,
					location = 1,
					offset   = sizeof(float) * 2
				},
				// Color : ubyte4 normalized
				new() {
					format   = SDL_GPUVertexElementFormat.SDL_GPU_VERTEXELEMENTFORMAT_UBYTE4_NORM,
					location = 2,
					offset   = sizeof(float) * 4
				}
			};

			var pipelineInfo = new SDL_GPUGraphicsPipelineCreateInfo
			{
				vertex_shader   = vertexShader,
				fragment_shader = fragmentShader,
				vertex_input_state = new()
				{
					vertex_buffer_descriptions = &vertexBuffDesc,
					num_vertex_buffers         = 1,
					vertex_attributes          = vertexAttr,
					num_vertex_attributes      = 3,
				},
				primitive_type   = SDL_GPUPrimitiveType.SDL_GPU_PRIMITIVETYPE_TRIANGLELIST,
				rasterizer_state = new() { cull_mode = SDL_GPUCullMode.SDL_GPU_CULLMODE_NONE },
				multisample_state   = new(),
				depth_stencil_state = new(),
				target_info = new()
				{
					num_color_targets         = 1,
					color_target_descriptions = &colorTargetDesc,
				}
			};
			pipeline = SDL_CreateGPUGraphicsPipeline(Device, &pipelineInfo);
			if (pipeline == null)
				throw new Exception($"{nameof(SDL_CreateGPUGraphicsPipeline)} Failed: {SDL_GetError()}");
		}

		// create buffers
		{
			vertexBuffer = new(Device, SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_VERTEX);
			indexBuffer  = new(Device, SDL_GPUBufferUsageFlags.SDL_GPU_BUFFERUSAGE_INDEX);
		}

		// create sampler
		{
			var samplerInfo = new SDL_GPUSamplerCreateInfo {
				min_filter    = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST,
				mag_filter    = SDL_GPUFilter.SDL_GPU_FILTER_NEAREST,
				mipmap_mode   = SDL_GPUSamplerMipmapMode.SDL_GPU_SAMPLERMIPMAPMODE_NEAREST,
				address_mode_u = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
				address_mode_v = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
				address_mode_w = SDL_GPUSamplerAddressMode.SDL_GPU_SAMPLERADDRESSMODE_CLAMP_TO_EDGE,
			};
			sampler = SDL_CreateGPUSampler(Device, &samplerInfo);
		}

		// setup imgui font texture
		{
			io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
			var size = width * height * 4;

			var texInfo = new SDL_GPUTextureCreateInfo
			{
				type               = SDL_GPUTextureType.SDL_GPU_TEXTURETYPE_2D,
				format             = SDL_GPUTextureFormat.SDL_GPU_TEXTUREFORMAT_R8G8B8A8_UNORM,
				usage              = SDL_GPUTextureUsageFlags.SDL_GPU_TEXTUREUSAGE_SAMPLER,
				width              = (uint)width,
				height             = (uint)height,
				layer_count_or_depth = 1,
				num_levels         = 1,
				sample_count       = SDL_GPUSampleCount.SDL_GPU_SAMPLECOUNT_1
			};
			fontTexture = SDL_CreateGPUTexture(Device, &texInfo);
			if (fontTexture == null)
				throw new Exception($"{nameof(SDL_CreateGPUTexture)} Failed: {SDL_GetError()}");

			var transferInfo = new SDL_GPUTransferBufferCreateInfo {
				usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
				size  = (uint)size
			};
			var transferBuffer = SDL_CreateGPUTransferBuffer(Device, &transferInfo);

			var transferPtr = (void*)SDL_MapGPUTransferBuffer(Device, transferBuffer, false);
			Buffer.MemoryCopy(pixels, transferPtr, size, size);
			SDL_UnmapGPUTransferBuffer(Device, transferBuffer);

			var cmd  = SDL_AcquireGPUCommandBuffer(Device);
			var pass = SDL_BeginGPUCopyPass(cmd);

			var src = new SDL_GPUTextureTransferInfo { transfer_buffer = transferBuffer, offset = 0 };
			var dst = new SDL_GPUTextureRegion { texture = fontTexture, w = (uint)width, h = (uint)height, d = 1 };
			SDL_UploadToGPUTexture(pass, &src, &dst, false);

			SDL_EndGPUCopyPass(pass);
			SDL_SubmitGPUCommandBuffer(cmd);
			SDL_ReleaseGPUTransferBuffer(Device, transferBuffer);
		}

		io.Fonts.SetTexID((nint)fontTexture);
	}

	~ImGuiRenderer()
		=> Dispose();

	/// <summary>
	/// Destroys the GPU resources used by the Renderer
	/// </summary>
	public void Dispose()
	{
		GC.SuppressFinalize(this);

		SDL_ReleaseGPUShader(Device, vertexShader);
		SDL_ReleaseGPUShader(Device, fragmentShader);
		SDL_ReleaseGPUGraphicsPipeline(Device, pipeline);
		SDL_ReleaseGPUTexture(Device, fontTexture);
		SDL_ReleaseGPUSampler(Device, sampler);

		vertexBuffer.Dispose();
		indexBuffer.Dispose();
	}

	/// <summary>
	/// Begins a new Frame (clears / increments internal data).
	/// Call this alongside <see cref="ImGui.NewFrame"/>.
	/// </summary>
	public void NewFrame()
	{
		callbacks.Clear();
	}

	/// <summary>
	/// Adds a UserCallback method to the current Window Draw List.
	/// This wraps <see cref="ImDrawListPtr.AddCallback(nint, nint)"/> as it
	/// doesn't work gracefully with C# by default.
	/// </summary>
	public void AddUserCallback(UserCallback callback, nint? userData = null)
	{
		callbacks.Add(callback);
		ImGui.GetWindowDrawList().AddCallback(callbacks.Count, userData ?? nint.Zero);
	}

	/// <summary>
	/// Renders ImGui contents, acquiring the swapchain texture internally.
	/// </summary>
	public void Render(SDL_FColor? clearColor = null)
	{
		var cmd = SDL_AcquireGPUCommandBuffer(Device);

		SDL_GPUTexture* swapchain;
		uint w, h;
		if (SDL_WaitAndAcquireGPUSwapchainTexture(cmd, Window, &swapchain, &w, &h))
		{
			Render(cmd, swapchain, (int)w, (int)h, clearColor);
		}
		else
		{
			Console.WriteLine($"{nameof(SDL_WaitAndAcquireGPUSwapchainTexture)} failed: {SDL_GetError()}");
		}

		SDL_SubmitGPUCommandBuffer(cmd);
	}

	/// <summary>
	/// Renders ImGui contents to an explicit command buffer and target texture.
	/// </summary>
	public void Render(SDL_GPUCommandBuffer* cmd, SDL_GPUTexture* swapchainTexture, int swapchainWidth, int swapchainHeight, SDL_FColor? clearColor = null)
	{
		var io = ImGui.GetIO();
		io.DisplaySize             = new Vector2(swapchainWidth, swapchainHeight) / Scale;
		io.DisplayFramebufferScale = Vector2.One * Scale;

		ImGui.Render();

		var data = ImGui.GetDrawData();
		if (data.NativePtr == null || data.TotalVtxCount <= 0)
			return;

		// build vertex/index buffers
		{
			var vertexCount = 0;
			var indexCount  = 0;
			for (int i = 0; i < data.CmdListsCount; i++)
			{
				vertexCount += data.CmdLists[i].VtxBuffer.Size;
				indexCount  += data.CmdLists[i].IdxBuffer.Size;
			}

			if (vertexCount > vertices.Length) Array.Resize(ref vertices, vertexCount);
			if (indexCount  > indices.Length)  Array.Resize(ref indices,  indexCount);

			vertexCount = indexCount = 0;
			for (int i = 0; i < data.CmdListsCount; i++)
			{
				var list      = data.CmdLists[i];
				var vertexSrc = new Span<ImDrawVert>((void*)list.VtxBuffer.Data, list.VtxBuffer.Size);
				var indexSrc  = new Span<ushort>((void*)list.IdxBuffer.Data, list.IdxBuffer.Size);

				vertexSrc.CopyTo(vertices.AsSpan()[vertexCount..]);
				indexSrc.CopyTo(indices.AsSpan()[indexCount..]);

				vertexCount += vertexSrc.Length;
				indexCount  += indexSrc.Length;
			}

			var copy = SDL_BeginGPUCopyPass(cmd);
			vertexBuffer.Upload<ImDrawVert>(copy, vertices.AsSpan(0, vertexCount));
			indexBuffer.Upload<ushort>(copy, indices.AsSpan(0, indexCount));
			SDL_EndGPUCopyPass(copy);
		}

		// render pass
		{
			var colorTargetInfo = new SDL_GPUColorTargetInfo
			{
				texture    = swapchainTexture,
				clear_color = clearColor ?? default,
				load_op    = clearColor.HasValue ? SDL_GPULoadOp.SDL_GPU_LOADOP_CLEAR : SDL_GPULoadOp.SDL_GPU_LOADOP_LOAD,
				store_op   = SDL_GPUStoreOp.SDL_GPU_STOREOP_STORE,
			};

			var pass = SDL_BeginGPURenderPass(cmd, &colorTargetInfo, 1, null);

			SDL_BindGPUGraphicsPipeline(pass, pipeline);

			var initialBinding = new SDL_GPUTextureSamplerBinding { sampler = sampler, texture = fontTexture };
			SDL_BindGPUFragmentSamplers(pass, 0, &initialBinding, 1);

			var ibBinding = new SDL_GPUBufferBinding { buffer = indexBuffer.Buffer };
			SDL_BindGPUIndexBuffer(pass, &ibBinding, SDL_GPUIndexElementSize.SDL_GPU_INDEXELEMENTSIZE_16BIT);

			var vbBinding = new SDL_GPUBufferBinding { buffer = vertexBuffer.Buffer };
			SDL_BindGPUVertexBuffers(pass, 0, &vbBinding, 1);

			{
				Matrix4x4 mat =
					Matrix4x4.CreateScale(data.FramebufferScale.X, data.FramebufferScale.Y, 1.0f) *
					Matrix4x4.CreateOrthographicOffCenter(0, swapchainWidth, swapchainHeight, 0, 0, 100.0f);
				SDL_PushGPUVertexUniformData(cmd, 0, (IntPtr)(&mat), (uint)Marshal.SizeOf<Matrix4x4>());
			}

			var currentTexture  = (nint)fontTexture;
			var globalVtxOffset = 0;
			var globalIdxOffset = 0;

			for (int i = 0; i < data.CmdListsCount; i++)
			{
				var imList     = data.CmdLists[i];
				var imCommands = (ImDrawCmd*)imList.CmdBuffer.Data;

				for (var imCmd = imCommands; imCmd < imCommands + imList.CmdBuffer.Size; imCmd++)
				{
					if (imCmd->UserCallback != nint.Zero)
					{
						var index = (int)imCmd->UserCallback - 1;
						if (index >= 0 && index < callbacks.Count)
							callbacks[index].Invoke(imList, imCmd);
						continue;
					}

					if (imCmd->TextureId != currentTexture)
					{
						currentTexture = imCmd->TextureId;
						var dynBinding = new SDL_GPUTextureSamplerBinding { sampler = sampler, texture = (SDL_GPUTexture*)currentTexture };
						SDL_BindGPUFragmentSamplers(pass, 0, &dynBinding, 1);
					}

					var scissor = new SDL_Rect
					{
						x = (int)(imCmd->ClipRect.X * data.FramebufferScale.X),
						y = (int)(imCmd->ClipRect.Y * data.FramebufferScale.Y),
						w = (int)((imCmd->ClipRect.Z - imCmd->ClipRect.X) * data.FramebufferScale.X),
						h = (int)((imCmd->ClipRect.W - imCmd->ClipRect.Y) * data.FramebufferScale.Y),
					};
					SDL_SetGPUScissor(pass, &scissor);

					SDL_DrawGPUIndexedPrimitives(
						render_pass:    pass,
						num_indices:    imCmd->ElemCount,
						num_instances:  1,
						first_index:    (uint)(imCmd->IdxOffset + globalIdxOffset),
						vertex_offset:  (int)(imCmd->VtxOffset + globalVtxOffset),
						first_instance: 0
					);
				}

				globalVtxOffset += imList.VtxBuffer.Size;
				globalIdxOffset += imList.IdxBuffer.Size;
			}

			SDL_EndGPURenderPass(pass);
		}
	}

	/// <summary>
	/// Wrapper around an SDL GPU Buffer shared between vertex and index buffer management.
	/// </summary>
	private unsafe class GpuBuffer(SDL_GPUDevice* device, SDL_GPUBufferUsageFlags usage) : IDisposable
	{
		public readonly SDL_GPUDevice*          Device   = device;
		public readonly SDL_GPUBufferUsageFlags Usage    = usage;
		public SDL_GPUBuffer*                   Buffer   { get; private set; }
		public int                              Capacity { get; private set; }

		~GpuBuffer() => Dispose();

		public void Dispose()
		{
			GC.SuppressFinalize(this);

			if (Buffer != null)
			{
				SDL_ReleaseGPUBuffer(Device, Buffer);
				Buffer = null;
			}
		}

		public void Upload<T>(SDL_GPUCopyPass* copyPass, in ReadOnlySpan<T> data) where T : unmanaged
		{
			var dataSize = Marshal.SizeOf<T>() * data.Length;

			if (Buffer == null || dataSize > Capacity)
			{
				if (Buffer != null)
				{
					SDL_ReleaseGPUBuffer(Device, Buffer);
					Buffer = null;
				}

				Capacity = Math.Max(Capacity, 8);
				while (Capacity < dataSize)
					Capacity *= 2;

				var bufInfo = new SDL_GPUBufferCreateInfo { usage = Usage, size = (uint)Capacity };
				Buffer = SDL_CreateGPUBuffer(Device, &bufInfo);

				if (Buffer == null)
					throw new Exception($"{nameof(SDL_CreateGPUBuffer)} Failed: {SDL_GetError()}");
			}

			// TODO: cache this! reuse transfer buffer!
			var transferInfo = new SDL_GPUTransferBufferCreateInfo
			{
				usage = SDL_GPUTransferBufferUsage.SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD,
				size  = (uint)dataSize
			};
			var transferBuffer = SDL_CreateGPUTransferBuffer(Device, &transferInfo);

			fixed (T* src = data)
			{
				byte* dst = (byte*)(void*)SDL_MapGPUTransferBuffer(Device, transferBuffer, false);
				System.Buffer.MemoryCopy(src, dst, dataSize, dataSize);
				SDL_UnmapGPUTransferBuffer(Device, transferBuffer);
			}

			var transferSrc = new SDL_GPUTransferBufferLocation { transfer_buffer = transferBuffer, offset = 0 };
			var bufDst      = new SDL_GPUBufferRegion           { buffer = Buffer, offset = 0, size = (uint)dataSize };
			SDL_UploadToGPUBuffer(copyPass, &transferSrc, &bufDst, false);

			SDL_ReleaseGPUTransferBuffer(Device, transferBuffer);
		}
	}

	/// <summary>
	/// Shaders are stored as Embedded files (see ImGuiSDL.csproj)
	/// </summary>
	private static byte[] GetEmbeddedBytes(string file)
	{
		var assembly = typeof(ImGuiRenderer).Assembly;
		using var stream = assembly.GetManifestResourceStream(file);
		if (stream != null)
		{
			var result = new byte[stream.Length];
			stream.ReadExactly(result);
			return result;
		}

		throw new Exception($"Failed to load Embedded file '{file}'");
	}
}
