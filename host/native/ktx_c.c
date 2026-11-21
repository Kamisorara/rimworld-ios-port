// The flat entry points the engine's Brutal.Ktx binds ("<function>1"), as the PC's Ktx.dll exports them over libktx:
// each forwards to the libktx function or vtable macro of that name. This is the read-only libktx (native/build.sh ktx),
// so what writes or encodes (Write*, Compress*, Deflate*, DecodeAstc) answers KTX_UNSUPPORTED_FEATURE; the engine's
// texture loader (Brutal.TextureApi.Ktx.Loader) reads, transcodes and uploads only.
#include <stdbool.h>
#include <stdlib.h>
#include "ktx.h"
#include "gl_format.h"
#include "vk_format.h"

// In libktx these come with its Vulkan loader (vkloader.c) or its writer (texture2.c under KTX_FEATURE_WRITE), both
// left out of the read-only build: the same code here.
bool isSrgbFormat(VkFormat format);
bool isNotSrgbFormatButHasSrgbVariant(VkFormat format);

static VkFormat Ktx1VkFormat(ktxTexture1* This)
{
    VkFormat vkFormat = vkGetFormatFromOpenGLInternalFormat(This->glInternalformat);
    if (vkFormat == VK_FORMAT_UNDEFINED)
    {
        vkFormat = vkGetFormatFromOpenGLFormat(This->glFormat, This->glType);
    }
    return vkFormat;
}

static KTX_error_code Ktx2SetTransfer(ktxTexture2* This, khr_df_transfer_e tf)
{
    if (isSrgbFormat((VkFormat)This->vkFormat) && tf != KHR_DF_TRANSFER_SRGB)
        return KTX_INVALID_OPERATION;
    if (isNotSrgbFormatButHasSrgbVariant((VkFormat)This->vkFormat) && tf == KHR_DF_TRANSFER_SRGB)
        return KTX_INVALID_OPERATION;
    KHR_DFDSETVAL(This->pDfd + 1, TRANSFER, tf);
    return KTX_SUCCESS;
}

#define API __attribute__((visibility("default")))

API const char* ktxErrorString1(KTX_error_code error) { return ktxErrorString(error); }
API const char* ktxSupercompressionSchemeString1(ktxSupercmpScheme scheme) { return ktxSupercompressionSchemeString(scheme); }
API const char* ktxTranscodeFormatString1(ktx_transcode_fmt_e format) { return ktxTranscodeFormatString(format); }
API void ktx_FreeWriteToMemoryBuffer1(void* ptr) { free(ptr); }

// ktxTexture
API KTX_error_code ktxTexture_CreateFromMemory1(const ktx_uint8_t* bytes, ktx_size_t size, ktxTextureCreateFlags flags, ktxTexture** newTex) { return ktxTexture_CreateFromMemory(bytes, size, flags, newTex); }
API KTX_error_code ktxTexture_CreateFromNamedFile1(const char* filename, ktxTextureCreateFlags flags, ktxTexture** newTex) { return ktxTexture_CreateFromNamedFile(filename, flags, newTex); }
API void ktxTexture_Destroy1(ktxTexture* This) { ktxTexture_Destroy(This); }
API ktx_uint8_t* ktxTexture_GetData1(ktxTexture* This) { return ktxTexture_GetData(This); }
API ktx_size_t ktxTexture_GetDataSize1(ktxTexture* This) { return ktxTexture_GetDataSize(This); }
API ktx_size_t ktxTexture_GetDataSizeUncompressed1(ktxTexture* This) { return ktxTexture_GetDataSizeUncompressed(This); }
API ktx_uint32_t ktxTexture_GetElementSize1(ktxTexture* This) { return ktxTexture_GetElementSize(This); }
API KTX_error_code ktxTexture_GetImageOffset1(ktxTexture* This, ktx_uint32_t level, ktx_uint32_t layer, ktx_uint32_t faceSlice, ktx_size_t* pOffset) { return ktxTexture_GetImageOffset(This, level, layer, faceSlice, pOffset); }
API ktx_size_t ktxTexture_GetImageSize1(ktxTexture* This, ktx_uint32_t level) { return ktxTexture_GetImageSize(This, level); }
API ktx_size_t ktxTexture_GetLevelSize1(ktxTexture* This, ktx_uint32_t level) { return ktxTexture_GetLevelSize(This, level); }
API ktx_uint32_t ktxTexture_GetRowPitch1(ktxTexture* This, ktx_uint32_t level) { return ktxTexture_GetRowPitch(This, level); }
API ktx_uint32_t ktxTexture_GetVkFormat1(ktxTexture* This) { return This->classId == ktxTexture2_c ? ((ktxTexture2*)This)->vkFormat : (ktx_uint32_t)Ktx1VkFormat((ktxTexture1*)This); }
API KTX_error_code ktxTexture_IterateLevelFaces1(ktxTexture* This, PFNKTXITERCB iterCb, void* userdata) { return ktxTexture_IterateLevelFaces(This, iterCb, userdata); }
API KTX_error_code ktxTexture_IterateLevels1(ktxTexture* This, PFNKTXITERCB iterCb, void* userdata) { return ktxTexture_IterateLevels(This, iterCb, userdata); }
API KTX_error_code ktxTexture_IterateLoadLevelFaces1(ktxTexture* This, PFNKTXITERCB iterCb, void* userdata) { return ktxTexture_IterateLoadLevelFaces(This, iterCb, userdata); }
API KTX_error_code ktxTexture_LoadImageData1(ktxTexture* This, ktx_uint8_t* pBuffer, ktx_size_t bufSize) { return ktxTexture_LoadImageData(This, pBuffer, bufSize); }
API ktx_bool_t ktxTexture_NeedsTranscoding1(ktxTexture* This) { return ktxTexture_NeedsTranscoding(This); }
API KTX_error_code ktxTexture_SetImageFromMemory1(ktxTexture* This, ktx_uint32_t level, ktx_uint32_t layer, ktx_uint32_t faceSlice, const ktx_uint8_t* src, ktx_size_t srcSize) { return ktxTexture_SetImageFromMemory(This, level, layer, faceSlice, src, srcSize); }
API KTX_error_code ktxTexture_WriteToMemory1(ktxTexture* This, ktx_uint8_t** bytes, ktx_size_t* size) { (void)This; (void)bytes; (void)size; return KTX_UNSUPPORTED_FEATURE; }
API KTX_error_code ktxTexture_WriteToNamedFile1(ktxTexture* This, const char* dstname) { (void)This; (void)dstname; return KTX_UNSUPPORTED_FEATURE; }

// ktxTexture1
API KTX_error_code ktxTexture1_Create1(const ktxTextureCreateInfo* createInfo, ktxTextureCreateStorageEnum storage, ktxTexture1** newTex) { return ktxTexture1_Create(createInfo, storage, newTex); }
API KTX_error_code ktxTexture1_CreateFromMemory1(const ktx_uint8_t* bytes, ktx_size_t size, ktxTextureCreateFlags flags, ktxTexture1** newTex) { return ktxTexture1_CreateFromMemory(bytes, size, flags, newTex); }
API KTX_error_code ktxTexture1_CreateFromNamedFile1(const char* filename, ktxTextureCreateFlags flags, ktxTexture1** newTex) { return ktxTexture1_CreateFromNamedFile(filename, flags, newTex); }
API ktx_uint32_t ktxTexture1_GetVkFormat1(ktxTexture1* This) { return (ktx_uint32_t)Ktx1VkFormat(This); }
API KTX_error_code ktxTexture1_WriteKTX2ToMemory1(ktxTexture1* This, ktx_uint8_t** bytes, ktx_size_t* size) { (void)This; (void)bytes; (void)size; return KTX_UNSUPPORTED_FEATURE; }
API KTX_error_code ktxTexture1_WriteKTX2ToNamedFile1(ktxTexture1* This, const char* dstname) { (void)This; (void)dstname; return KTX_UNSUPPORTED_FEATURE; }

// ktxTexture2
API KTX_error_code ktxTexture2_Create1(const ktxTextureCreateInfo* createInfo, ktxTextureCreateStorageEnum storage, ktxTexture2** newTex) { return ktxTexture2_Create(createInfo, storage, newTex); }
API KTX_error_code ktxTexture2_CreateCopy1(ktxTexture2* orig, ktxTexture2** newTex) { return ktxTexture2_CreateCopy(orig, newTex); }
API KTX_error_code ktxTexture2_CreateFromMemory1(const ktx_uint8_t* bytes, ktx_size_t size, ktxTextureCreateFlags flags, ktxTexture2** newTex) { return ktxTexture2_CreateFromMemory(bytes, size, flags, newTex); }
API KTX_error_code ktxTexture2_CreateFromNamedFile1(const char* filename, ktxTextureCreateFlags flags, ktxTexture2** newTex) { return ktxTexture2_CreateFromNamedFile(filename, flags, newTex); }
API khr_df_model_e ktxTexture2_GetColorModel_e1(ktxTexture2* This) { return ktxTexture2_GetColorModel_e(This); }
API void ktxTexture2_GetComponentInfo1(ktxTexture2* This, ktx_uint32_t* numComponents, ktx_uint32_t* componentByteLength) { ktxTexture2_GetComponentInfo(This, numComponents, componentByteLength); }
API ktx_uint32_t ktxTexture2_GetNumComponents1(ktxTexture2* This) { return ktxTexture2_GetNumComponents(This); }
API ktx_uint32_t ktxTexture2_GetOETF1(ktxTexture2* This) { return ktxTexture2_GetOETF(This); }
API khr_df_transfer_e ktxTexture2_GetOETF_e1(ktxTexture2* This) { return ktxTexture2_GetOETF_e(This); }
API ktx_bool_t ktxTexture2_GetPremultipliedAlpha1(ktxTexture2* This) { return ktxTexture2_GetPremultipliedAlpha(This); }
API khr_df_primaries_e ktxTexture2_GetPrimaries_e1(ktxTexture2* This) { return ktxTexture2_GetPrimaries_e(This); }
API khr_df_transfer_e ktxTexture2_GetTransferFunction_e1(ktxTexture2* This) { return ktxTexture2_GetTransferFunction_e(This); }
API ktx_uint32_t ktxTexture2_GetVkFormat1(ktxTexture2* This) { return This->vkFormat; }
API KTX_error_code ktxTexture2_LoadDeflatedImageData1(ktxTexture2* This, ktx_uint8_t* pBuffer, ktx_size_t bufSize) { return ktxTexture2_LoadDeflatedImageData(This, pBuffer, bufSize); }
API KTX_error_code ktxTexture2_SetOETF1(ktxTexture2* This, khr_df_transfer_e oetf) { return Ktx2SetTransfer(This, oetf); }
API KTX_error_code ktxTexture2_SetPrimaries1(ktxTexture2* This, khr_df_primaries_e primaries) { KHR_DFDSETVAL(This->pDfd + 1, PRIMARIES, primaries); return KTX_SUCCESS; }
API KTX_error_code ktxTexture2_SetTransferFunction1(ktxTexture2* This, khr_df_transfer_e tf) { return Ktx2SetTransfer(This, tf); }
API KTX_error_code ktxTexture2_TranscodeBasis1(ktxTexture2* This, ktx_transcode_fmt_e fmt, ktx_transcode_flags flags) { return ktxTexture2_TranscodeBasis(This, fmt, flags); }
API KTX_error_code ktxTexture2_CompressAstc1(ktxTexture2* This, ktx_uint32_t quality) { (void)This; (void)quality; return KTX_UNSUPPORTED_FEATURE; }
API KTX_error_code ktxTexture2_CompressAstcEx1(ktxTexture2* This, void* params) { (void)This; (void)params; return KTX_UNSUPPORTED_FEATURE; }
API KTX_error_code ktxTexture2_CompressBasis1(ktxTexture2* This, ktx_uint32_t quality) { (void)This; (void)quality; return KTX_UNSUPPORTED_FEATURE; }
API KTX_error_code ktxTexture2_CompressBasisEx1(ktxTexture2* This, void* params) { (void)This; (void)params; return KTX_UNSUPPORTED_FEATURE; }
API KTX_error_code ktxTexture2_DecodeAstc1(ktxTexture2* This) { (void)This; return KTX_UNSUPPORTED_FEATURE; }
API KTX_error_code ktxTexture2_DeflateZLIB1(ktxTexture2* This, ktx_uint32_t level) { (void)This; (void)level; return KTX_UNSUPPORTED_FEATURE; }
API KTX_error_code ktxTexture2_DeflateZstd1(ktxTexture2* This, ktx_uint32_t level) { (void)This; (void)level; return KTX_UNSUPPORTED_FEATURE; }
