// libgli.so: the small C API over the gli texture library that the engine's Brutal.Gli binds (gli.dll on Windows):
// DDS/KTX/KMG loading, texture creation and the accessors. A texture handle is a heap-allocated gli::texture.
#include <cstdint>
#include <cstring>
#include <new>
#include <gli/gli.hpp>

#define API extern "C" __attribute__((visibility("default")))

struct int3 { int x, y, z; };
struct swizzles4 { int r, g, b, a; };

static gli::swizzles to_swizzles(swizzles4 s)
{
    return gli::swizzles(gli::swizzle(s.r), gli::swizzle(s.g), gli::swizzle(s.b), gli::swizzle(s.a));
}

static int3 to_int3(gli::texture::extent_type e)
{
    int3 r = { e.x, e.y, e.z };
    return r;
}

static gli::texture* tex(void* handle) { return static_cast<gli::texture*>(handle); }

API void* createEmpty()
{
    return new (std::nothrow) gli::texture();
}

API void* create(int target, int format, int3 extent, int64_t layers, int64_t faces, int64_t levels, swizzles4 swizzles)
{
    gli::texture::extent_type e(extent.x, extent.y, extent.z);
    return new (std::nothrow) gli::texture(gli::target(target), gli::format(format), e, (size_t)layers, (size_t)faces, (size_t)levels, to_swizzles(swizzles));
}

API void* createFromExistingSubset(void* texture, int target, int format, int64_t baseLayer, int64_t maxLayer, int64_t baseFace, int64_t maxFace, int64_t baseLevel, int64_t maxLevel, swizzles4 swizzles)
{
    if (!texture) return nullptr;
    return new (std::nothrow) gli::texture(*tex(texture), gli::target(target), gli::format(format), (size_t)baseLayer, (size_t)maxLayer, (size_t)baseFace, (size_t)maxFace, (size_t)baseLevel, (size_t)maxLevel, to_swizzles(swizzles));
}

API void* createFromExisting(void* texture, int target, int format, swizzles4 swizzles)
{
    if (!texture) return nullptr;
    return new (std::nothrow) gli::texture(*tex(texture), gli::target(target), gli::format(format), to_swizzles(swizzles));
}

API bool save(void* texture, const char* filePath)
{
    if (!texture || !filePath) return false;
    return gli::save(*tex(texture), filePath);
}

API void release(void* texture)
{
    delete tex(texture);
}

API void* load(const char* fileName)
{
    if (!fileName) return nullptr;
    return new (std::nothrow) gli::texture(gli::load(fileName));
}

API void* loadFromMemory(const char* data, int64_t size)
{
    if (!data || size <= 0) return nullptr;
    return new (std::nothrow) gli::texture(gli::load(data, (size_t)size));
}

API unsigned char* data(void* texture)
{
    if (!texture || tex(texture)->empty()) return nullptr;
    return static_cast<unsigned char*>(tex(texture)->data());
}

API unsigned char* imagedata(void* texture, int64_t level, int64_t layer, int64_t face)
{
    if (!texture || tex(texture)->empty()) return nullptr;
    return static_cast<unsigned char*>(tex(texture)->data((size_t)layer, (size_t)face, (size_t)level));
}

API int64_t size(void* texture)
{
    if (!texture) return 0;
    return (int64_t)tex(texture)->size();
}

API int64_t levelsize(void* texture, int64_t level)
{
    if (!texture || tex(texture)->empty()) return 0;
    return (int64_t)tex(texture)->size((size_t)level);
}

API int3 extent(void* texture, int64_t level)
{
    int3 zero = { 0, 0, 0 };
    if (!texture || tex(texture)->empty()) return zero;
    return to_int3(tex(texture)->extent((size_t)level));
}

API int format(void* texture)
{
    if (!texture) return 0;
    return (int)tex(texture)->format();
}

API int64_t layers(void* texture) { return texture ? (int64_t)tex(texture)->layers() : 0; }
API int64_t levels(void* texture) { return texture ? (int64_t)tex(texture)->levels() : 0; }
API int64_t faces(void* texture) { return texture ? (int64_t)tex(texture)->faces() : 0; }
API int target(void* texture) { return texture ? (int)tex(texture)->target() : 0; }

API int64_t rowpitch(void* texture, int64_t level)
{
    if (!texture || tex(texture)->empty()) return 0;
    gli::format f = tex(texture)->format();
    gli::texture::extent_type e = tex(texture)->extent((size_t)level);
    gli::texture::extent_type b = gli::block_extent(f);
    int64_t blocks = (e.x + b.x - 1) / b.x;
    return blocks * (int64_t)gli::block_size(f);
}

API int64_t blocksize(void* texture)
{
    if (!texture) return 0;
    return (int64_t)gli::block_size(tex(texture)->format());
}

API int3 blockextent(void* texture)
{
    int3 zero = { 0, 0, 0 };
    if (!texture) return zero;
    return to_int3(gli::block_extent(tex(texture)->format()));
}

API bool iscompressed(void* texture)
{
    if (!texture) return false;
    return gli::is_compressed(tex(texture)->format());
}
