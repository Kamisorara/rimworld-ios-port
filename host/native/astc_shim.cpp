// libportastc.so: ASTC 4x4 encoding for the GPUs that cannot sample BC textures (ARM's Mali): hooks/Textures.cs
// transcodes the game's BC textures to ASTC instead of decoding them to RGBA (four times the memory).
// ARM's astcenc 5.7.0 (downloads/src/astc-encoder-5.7.0, Apache-2.0), built for NEON only: every 64-bit ARM core has
// it, so the oldest Dimensity runs this the same; SVE and the like are left out.
//
// port_astc_encode encodes one image (one mip level) on the calling thread. A context is made once per thread and per
// colour profile (the tables astcenc builds for a block size take a few milliseconds) and kept.
// port_astc_encode_parallel encodes one image on several threads at once, astcenc's own way: one context with a
// working area per thread, every thread taking blocks from the same image (an atlas page, while the game waits).
#include <stdint.h>
#include <stddef.h>
#include <mutex>
#include <thread>
#include <vector>
#include "astcenc.h"

namespace
{
	struct Shared
	{
		astcenc_context* context = nullptr;
		bool srgb = false;
		float quality = -1.0f;
		int threads = 0;
	};

	std::mutex sharedLock;
	Shared shared;

	struct Slot
	{
		astcenc_context* context = nullptr;
		float quality = -1.0f;
	};

	thread_local Slot slots[2]; // linear, sRGB

	astcenc_context* ContextFor(bool srgb, float quality, astcenc_error* error)
	{
		Slot& slot = slots[srgb ? 1 : 0];
		if (slot.context != nullptr && slot.quality == quality)
		{
			*error = ASTCENC_SUCCESS;
			return slot.context;
		}
		if (slot.context != nullptr)
		{
			astcenc_context_free(slot.context);
			slot.context = nullptr;
		}
		astcenc_config config;
		*error = astcenc_config_init(srgb ? ASTCENC_PRF_LDR_SRGB : ASTCENC_PRF_LDR, 4, 4, 1, quality, 0, &config);
		if (*error != ASTCENC_SUCCESS)
		{
			return nullptr;
		}
		*error = astcenc_context_alloc(&config, 1, &slot.context, nullptr);
		if (*error != ASTCENC_SUCCESS)
		{
			slot.context = nullptr;
			return nullptr;
		}
		slot.quality = quality;
		return slot.context;
	}
}

extern "C"
{
	// RGBA8 texels (rows tightly packed) to ASTC 4x4 blocks, 16 bytes each, row after row of blocks.
	// out must hold ((width + 3) / 4) * ((height + 3) / 4) * 16 bytes. Returns 0, or astcenc's error code.
	__attribute__((visibility("default"))) int port_astc_encode(const uint8_t* rgba, int width, int height, int srgb, float quality, uint8_t* out, size_t outLength)
	{
		if (rgba == nullptr || out == nullptr || width <= 0 || height <= 0)
		{
			return -1;
		}
		size_t needed = (size_t)((width + 3) / 4) * (size_t)((height + 3) / 4) * 16;
		if (outLength < needed)
		{
			return -2;
		}
		astcenc_error error;
		astcenc_context* context = ContextFor(srgb != 0, quality, &error);
		if (context == nullptr)
		{
			return (int)error;
		}
		void* slice = (void*)rgba;
		astcenc_image image;
		image.dim_x = (unsigned int)width;
		image.dim_y = (unsigned int)height;
		image.dim_z = 1;
		image.data_type = ASTCENC_TYPE_U8;
		image.data = &slice;
		const astcenc_swizzle swizzle = { ASTCENC_SWZ_R, ASTCENC_SWZ_G, ASTCENC_SWZ_B, ASTCENC_SWZ_A };
		error = astcenc_compress_image(context, &image, &swizzle, out, outLength, 0);
		astcenc_compress_reset(context);
		return (int)error;
	}

	// As port_astc_encode, on `threads` threads (the calling one and threads - 1 more). One call at a time.
	__attribute__((visibility("default"))) int port_astc_encode_parallel(const uint8_t* rgba, int width, int height, int srgb, float quality, uint8_t* out, size_t outLength, int threads)
	{
		if (rgba == nullptr || out == nullptr || width <= 0 || height <= 0)
		{
			return -1;
		}
		size_t needed = (size_t)((width + 3) / 4) * (size_t)((height + 3) / 4) * 16;
		if (outLength < needed)
		{
			return -2;
		}
		threads = threads < 1 ? 1 : threads > 32 ? 32 : threads;
		std::lock_guard<std::mutex> hold(sharedLock);
		if (shared.context == nullptr || shared.srgb != (srgb != 0) || shared.quality != quality || shared.threads != threads)
		{
			if (shared.context != nullptr)
			{
				astcenc_context_free(shared.context);
				shared.context = nullptr;
			}
			astcenc_config config;
			astcenc_error error = astcenc_config_init(srgb ? ASTCENC_PRF_LDR_SRGB : ASTCENC_PRF_LDR, 4, 4, 1, quality, 0, &config);
			if (error == ASTCENC_SUCCESS)
			{
				error = astcenc_context_alloc(&config, (unsigned int)threads, &shared.context, nullptr);
			}
			if (error != ASTCENC_SUCCESS)
			{
				shared.context = nullptr;
				return (int)error;
			}
			shared.srgb = srgb != 0;
			shared.quality = quality;
			shared.threads = threads;
		}
		void* slice = (void*)rgba;
		astcenc_image image;
		image.dim_x = (unsigned int)width;
		image.dim_y = (unsigned int)height;
		image.dim_z = 1;
		image.data_type = ASTCENC_TYPE_U8;
		image.data = &slice;
		const astcenc_swizzle swizzle = { ASTCENC_SWZ_R, ASTCENC_SWZ_G, ASTCENC_SWZ_B, ASTCENC_SWZ_A };
		std::vector<astcenc_error> errors((size_t)threads, ASTCENC_SUCCESS);
		std::vector<std::thread> helpers;
		for (int i = 1; i < threads; i++)
		{
			helpers.emplace_back([&, i]() { errors[(size_t)i] = astcenc_compress_image(shared.context, &image, &swizzle, out, outLength, (unsigned int)i); });
		}
		errors[0] = astcenc_compress_image(shared.context, &image, &swizzle, out, outLength, 0);
		for (std::thread& helper : helpers)
		{
			helper.join();
		}
		astcenc_compress_reset(shared.context);
		for (astcenc_error error : errors)
		{
			if (error != ASTCENC_SUCCESS)
			{
				return (int)error;
			}
		}
		return 0;
	}

	// ASTC 4x4 blocks back to RGBA8 (for checking an encoding; the GPU decodes them itself).
	__attribute__((visibility("default"))) int port_astc_decode(const uint8_t* blocks, size_t blocksLength, int width, int height, int srgb, uint8_t* rgba)
	{
		astcenc_error error;
		astcenc_context* context = ContextFor(srgb != 0, 0.0f, &error);
		if (context == nullptr)
		{
			return (int)error;
		}
		void* slice = (void*)rgba;
		astcenc_image image;
		image.dim_x = (unsigned int)width;
		image.dim_y = (unsigned int)height;
		image.dim_z = 1;
		image.data_type = ASTCENC_TYPE_U8;
		image.data = &slice;
		const astcenc_swizzle swizzle = { ASTCENC_SWZ_R, ASTCENC_SWZ_G, ASTCENC_SWZ_B, ASTCENC_SWZ_A };
		error = astcenc_decompress_image(context, blocks, blocksLength, &image, &swizzle, 0);
		astcenc_decompress_reset(context);
		return (int)error;
	}

	// The calling thread's contexts let go (a worker that is done), and the shared one of port_astc_encode_parallel.
	__attribute__((visibility("default"))) void port_astc_release(void)
	{
		for (Slot& slot : slots)
		{
			if (slot.context != nullptr)
			{
				astcenc_context_free(slot.context);
				slot.context = nullptr;
				slot.quality = -1.0f;
			}
		}
		std::lock_guard<std::mutex> hold(sharedLock);
		if (shared.context != nullptr)
		{
			astcenc_context_free(shared.context);
			shared.context = nullptr;
			shared.threads = 0;
		}
	}
}
