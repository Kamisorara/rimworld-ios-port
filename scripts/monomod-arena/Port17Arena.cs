using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MonoMod.Core.Platforms;

namespace Port17
{
    // iOS has no in-process executable memory: after the StikDebug detach only the pages the
    // debugger "blessed" (rw17-jit26.js) stay executable, and the kernel refuses mprotect(W) on
    // them directly - writes must go through the host's RW alias of the same physical pages.
    // MonoMod therefore gets pages carved from the top of that arena, with BaseAddress being the
    // executable view and Memory being the writable alias view.
    public static unsafe class Port17Arena
    {
        public static nint Rx { get; private set; }
        public static nint Rw { get; private set; }
        public static int RuntimeSize { get; private set; }
        public static nint MmRx { get; private set; }
        public static nint MmRw { get; private set; }
        public static int MmSize { get; private set; }
        public static bool Available => Rx != 0 && Rw != 0 && MmSize > 0;

        public static Port17ArenaAllocator Allocator { get; internal set; }

        // Set once the macOS exhelper failed to load, so MonoMod's repeated NativeExceptionHelper
        // probes do not retry (and re-log) the same dlopen.
        public static bool ExhelperDisabled { get; private set; }

        private static readonly List<IDisposable> KeepAlive = new List<IDisposable>();

        [DllImport("libSystem", EntryPoint = "sys_icache_invalidate")]
        private static extern void sys_icache_invalidate(void* address, nuint size);

        static Port17Arena()
        {
            Rx = ParseHex("PORT17_JIT_RX");
            Rw = ParseHex("PORT17_JIT_RW");
            RuntimeSize = (int)ParseHex("PORT17_JIT_SIZE");
            MmRx = ParseHex("PORT17_MM_RX");
            MmRw = ParseHex("PORT17_MM_RW");
            MmSize = (int)ParseHex("PORT17_MM_SIZE");
        }

        private static nint ParseHex(string name)
        {
            var text = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(text))
                return 0;
            return (nint)ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        // AllocationRequest's init accessors carry a modreq on MonoMod's obfuscated IsExternalInit
        // shim, which the C# compiler refuses (CS0570); set the backing fields of a boxed copy.
        private static AllocationRequest Request(int size, int alignment, bool executable)
        {
            object boxed = new AllocationRequest(size);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(AllocationRequest).GetField("<Alignment>k__BackingField", flags)!.SetValue(boxed, alignment);
            typeof(AllocationRequest).GetField("<Executable>k__BackingField", flags)!.SetValue(boxed, executable);
            return (AllocationRequest)boxed;
        }

        public static bool InArena(nint address, int length)
        {
            if (!Available || address < Rx)
                return false;
            var offset = (ulong)(address - Rx);
            return offset + (ulong)length <= (ulong)RuntimeSize + (ulong)MmSize;
        }

        public static nint AliasOf(nint address) => Rw + (address - Rx);

        public static void MarkExhelperDisabled() => ExhelperDisabled = true;

        // iOS: AppContext.BaseDirectory is the game folder (engine.c sets APP_CONTEXT_BASE_DIRECTORY),
        // while the host ships MonoMod's native files in the .app bundle. Resolve against the
        // executing assembly first, then the game folder and the process folder; probe-load the
        // result so dyld's refusal ends up in the log instead of MonoMod's generic "Could not open".
        public static string Resolve(string fileName)
        {
            string path = null;
            string dir = Path.GetDirectoryName(typeof(Port17Arena).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
                path = Path.Combine(dir, fileName);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                string game = Path.Combine(AppContext.BaseDirectory, fileName);
                if (File.Exists(game))
                    path = game;
                else
                {
                    string exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        dir = Path.GetDirectoryName(exe);
                        if (!string.IsNullOrEmpty(dir))
                            path = Path.Combine(dir, fileName);
                    }
                    if (string.IsNullOrEmpty(path))
                        path = Path.Combine(AppContext.BaseDirectory, fileName);
                }
            }
            Console.WriteLine("[port17] monomod shim " + fileName + " -> " + path);
            if (!File.Exists(path))
                Console.WriteLine("[port17] monomod shim missing: " + path);
            try
            {
                NativeLibrary.Free(NativeLibrary.Load(path));
            }
            catch (Exception e)
            {
                Console.WriteLine("[port17] monomod shim load failed: " + e.Message);
            }
            return path;
        }

        // Fast path for ISystem.PatchData: the target lives in the granted arena, so write through
        // the host's RW alias and flush the executable view. Returns false for every other target,
        // leaving MonoMod's own Mach logic in charge.
        public static bool TryPatchViaAlias(nint target, ReadOnlySpan<byte> data, Span<byte> backup)
        {
            if (!InArena(target, data.Length))
                return false;
            var destination = new Span<byte>((void*)AliasOf(target), data.Length);
            int keep = Math.Min(backup.Length, data.Length);
            if (keep > 0)
                destination.Slice(0, keep).CopyTo(backup);
            data.CopyTo(destination);
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
                sys_icache_invalidate((void*)target, (nuint)data.Length);
            return true;
        }

        // The ICorJitCompiler vtable lives in libcoreclr's __DATA_CONST (write-protected by dyld),
        // so its compileMethod slot cannot be patched in place like MonoMod does on macOS. The
        // compiler object itself (getJit's return value) sits in writable __data: copy the vtable
        // into our arena, patch slot 0 and repoint the object instead.
        public static void SwapCompilerVtable(nint jit, nint* vtableEntry, nint newCompileMethod)
        {
            nint* obj = (nint*)jit;
            nint oldVtable = *obj;
            Console.WriteLine($"[port17] monomod jit vtable: object 0x{jit:x} vtable 0x{oldVtable:x} slot0 0x{(oldVtable == 0 ? 0 : ((nint*)oldVtable)[0]):x} -> hook 0x{newCompileMethod:x}");
            if (oldVtable == 0 || (nint)vtableEntry < oldVtable || (nint)vtableEntry >= oldVtable + 16 * IntPtr.Size)
                throw new InvalidOperationException("unexpected ICorJitCompiler vtable layout");
            const int Count = 8; // .NET 11 has 4 slots; copying a few extra __DATA_CONST words is harmless
            if (!Allocator.TryAllocate(Request(Count * IntPtr.Size, 16, false), out var copy))
                throw new InvalidOperationException("monomod arena exhausted while copying the jit vtable");
            var span = copy.Memory;
            for (int i = 0; i < Count; i++)
                MemoryMarshal.Write(span.Slice(i * IntPtr.Size), ((nint*)oldVtable)[i]);
            MemoryMarshal.Write(span, newCompileMethod);
            // Other threads JIT concurrently: publish the copy contents before the pointer (ARM64
            // has no implicit store-store order; Volatile.Write is stlr).
            Volatile.Write(ref Unsafe.AsRef<nint>((void*)obj), copy.BaseAddress);
            lock (KeepAlive)
                KeepAlive.Add(copy);
            Console.WriteLine($"[port17] monomod jit vtable swapped: copy 0x{copy.BaseAddress:x} now slot0 0x{(*(nint**)jit)[0]:x}");
        }

        // Exercised by patch-harmony's smoke mode with PORT17_MM_* pointing at a plain (RW)
        // buffer: ref-struct parameters cannot be driven through reflection from the outside.
        public static string SelfTest()
        {
            var allocator = Allocator ?? new Port17ArenaAllocator();
            int pageSize = Environment.SystemPageSize;
            var request = Request(100, 32, true);
            if (!allocator.TryAllocate(request, out var first) || !first.IsExecutable)
                throw new InvalidOperationException("executable allocation failed");
            if (first.BaseAddress % 32 != 0)
                throw new InvalidOperationException("alignment not honoured");
            first.Memory[0] = 0x42;
            first.Memory[99] = 0x43;
            if (*(byte*)first.BaseAddress != 0x42 || *((byte*)first.BaseAddress + 99) != 0x43)
                throw new InvalidOperationException("alias write not visible through the executable view");
            if (!allocator.TryAllocate(Request(8, 8, true), out var second))
                throw new InvalidOperationException("second small allocation failed");
            long gap = second.BaseAddress - first.BaseAddress;
            if (gap != 104 /* align8(100) */)
                throw new InvalidOperationException("small allocations did not pack into one page: gap " + gap);
            if (!allocator.TryAllocate(Request(pageSize, 8, true), out var whole))
                throw new InvalidOperationException("whole-page allocation failed");
            if (whole.BaseAddress % pageSize != 0)
                throw new InvalidOperationException("whole-page block is not page aligned");
            whole.Memory[pageSize - 1] = 0x55;
            if (*((byte*)whole.BaseAddress + pageSize - 1) != 0x55)
                throw new InvalidOperationException("whole-page alias write failed");
            nint wholeAddress = whole.BaseAddress;
            whole.Dispose();
            if (!allocator.TryAllocate(Request(pageSize, 8, true), out var again) || again.BaseAddress != wholeAddress)
                throw new InvalidOperationException("freed page was not reused");
            if (allocator.TryAllocateInRange(new PositionedAllocationRequest(first.BaseAddress, first.BaseAddress, first.BaseAddress + pageSize, new AllocationRequest(16)), out var near) && !(near.BaseAddress >= first.BaseAddress && near.BaseAddress < first.BaseAddress + pageSize))
                throw new InvalidOperationException("in-range allocation escaped its bounds");
            if (allocator.TryAllocateInRange(new PositionedAllocationRequest(0x1000, 0x1000, 0x2000, new AllocationRequest(16)), out var far))
                throw new InvalidOperationException("out-of-range request must fail");

            var bytes = new byte[] { 1, 2, 3, 4 };
            var backup = new byte[4];
            if (!TryPatchViaAlias(second.BaseAddress, bytes, backup))
                throw new InvalidOperationException("alias patch refused an arena address");
            if (*((byte*)second.BaseAddress + 3) != 4)
                throw new InvalidOperationException("alias patch did not land");
            if (TryPatchViaAlias(0x1000, bytes, backup))
                throw new InvalidOperationException("alias patch accepted a foreign address");

            nint fakeVtable = Marshal.AllocHGlobal(IntPtr.Size * 8);
            nint fakeObject = Marshal.AllocHGlobal(IntPtr.Size);
            for (int i = 0; i < 8; i++)
                *(nint*)(fakeVtable + i * IntPtr.Size) = 0x1000 + i;
            *(nint*)fakeObject = fakeVtable;
            SwapCompilerVtable(fakeObject, (nint*)fakeVtable, 0xDEAD);
            nint copied = *(nint*)fakeObject;
            if (copied < MmRx || copied >= MmRx + MmSize || *(nint*)copied != 0xDEAD || *(nint*)(copied + IntPtr.Size) != 0x1001)
                throw new InvalidOperationException("vtable swap did not produce a patched copy");
            return $"ok: first=0x{first.BaseAddress:x} second=0x{second.BaseAddress:x} (gap {gap}) whole=0x{wholeAddress:x} copy=0x{copied:x}";
        }
    }

    // Page allocator over the MonoMod slice of the granted arena. Allocations at most half a page
    // are packed into the current partial page of their executability class (bump pointer, no
    // per-block free); bigger ones take whole pages, which Dispose returns to the bitmap.
    public sealed unsafe class Port17ArenaAllocator : IMemoryAllocator
    {
        private readonly object gate = new object();
        private readonly nint rx;
        private readonly nint rw;
        private readonly int size;
        private readonly int pageSize;
        private readonly int pageCount;
        private readonly bool[] pageUsed;
        private int execPage = -1;
        private int execUsed;
        private int dataPage = -1;
        private int dataUsed;
        private bool exhaustionLogged;

        public Port17ArenaAllocator()
        {
            Port17Arena.Allocator = this;
            rx = Port17Arena.MmRx;
            rw = Port17Arena.MmRw;
            size = Port17Arena.MmSize;
            pageSize = Environment.SystemPageSize;
            // The host slice is page aligned on device; be robust anyway (and the smoke test's
            // fake arena is an unaligned HGlobal buffer).
            ulong misaligned = (ulong)rx & (ulong)(pageSize - 1);
            if (misaligned != 0)
            {
                int adjust = pageSize - (int)misaligned;
                rx += adjust;
                rw += adjust;
                size -= adjust;
            }
            pageCount = size / pageSize;
            pageUsed = new bool[Math.Max(pageCount, 0)];
            Console.WriteLine($"[port17] monomod arena allocator: rx=0x{rx:x} rw=0x{rw:x} size=0x{size:x} pageSize=0x{pageSize:x} pages={pageCount}");
        }

        public int MaxSize => pageSize;

        public bool TryAllocate(AllocationRequest request, [NotNullWhen(true)] out IAllocatedMemory allocated)
        {
            return AllocateCore(request, out allocated);
        }

        public bool TryAllocateInRange(PositionedAllocationRequest request, [NotNullWhen(true)] out IAllocatedMemory allocated)
        {
            if (request.Target < request.LowBound || request.Target > request.HighBound)
                throw new ArgumentException("Target not between low and high", "request");
            if (!AllocateCore(request.Base, out allocated))
                return false;
            if (allocated.BaseAddress >= request.LowBound && allocated.BaseAddress < request.HighBound)
                return true;
            allocated.Dispose();
            allocated = null;
            return false;
        }

        private bool AllocateCore(AllocationRequest request, [NotNullWhen(true)] out IAllocatedMemory allocated)
        {
            int length = request.Size;
            int alignment = request.Alignment;
            if (length < 0)
                throw new ArgumentException("Size is negative", "request");
            if (alignment <= 0)
                throw new ArgumentException("Alignment is zero or negative", "request");
            if (length > pageSize)
                throw new NotSupportedException("Single allocations cannot be larger than a page");
            if (alignment > pageSize)
            {
                allocated = null;
                return false;
            }
            lock (gate)
            {
                if (pageCount == 0)
                {
                    LogExhaustion("no PORT17_MM_* arena");
                    allocated = null;
                    return false;
                }
                if (length > pageSize / 2)
                {
                    int pages = (length + pageSize - 1) / pageSize;
                    int first = FindFreeRun(pages);
                    if (first < 0)
                    {
                        LogExhaustion($"need {pages} page(s)");
                        allocated = null;
                        return false;
                    }
                    for (int i = 0; i < pages; i++)
                        pageUsed[first + i] = true;
                    allocated = new ArenaBlock(this, rx + (nint)first * pageSize, rw + (nint)first * pageSize, pages * pageSize, request.Executable, first, pages);
                    return true;
                }

                ref int partial = ref (request.Executable ? ref execPage : ref dataPage);
                ref int used = ref (request.Executable ? ref execUsed : ref dataUsed);
                if (partial >= 0)
                {
                    int start = (used + alignment - 1) & ~(alignment - 1);
                    if ((ulong)start + (ulong)length <= (ulong)pageSize)
                    {
                        used = start + length;
                        allocated = new ArenaBlock(this, rx + (nint)partial * pageSize + start, rw + (nint)partial * pageSize + start, length, request.Executable, -1, 0);
                        return true;
                    }
                }
                int page = FindFreeRun(1);
                if (page < 0)
                {
                    LogExhaustion($"need 1 page for {length} bytes");
                    allocated = null;
                    return false;
                }
                pageUsed[page] = true;
                partial = page;
                used = length;
                allocated = new ArenaBlock(this, rx + (nint)page * pageSize, rw + (nint)page * pageSize, length, request.Executable, -1, 0);
                return true;
            }
        }

        private int FindFreeRun(int pages)
        {
            for (int i = 0; i + pages <= pageCount; i++)
            {
                if (pageUsed[i])
                    continue;
                int j = 1;
                while (j < pages && !pageUsed[i + j])
                    j++;
                if (j == pages)
                    return i;
                i += j;
            }
            return -1;
        }

        private void LogExhaustion(string need)
        {
            if (exhaustionLogged)
                return;
            exhaustionLogged = true;
            Console.WriteLine($"[port17] monomod arena exhausted ({need}; {pageCount} pages of 0x{pageSize:x} at 0x{rx:x})");
        }

        private void FreePages(int first, int count)
        {
            lock (gate)
            {
                for (int i = 0; i < count; i++)
                    pageUsed[first + i] = false;
            }
        }

        private sealed unsafe class ArenaBlock : IAllocatedMemory
        {
            private readonly Port17ArenaAllocator owner;
            private readonly nint rwAddress;
            private readonly int firstPage;
            private readonly int pageCount;
            private bool disposed;

            public bool IsExecutable { get; }
            public nint BaseAddress { get; }
            public int Size { get; }

            public Span<byte> Memory
            {
                get
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    return new Span<byte>((void*)rwAddress, Size);
                }
            }

            public ArenaBlock(Port17ArenaAllocator owner, nint baseAddress, nint rwAddress, int size, bool executable, int firstPage, int pageCount)
            {
                this.owner = owner;
                BaseAddress = baseAddress;
                this.rwAddress = rwAddress;
                Size = size;
                IsExecutable = executable;
                this.firstPage = firstPage;
                this.pageCount = pageCount;
            }

            public void Dispose()
            {
                if (disposed)
                    return;
                disposed = true;
                if (pageCount > 0)
                    owner.FreePages(firstPage, pageCount);
            }
        }
    }
}
