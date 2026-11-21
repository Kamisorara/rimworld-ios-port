// libimgui_freetype.so: Dear ImGui's FreeType font loader (misc/freetype), with FreeType linked in, exporting the
// two functions the engine binds (imgui_freetype.dll on Windows).
#include "imgui.h"
#include "misc/freetype/imgui_freetype.h"

#define EXPORT __attribute__((visibility("default")))

extern "C" EXPORT const ImFontLoader* GetFontLoader()
{
    return ImGuiFreeType::GetFontLoader();
}

extern "C" EXPORT bool DebugEditFontLoaderFlags(ImGuiFreeTypeLoaderFlags* p_font_loader_flags)
{
    return ImGuiFreeType::DebugEditFontLoaderFlags(p_font_loader_flags);
}
