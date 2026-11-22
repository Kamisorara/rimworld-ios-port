using System;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// Stand-ins for the types Ludeon's runtime fork ("WaylithClr", living in its System.Runtime) adds for the engine's
/// profiler: live method-body replacement for instrumentation, and allocation/pause events; and, since 1.7.4995, for
/// field injection, through which the game's Prepatcher layer (Waylith.Injection and RimWorldWaylith.PrepatcherCompat,
/// set up by the launcher at every start) gives mods the fields they add to the game's classes; and, since 1.7.4998, for
/// the transformer pipeline the profiler's instrumentation now goes through and the new MethodBodyUpdater members that
/// the profiler, the Prepatcher layer's free patches (there since 1.7.4995) and its field injection now use. The stock
/// runtime has none of that, so they report "not supported": the profiler keeps its instrumentation off, and the
/// Prepatcher layer finds field injection off and logs that free patches need WaylithCLR, as on a PC whose runtime has
/// these disabled.
/// The device-side setup (setup/Setup.cs, RetargetWaylithClr) retargets the engine's references from System.Runtime to
/// this assembly (the same surgery as patch/Patch.cs in the Android port).
/// </summary>
namespace WaylithClr
{
    public static class MethodBodyUpdater
    {
        public static bool IsSupported => false;

        public static int MetadataUpdateVersion => 0;

        public static int GetTokenForLocals(MethodBase method, byte[] localsSignature)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenFor(MethodBase anchor, Type type)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenFor(MethodBase anchor, MethodBase method)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int[] GetUpdatedMethodTokens(Module module)
        {
            return Array.Empty<int>();
        }

        public static void ReplaceMethodBodies(ReadOnlySpan<MethodBase> methods, ReadOnlySpan<byte> bodies, ReadOnlySpan<int> bodyOffsets, ReadOnlySpan<ILOffsetMapEntry> offsetMaps, ReadOnlySpan<int> offsetMapOffsets)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static void ReplaceMethodBody(MethodBase method, ReadOnlySpan<byte> body, ReadOnlySpan<ILOffsetMapEntry> offsetMap)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static void Revert(ReadOnlySpan<MethodBase> methods)
        {
        }

        // The overloads that name the module the new IL is for (the game's Prepatcher layer since 1.7.4995).
        public static int GetTokenFor(Module moduleForIL, MethodBase member)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenForVarargCall(Module moduleForIL, MethodBase method, Type[] optionalParameterTypes)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenFor(Module moduleForIL, FieldInfo field)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenForDefinition(Module moduleForIL, MethodBase member)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenForDefinition(Module moduleForIL, FieldInfo field)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenFor(Module moduleForIL, Type type)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenForInstantiation(Module moduleForIL, Type genericTypeDefinition)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenFor(Module moduleForIL, string literal)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static int GetTokenForLocals(Module moduleForIL, byte[] localSignature)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static void ReplaceMethodBodies(ReadOnlySpan<MethodBase> methods, ReadOnlySpan<ReadOnlyMemory<byte>> methodBodies, ReadOnlySpan<ReadOnlyMemory<ILOffsetMapEntry>> ilOffsetMaps = default)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        // Since 1.7.4998: the engine's profiler reads and rewrites bodies by method handle (MethodBodyTransformer below), and
        // the game's Prepatcher layer runs free patches and field injection through these new members. The profiler and the
        // free patches ask MethodBodyUpdater.IsSupported first, field injection FieldInjection.IsSupported, so none of this
        // is reached here. Handles, and tokens of ordinary methods, are answered through reflection with the results
        // Ludeon's runtime gives; bodies are never read, and dynamic methods and new tokens are refused.
        public static bool TryGetDefaultMethodBody(RuntimeMethodHandle method, Span<MethodBodyClause> clauses, out MethodBodyView body)
        {
            body = default;
            return false;
        }

        public static bool TryGetCurrentMethodBody(RuntimeMethodHandle method, Span<MethodBodyClause> clauses, out MethodBodyView body)
        {
            body = default;
            return false;
        }

        public static bool TryGetDefaultMethodBody(MethodBase method, Span<MethodBodyClause> clauses, out MethodBodyView body)
        {
            body = default;
            return false;
        }

        public static bool TryGetCurrentMethodBody(MethodBase method, Span<MethodBodyClause> clauses, out MethodBodyView body)
        {
            body = default;
            return false;
        }

        public static object ResolveToken(RuntimeMethodHandle method, int token)
        {
            return ResolveToken(GetMethodFromHandle(method), token);
        }

        public static object ResolveToken(MethodBase method, int token)
        {
            ArgumentNullException.ThrowIfNull(method);
            if (method is System.Reflection.Emit.DynamicMethod)
            {
                throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
            }
            Type declaringType = method.DeclaringType;
            Type[] typeArguments = declaringType != null && declaringType.IsGenericType ? declaringType.GetGenericArguments() : null;
            Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            switch (token & unchecked((int)0xFF000000))
            {
                case 0x70000000: return method.Module.ResolveString(token);
                case 0x11000000: return method.Module.ResolveSignature(token);
                default: return method.Module.ResolveMember(token, typeArguments, methodArguments);
            }
        }

        public static RuntimeMethodHandle ResolveMethodDef(Module module, int methodDefToken)
        {
            ArgumentNullException.ThrowIfNull(module);
            return module.ResolveMethod(methodDefToken).MethodHandle;
        }

        public static RuntimeMethodHandle GetMethodHandle(MethodBase method)
        {
            ArgumentNullException.ThrowIfNull(method);
            return method.MethodHandle;
        }

        public static MethodBase GetMethodFromHandle(RuntimeMethodHandle method)
        {
            return MethodBase.GetMethodFromHandle(method);
        }

        // Since 1.7.5014 (not yet in the Android port's stand-ins, which track the 4998 lineage): the handle of a
        // method's definition. Answered through reflection, like the other handle members.
        public static RuntimeMethodHandle GetDefinitionHandle(MethodBase method)
        {
            ArgumentNullException.ThrowIfNull(method);
            return method.MethodHandle;
        }

        public static int GetTokenForSignature(Module moduleForIL, ReadOnlySpan<byte> signature)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static void ReplaceMethodBody(MethodBase method, ReadOnlySpan<byte> methodBody, ReadOnlySpan<ILOffsetMapEntry> ilOffsetMap = default, object keepAlive = null)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public static void ReplaceMethodBodies(ReadOnlySpan<RuntimeMethodHandle> methods, ReadOnlySpan<byte> methodBodies, ReadOnlySpan<int> methodBodyLengths, ReadOnlySpan<ILOffsetMapEntry> ilOffsetMaps = default, ReadOnlySpan<int> ilOffsetMapCounts = default, ReadOnlySpan<object> keepAlives = default)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }
    }

    /// <summary>
    /// The pipeline the engine's profiler (since 1.7.4998) hands method bodies through: registered transformers rewrite a
    /// method's IL in turn, and the runtime swaps the result in. Here, as on a runtime that cannot replace method bodies,
    /// Register refuses with Ludeon's exception and message, so no transformer ever runs and no builder is ever made.
    /// </summary>
    public abstract class MethodBodyTransformer
    {
        public int Priority { get; }

        public bool IsRegistered => false;

        protected virtual bool StaysAttached => false;

        protected virtual bool CanTransformConcurrently => false;

        /// <summary>Never raised: no assembly is ever updated in place.</summary>
        public static event Action<Module, ReadOnlyMemory<int>, ReadOnlyMemory<int>> DefaultBodiesChanged
        {
            add { }
            remove { }
        }

        public static event Action<Exception> UnobservedException
        {
            add { }
            remove { }
        }

        protected MethodBodyTransformer(int priority)
        {
            Priority = priority;
        }

        public void Register()
        {
            throw new NotSupportedException("Method body replacement not supported in this runtime.");
        }

        public void Unregister()
        {
        }

        public Exception Rebuild(RuntimeMethodHandle method)
        {
            MethodBodyTransformFailure[] failures = Rebuild(new[] { method });
            return failures.Length >= 1 ? failures[0].Exception : null;
        }

        public MethodBodyTransformFailure[] Rebuild(ReadOnlySpan<RuntimeMethodHandle> methods)
        {
            throw new InvalidOperationException("The transformer must be registered first.");
        }

        public static void ProcessMetadataUpdates()
        {
        }

        protected abstract bool TryTransform(RuntimeMethodHandle method, in MethodBodyView body, ReadOnlySpan<MethodBodyClause> clauses, MethodBodyBuilder result);

        protected virtual void Prepare(RuntimeMethodHandle method, in MethodBodyView body, ReadOnlySpan<MethodBodyClause> clauses)
        {
        }

        protected virtual bool CanTransform(RuntimeMethodHandle method)
        {
            return true;
        }

        protected virtual void OnTransformFailed(RuntimeMethodHandle method, Exception exception)
        {
        }

        protected virtual void OnParticipationChanged(ReadOnlySpan<RuntimeMethodHandle> entered, ReadOnlySpan<RuntimeMethodHandle> left)
        {
        }

        protected virtual void OnDefaultBodiesChanged(ReadOnlySpan<RuntimeMethodHandle> methods)
        {
        }
    }

    public sealed class MethodBodyBuilder
    {
        public MethodBodyFlags Flags { get; set; }

        internal MethodBodyBuilder()
        {
        }

        public void KeepAlive(object value)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public void SetInputVariant(RuntimeMethodHandle variant)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public void SetInputVariant(MethodBase variant)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public void AddOffsetMapEntries(ReadOnlySpan<ILOffsetMapEntry> entries)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public int GetTokenFor(MethodBase member)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public int GetTokenFor(FieldInfo field)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public int GetTokenFor(Type type)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public int GetTokenFor(string literal)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public int GetTokenForVarargCall(MethodBase target, Type[] optionalParameterTypes)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }

        public int GetTokenForSignature(ReadOnlySpan<byte> signature)
        {
            throw new NotSupportedException("MethodBodyUpdater is not available on this runtime");
        }
    }

    /// <summary>Only ever default here: no body is read.</summary>
    public readonly struct MethodBodyView
    {
        private readonly byte[] il;
        private readonly byte[] localSignature;

        public ReadOnlySpan<byte> IL => il;

        public ReadOnlySpan<byte> LocalSignature => localSignature;

        public int LocalSignatureToken { get; }

        public int MaxStackSize { get; }

        public bool InitLocals { get; }

        public int ClauseCount { get; }

        public bool IsDefault { get; }

        public MethodBodyFlags Flags { get; }
    }

    public readonly struct MethodBodyClause
    {
        public ExceptionHandlingClauseOptions Flags { get; init; }

        public int TryOffset { get; init; }

        public int TryLength { get; init; }

        public int HandlerOffset { get; init; }

        public int HandlerLength { get; init; }

        public int ClassTokenOrFilterOffset { get; init; }
    }

    [Flags]
    public enum MethodBodyFlags
    {
        None = 0,
        Forwarder = 1
    }

    public readonly struct MethodBodyTransformFailure
    {
        public RuntimeMethodHandle Method { get; }

        public Exception Exception { get; }

        internal MethodBodyTransformFailure(RuntimeMethodHandle method, Exception exception)
        {
            Method = method;
            Exception = exception;
        }
    }

    /// <summary>
    /// Ludeon's runtime adds instance fields to classes that are already loaded: a batch of fields is committed, then
    /// applied at a frame boundary, where the live objects move to the new layout. The stand-ins behave as the PC's
    /// runtime does with the feature off: a batch takes fields, refuses to be committed, and nothing is ever pending.
    /// </summary>
    public static class FieldInjection
    {
        public static bool IsSupported => false;

        /// <summary>Never changes: no class is ever laid out anew.</summary>
        public static int LayoutEpoch => 0;

        public static int PendingCount => 0;

        public static FieldInjectionApplyResult ApplyPending()
        {
            return new FieldInjectionApplyResult();
        }
    }

    public enum FieldInjectionBatchStatus
    {
        Building,
        Queued,
        Applied,
        Failed
    }

    public enum FieldInjectionRefusal
    {
        None,
        CompileGateBusy,
        DependentFrames,
        PinnedInstance,
        NoGCRegions,
        GCNotLatched,
        GCBusy
    }

    public sealed class FieldInjectionApplyResult
    {
        public IReadOnlyList<FieldInjectionBatch> Applied { get; } = Array.Empty<FieldInjectionBatch>();

        public IReadOnlyList<FieldInjectionBatch> Pending { get; } = Array.Empty<FieldInjectionBatch>();

        public IReadOnlyList<MethodBase> RejittedMethods { get; } = Array.Empty<MethodBase>();

        public IReadOnlyList<MethodBase> StaleDynamicMethods { get; } = Array.Empty<MethodBase>();

        public long MigratedInstances => 0;

        public bool AllApplied => Pending.Count == 0;

        internal FieldInjectionApplyResult()
        {
        }
    }

    public sealed class FieldInjectionBatch
    {
        private readonly List<InjectedField> fields = new List<InjectedField>();

        public IReadOnlyList<InjectedField> Fields => fields;

        /// <summary>Stays Building: Ludeon's Commit, too, refuses before it queues anything when the feature is off.</summary>
        public FieldInjectionBatchStatus Status => FieldInjectionBatchStatus.Building;

        public FieldInjectionRefusal LastRefusal => FieldInjectionRefusal.None;

        public string PendingReason => null;

        public Exception Error => null;

        public long MigratedInstances => 0;

        public InjectedField AddField(Type declaringType, string name, Type fieldType, FieldAttributes attributes = FieldAttributes.Public)
        {
            ArgumentNullException.ThrowIfNull(declaringType);
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(fieldType);
            var field = new InjectedField(declaringType, name, fieldType, attributes);
            fields.Add(field);
            return field;
        }

        /// <summary>The default and the initializers are not kept: a batch is never applied here.</summary>
        public void SetDefault(InjectedField field, object value)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!fields.Contains(field))
            {
                throw new ArgumentException("WaylithClr.FieldInjection: the field does not belong to this batch.", nameof(field));
            }
        }

        public void OnMigrated(Type declaringType, Action<object> initializer)
        {
            ArgumentNullException.ThrowIfNull(declaringType);
            ArgumentNullException.ThrowIfNull(initializer);
        }

        /// <summary>Ludeon's exception and message, so that what a mod logs reads as on the PC with the feature off.</summary>
        public void Commit()
        {
            throw new NotSupportedException("WaylithClr.FieldInjection: not supported (enable with DOTNET_FieldInjection=1, requires MethodBodyUpdater and the stock regions GC).");
        }

        public FieldInjectionApplyResult CommitAndApply()
        {
            Commit();
            return FieldInjection.ApplyPending();
        }
    }

    public sealed class InjectedField
    {
        public Type DeclaringType { get; }

        public string Name { get; }

        public Type FieldType { get; }

        public FieldAttributes Attributes { get; }

        /// <summary>0 and null: the field is made only when its batch is applied.</summary>
        public int MetadataToken => 0;

        public FieldInfo Field => null;

        internal InjectedField(Type declaringType, string name, Type fieldType, FieldAttributes attributes)
        {
            DeclaringType = declaringType;
            Name = name;
            FieldType = fieldType;
            Attributes = attributes;
        }
    }

    public struct ILOffsetMapEntry
    {
        public int OriginalOffset;
        public int NewOffset;

        public ILOffsetMapEntry(int originalOffset, int newOffset)
        {
            OriginalOffset = originalOffset;
            NewOffset = newOffset;
        }
    }

    public static class SelfProfiler
    {
        public enum RuntimePauseReason
        {
            Other,
            GC,
            AppDomainShutdown,
            ReJit,
            Shutdown,
            Debugger,
            GCPrep,
            DebuggerSweep,
            Profiler
        }

        public struct RuntimePause
        {
            public long StartTimestamp;
            public long ThreadsSuspendedTimestamp;
            public long EndTimestamp;
            public RuntimePauseReason Reason;
            public int CondemnedGeneration;
            public int TriggeringThreadId;
            public long PromotedBytes;
            public long CollectedBytes;
        }

        /// <summary>Never raised: the stock runtime has no allocation callback.</summary>
        public static event Action<object, IntPtr> ObjectAllocated
        {
            add { }
            remove { }
        }

        public static int DrainRuntimePauses(Span<RuntimePause> into)
        {
            return 0;
        }
    }
}
