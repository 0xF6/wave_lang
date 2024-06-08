namespace ishtar.runtime.gc
{
    using allocators;
    using ishtar;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using collections;
    using vein.runtime;
    using static vein.runtime.VeinTypeCode;
    using LLVMSharp;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void IshtarFinalizationProc(nint p, nint i);

    // ReSharper disable once ClassTooBig
    public unsafe class IshtarGC(VirtualMachine vm) : IDisposable
    {
        public readonly GCStats Stats = new();
        private readonly LinkedList<nint> RefsHeap = new();
        private readonly LinkedList<nint> ArrayRefsHeap = new();
        private readonly LinkedList<nint> ImmortalHeap = new();
        private readonly LinkedList<nint> TemporaryHeap = new();

        private readonly LinkedList<AllocationDebugInfo> allocationTreeDebugInfos = new();
        private readonly Dictionary<nint, AllocationDebugInfo> allocationDebugInfos = new();

        private IIshtarAllocatorPool allocatorPool;

#if BOEHM_GC
        private static readonly GCLayout gcLayout = new BoehmGCLayout();
#else
        private static GCLayout gcLayout = null;
#error No defined GC layout
#endif

        public string DebugGet() =>
            $"RefsHeap: {RefsHeap.Count}\n" +
            $"ArrayRefsHeap: {ArrayRefsHeap.Count}\n" +
            $"ImmortalHeap: {ImmortalHeap.Count}\n" +
            $"TemporaryHeap: {TemporaryHeap.Count}\n";

        public class GCStats
        {
            private ulong TotalAllocation;
            private ulong TotalBytesAllocated;

            public long total_allocations
            {
                get => (long)TotalAllocation;
                set => TotalAllocation = checked((ulong)value);
            }

            public long total_bytes_requested
            {
                set => TotalBytesAllocated = checked((ulong)value);
                get => (long)TotalBytesAllocated;
            }
            public ulong alive_objects;
        }

        public record AllocationDebugInfo(ulong BytesAllocated, string Method, nint pointer)
        {
            public string Trace;

            public void Bump() => Trace = new StackTrace(true).ToString();
        }


        public enum GcColor
        {
            RED,
            GREEN,
            YELLOW
        }


        public void init()
        {
            if (is_inited)
                throw new NotImplementedException();
            allocatorPool = new IshtarAllocatorPool(gcLayout);

            is_inited = true;
        }

        private bool is_inited;

        public VirtualMachine VM => vm;
        public bool check_memory_leak = true;

        private bool is_disposed;
        public static string previousDisposeStackTrace;

        public void Dispose()
        {
            if (is_disposed)
                throw new InvalidOperationException();
            is_disposed = true;
            previousDisposeStackTrace = Environment.StackTrace;
            var gcHeapSizeBefore = gcLayout.get_heap_size();
            var gcFreeBytesBefore = gcLayout.get_free_bytes();
            var gcHeapUsageBefore = gcLayout.get_heap_usage();

            gcLayout.collect();
            //var hasCollected = gcLayout.try_collect();
            gcLayout.finalize_all();

            foreach (var p in RefsHeap.ToArray())
            {
                FreeObject((IshtarObject*)p, VM.Frames.GarbageCollector);
            }
            RefsHeap.Clear();

            foreach (var p in ArrayRefsHeap.ToArray())
            {
                FreeArray((IshtarArray*)p, VM.Frames.GarbageCollector);
            }
            ArrayRefsHeap.Clear();

            //hasCollected = gcLayout.try_collect();

            if (!check_memory_leak) return;

            var gcHeapSize = gcLayout.get_heap_size();
            var gcFreeBytes = gcLayout.get_free_bytes();
            var gcHeapUsage = gcLayout.get_heap_usage();

            if (gcHeapUsage.pbytes_since_gc != 0)
            {
                vm.FastFail(WNE.MEMORY_LEAK, $"After clear all allocated memory, total_bytes_requested is not zero ({Stats.total_bytes_requested})", VM.Frames.GarbageCollector);
                return;
            }
        }

        private void InsertDebugData(AllocationDebugInfo info)
        {
            allocationTreeDebugInfos.AddLast(info);
            allocationDebugInfos[info.pointer] = info;
            info.Bump();
        }

        private void DeleteDebugData(nint pointer)
            => allocationTreeDebugInfos.Remove(allocationDebugInfos[pointer]);

        /// <exception cref="OutOfMemoryException">Allocating stackval of memory failed.</exception>
        public stackval* AllocValue(CallFrame frame)
        {
            var allocator = allocatorPool.Rent<stackval>(out var p, AllocationKind.no_reference, frame);

            Stats.total_allocations++;
            Stats.total_bytes_requested += allocator.TotalSize;

            TemporaryHeap.AddLast((nint)p);
            InsertDebugData(new((ulong)sizeof(stackval),
                nameof(AllocValue), (nint)p));

            return p;
        }


        public stackval* AllocateStack(CallFrame frame, int size)
        {
            var allocator = allocatorPool.RentArray<stackval>(out var p, size, frame);
            vm.println($"Allocated stack '{size}' for '{frame.method->Name}'");

            Stats.total_allocations++;
            Stats.total_bytes_requested += allocator.TotalSize;

            InsertDebugData(new((ulong)allocator.TotalSize,
                nameof(AllocateStack), (nint)p));

            ImmortalHeap.AddLast((nint)p);

            return p;
        }

        public void FreeStack(CallFrame frame, stackval* stack, int size)
        {
            ImmortalHeap.Remove((nint)stack);
            DeleteDebugData((nint)stack);
            Stats.total_allocations--;
            Stats.total_bytes_requested -= allocatorPool.Return(stack);
        }

        public void UnsafeAllocValueInto(RuntimeIshtarClass* @class, stackval* pointer)
        {
            if (!@class->IsPrimitive)
                return;
            pointer->type = @class->TypeCode;
            pointer->data.l = 0;
        }

        /// <exception cref="OutOfMemoryException">Allocating stackval of memory failed.</exception>
        public stackval* AllocValue(VeinClass @class, CallFrame frame)
        {
            if (!@class.IsPrimitive)
                return null;
            var p = AllocValue(frame);
            p->type = @class.TypeCode;
            p->data.l = 0;
            return p;
        }

        public void FreeValue(stackval* value)
        {
            TemporaryHeap.Remove((nint)value);
            DeleteDebugData((nint)value);
            Stats.total_allocations--;
            Stats.total_bytes_requested -= allocatorPool.Return(value);
        }

        public void FreeArray(IshtarArray* array, CallFrame frame)
        {
            DeleteDebugData((nint)array);
            ArrayRefsHeap.Remove((nint)array);
            Stats.total_bytes_requested -= allocatorPool.Return(array);
            Stats.total_allocations--;
            Stats.alive_objects--;
        }

        public IshtarArray* AllocArray(RuntimeIshtarClass* @class, ulong size, byte rank, CallFrame frame)
        {
            if (!@class->is_inited)
                throw new NotImplementedException();

            if (size >= IshtarArray.MAX_SIZE)
            {
                vm.FastFail(WNE.OVERFLOW, "", frame);
                return null;
            }

            if (rank != 1)
            {
                vm.FastFail(WNE.TYPE_LOAD, "Currently array rank greater 1 not supported.", frame);
                return null;
            }


            var arr = TYPE_ARRAY.AsRuntimeClass(vm.Types);
            var bytes_len = @class->computed_size * size * rank;

            // enter critical zone
            //IshtarSync.EnterCriticalSection(ref @class->Owner->Interlocker.INIT_ARRAY_BARRIER);

            if (!arr->is_inited) arr->init_vtable(vm);

            var obj = AllocObject(arr, frame);

            var allocator = allocatorPool.Rent<IshtarArray>(out var arr_obj, AllocationKind.reference, frame);

            if (arr_obj is null)
            {
                vm.FastFail(WNE.OUT_OF_MEMORY, "", frame);
                return null;
            }

            // validate fields
            ForeignFunctionInterface.StaticValidateField(frame, &obj, "!!value");
            ForeignFunctionInterface.StaticValidateField(frame, &obj, "!!block");
            ForeignFunctionInterface.StaticValidateField(frame, &obj, "!!size");
            ForeignFunctionInterface.StaticValidateField(frame, &obj, "!!rank");

            // fill array block
            arr_obj->SetMemory(obj);
            arr_obj->element_clazz = @class;
            arr_obj->_block.offset_value = arr->Field["!!value"]->vtable_offset;
            arr_obj->_block.offset_block = arr->Field["!!block"]->vtable_offset;
            arr_obj->_block.offset_size = arr->Field["!!size"]->vtable_offset;
            arr_obj->_block.offset_rank = arr->Field["!!rank"]->vtable_offset;

            // fill live table memory
            obj->vtable[arr_obj->_block.offset_value] = (void**)allocator.AllocZeroed(bytes_len, AllocationKind.no_reference, frame);
            obj->vtable[arr_obj->_block.offset_block] = (long*)@class->computed_size;
            obj->vtable[arr_obj->_block.offset_size] = (long*)size;
            obj->vtable[arr_obj->_block.offset_rank] = (long*)rank;

            // fill array block memory
            for (var i = 0UL; i != size; i++)
                ((void**)obj->vtable[arr->Field["!!value"]->vtable_offset])[i] = AllocObject(@class, frame);

            // exit from critical zone
            //IshtarSync.LeaveCriticalSection(ref @class.Owner.Interlocker.INIT_ARRAY_BARRIER);


            InsertDebugData(new(checked((ulong)allocator.TotalSize),
                nameof(AllocArray), (nint)arr_obj));

#if DEBUG
            arr_obj->__gc_id = (long)Stats.alive_objects++;
#else
            GCStats.alive_objects++;
#endif


            Stats.total_bytes_requested += allocator.TotalSize;
            Stats.total_allocations++;

            ArrayRefsHeap.AddLast((nint)arr_obj);

            return arr_obj;
        }


        private readonly Dictionary<RuntimeToken, nint> _types_cache = new();
        private readonly Dictionary<RuntimeToken, Dictionary<string, nint>> _fields_cache = new();
        private readonly Dictionary<RuntimeToken, Dictionary<string, nint>> _methods_cache = new();


        // TODO
        public void** AllocVTable(uint size)
        {
            var p = (void**)gcLayout.alloc((uint)(size * sizeof(void*)));

            if (p is null)
                vm.FastFail(WNE.TYPE_LOAD, "Out of memory.", vm.Frames.GarbageCollector);
            return p;
        }

        public IshtarObject* AllocTypeInfoObject(RuntimeIshtarClass* @class, CallFrame frame)
        {
            if (!@class->is_inited)
                throw new NotImplementedException();

            //IshtarSync.EnterCriticalSection(ref @class.Owner.Interlocker.INIT_TYPE_BARRIER);

            if (_types_cache.TryGetValue(@class->runtime_token, out nint value))
                return (IshtarObject*)value;

            var tt = KnowTypes.Type(frame);
            var obj = AllocObject(tt, frame);
            var gc = frame.GetGC();

            obj->flags |= GCFlags.IMMORTAL;

            obj->vtable[tt->Field["_unique_id"]->vtable_offset] = gc.ToIshtarObjectT(@class->runtime_token.ClassID, frame);
            obj->vtable[tt->Field["_module_id"]->vtable_offset] = gc.ToIshtarObjectT(@class->runtime_token.ModuleID, frame);
            obj->vtable[tt->Field["_flags"]->vtable_offset] = gc.ToIshtarObject((int)@class->Flags, frame);
            obj->vtable[tt->Field["_name"]->vtable_offset] = gc.ToIshtarObject(@class->Name, frame);
            obj->vtable[tt->Field["_namespace"]->vtable_offset] = gc.ToIshtarObject(@class->FullName->Namespace, frame);

            _types_cache[@class->runtime_token] = (nint)obj;

            //IshtarSync.LeaveCriticalSection(ref @class.Owner.Interlocker.INIT_TYPE_BARRIER);

            return obj;
        }

        public IshtarObject* AllocFieldInfoObject(RuntimeIshtarField* field, CallFrame frame)
        {
            var @class = field->Owner;
            if (!@class->is_inited)
                throw new NotImplementedException();

            var name = field->Name;
            var gc = frame.GetGC();

            //IshtarSync.EnterCriticalSection(ref @class.Owner.Interlocker.INIT_FIELD_BARRIER);

            if (_fields_cache.ContainsKey(@class->runtime_token) && _fields_cache[@class->runtime_token].ContainsKey(name))
                return (IshtarObject*)_fields_cache[@class->runtime_token][name];

            var tt = KnowTypes.Field(frame);
            var obj = AllocObject(tt, frame);

            obj->flags |= GCFlags.IMMORTAL;

            var field_owner = AllocTypeInfoObject(@class, frame);

            obj->vtable[tt->Field["_target"]->vtable_offset] = field_owner;
            obj->vtable[tt->Field["_name"]->vtable_offset] = gc.ToIshtarObject(name, frame);
            obj->vtable[tt->Field["_vtoffset"]->vtable_offset] = gc.ToIshtarObject((long)field->vtable_offset, frame);

            if (!_fields_cache.ContainsKey(@class->runtime_token))
                _fields_cache[@class->runtime_token] = new();
            _fields_cache[@class->runtime_token][name] = (nint)obj;

            //IshtarSync.LeaveCriticalSection(ref @class.Owner.Interlocker.INIT_FIELD_BARRIER);

            return obj;
        }


        public IshtarObject* AllocMethodInfoObject(RuntimeIshtarMethod* method, CallFrame frame)
        {
            var @class = method->Owner;
            if (!@class->is_inited)
                throw new NotImplementedException();

            var key = method->Name;
            var gc = frame.GetGC();

            //IshtarSync.EnterCriticalSection(ref @class.Owner.Interlocker.INIT_METHOD_BARRIER);

            if (_fields_cache.ContainsKey(@class->runtime_token) && _fields_cache[@class->runtime_token].ContainsKey(key))
                return (IshtarObject*)_fields_cache[@class->runtime_token][key];

            var tt = KnowTypes.Function(frame);
            var obj = AllocObject(tt, frame);

            obj->flags |= GCFlags.IMMORTAL;

            var method_owner = AllocTypeInfoObject(@class, frame);

            obj->vtable[tt->Field["_target"]->vtable_offset] = method_owner;
            obj->vtable[tt->Field["_name"]->vtable_offset] = gc.ToIshtarObject(method->RawName, frame);
            obj->vtable[tt->Field["_quality_name"]->vtable_offset] = gc.ToIshtarObject(method->Name, frame);
            obj->vtable[tt->Field["_vtoffset"]->vtable_offset] = gc.ToIshtarObject((long)method->vtable_offset, frame);

            if (!_methods_cache.ContainsKey(@class->runtime_token))
                _methods_cache[@class->runtime_token] = new();
            _methods_cache[@class->runtime_token][key] = (nint)obj;

            //IshtarSync.LeaveCriticalSection(ref @class.Owner.Interlocker.INIT_METHOD_BARRIER);

            return obj;
        }

        public IshtarObject* AllocObject(RuntimeIshtarClass* @class, CallFrame frame)
        {
            var allocator = allocatorPool.Rent<IshtarObject>(out var p, AllocationKind.reference, frame);

            if (!@class->is_inited)
                throw new NotImplementedException();

            p->vtable = (void**)allocator.AllocZeroed(
                (nuint)(sizeof(void*) * (long)@class->computed_size),
                AllocationKind.reference, frame);

            IshtarUnsafe.CopyBlock(p->vtable, @class->vtable, (uint)@class->computed_size * (uint)sizeof(void*));
            p->clazz = @class;
            p->vtable_size = (uint)@class->computed_size;
            p->__gc_id = (long)Stats.alive_objects++;
#if DEBUG
            p->m1 = IshtarObject.magic1;
            p->m2 = IshtarObject.magic2;
            IshtarObject.CreationTrace[p->__gc_id] = Environment.StackTrace;
#endif
            @class->computed_size = @class->computed_size;

            Stats.total_allocations++;
            Stats.total_bytes_requested += allocator.TotalSize;

            InsertDebugData(new(checked((ulong)allocator.TotalSize), nameof(AllocObject), (nint)p));
            RefsHeap.AddLast((nint)p);

            ObjectRegisterFinalizer(p, &_direct_finalizer, frame);

            return p;
        }


        private static void _direct_finalizer(nint obj, nint _)
        {
            var o = (IshtarObject*)obj;

            if (!o->IsValidObject())
            {
                #if DEBUG
                Debug.WriteLine($"Detected invalid object when calling _direct_finalizer");
                Debugger.Break();
                #endif
                return;
            }

            var vm = o->clazz->Owner->vm;
            var gc = vm.GC;

            var frame = vm.Frames.GarbageCollector;

            gc.ObjectRegisterFinalizer(o, null, frame);

            if (vm.Config.DisabledFinalization)
                return;

            var clazz = o->clazz;

            var finalizer = clazz->GetDefaultDtor();

            vm.println($"@@[dtor] called! for instance of {clazz->FullName->NameWithNS}");
            if (finalizer is not null)
            {
                vm.exec_method(new CallFrame(vm)
                {
                    args = null,
                    method = finalizer,
                    level = 1,
                    parent = frame
                });
                vm.watcher.ValidateLastError();
            }

            gc.RefsHeap.Remove((nint)o);
            gc.DeleteDebugData(obj);

            gc.Stats.total_allocations--;
            gc.Stats.alive_objects--;


            gcLayout.free((void**)&o);
        }


        public void FreeObject(IshtarObject* obj, CallFrame frame)
        {
            if (!obj->IsValidObject())
            {
                VM.FastFail(WNE.STATE_CORRUPT, "trying free memory of invalid object", frame);
                return;
            }

            if (obj->flags.HasFlag(GCFlags.NATIVE_REF))
            {
                VM.FastFail(WNE.ACCESS_VIOLATION, "trying free memory of static native object", frame);
                return;
            }
            gcLayout.register_finalizer_no_order(obj, null, frame);

            DeleteDebugData((nint)obj);
            RefsHeap.Remove((nint)obj);
            
            Stats.total_bytes_requested -= allocatorPool.Return(obj);
            Stats.total_allocations--;
            Stats.alive_objects--;

            gcLayout.free((void**)&obj);
        }

        public bool IsAlive(IshtarObject* obj)
            => obj->IsValidObject() && gcLayout.is_marked(obj);

        public void ObjectRegisterFinalizer(IshtarObject* obj, delegate*<nint, nint, void> proc, CallFrame frame)
        {
            var clazz = obj->clazz;

            //IshtarSync.EnterCriticalSection(ref clazz.Owner.Interlocker.GC_FINALIZER_BARRIER);

            gcLayout.register_finalizer_no_order(obj, proc, frame);

            //IshtarSync.LeaveCriticalSection(ref clazz.Owner.Interlocker.GC_FINALIZER_BARRIER);
        }

        public void RegisterWeakLink(IshtarObject* obj, void** link, bool longLive)
            => gcLayout.create_weak_link(link, obj, longLive);
        public void UnRegisterWeakLink(void** link, bool longLive)
            => gcLayout.unlink(link, longLive);

        public void FreeObject(IshtarObject** obj, CallFrame frame) => FreeObject(*obj, frame);

        public long GetUsedMemorySize() => gcLayout.get_heap_size() - gcLayout.get_free_bytes();

        public void Collect() => gcLayout.collect();


        #region internal

#if !BOEHM_GC
        private static readonly AllocatorBlock _allocator = new()
        {
            alloc = &IshtarGC_Alloc,
            alloc_primitives = &IshtarGC_AtomicAlloc,
            free = &IshtarGC_Free,
            realloc = &IshtarGC_Realloc
        };

#else

        private static readonly AllocatorBlock _allocator = new()
        {
            alloc = &NativeMemory_AllocZeroed,
            alloc_primitives = &NativeMemory_AllocZeroed,
            free = &NativeMemory_Free,
            realloc = &NativeMemory_Realloc
        };

#endif


        private static void* NativeMemory_AllocZeroed(uint size)
            => NativeMemory.AllocZeroed(size);

        private static void* IshtarGC_Alloc(uint size)
            => BoehmGCLayout.Native.GC_malloc(size);
        private static void* IshtarGC_AtomicAlloc(uint size)
            => BoehmGCLayout.Native.GC_malloc_atomic(size);

        private static void NativeMemory_Free(void* ptr)
            => NativeMemory.Free(ptr);

        private static void IshtarGC_Free(void* ptr)
            => BoehmGCLayout.Native.GC_free(ptr);

        private static void* NativeMemory_Realloc(void* ptr, uint newBytes)
            => NativeMemory.Realloc(ptr, newBytes);

        private static void* IshtarGC_Realloc(void* ptr, uint newBytes)
            => (void*)BoehmGCLayout.Native.GC_realloc((nint)ptr, newBytes);


        public static NativeList<T>* AllocateList<T>(int initialCapacity = 16) where T : unmanaged, IEq<T>
            => NativeList<T>.Create(initialCapacity, _allocator);


        public static void FreeList<T>(NativeList<T>* list) where T : unmanaged, IEq<T>
            => NativeList<T>.Free(list, _allocator);


        public static AtomicNativeList<T>* AllocateAtomicList<T>(int initialCapacity = 16) where T : unmanaged, IEquatable<T>
            => AtomicNativeList<T>.Create(initialCapacity, _allocator);

        public static NativeDictionary<TKey, TValue>* AllocateDictionary<TKey, TValue>(int initialCapacity = 16)
            where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged
            => NativeDictionary<TKey, TValue>.Create(initialCapacity, _allocator);

        public static AtomicNativeDictionary<TKey, TValue>* AllocateAtomicDictionary<TKey, TValue>(int initialCapacity = 16)
            where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged, IEquatable<TValue>
            => AtomicNativeDictionary<TKey, TValue>.Create(initialCapacity, _allocator);

        public static void FreeDictionary<TKey, TValue>(NativeDictionary<TKey, TValue>* list)
            where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged
            => NativeDictionary<TKey, TValue>.Free(list, _allocator);

        public static T* AllocateImmortal<T>() where T : unmanaged
        {
            //return (T*)NativeMemory.AllocZeroed((uint)sizeof(T));
            var p = (T*)gcLayout.alloc_immortal((uint)sizeof(T));
            allocatedImmortals.Add((nint)p);
            return p;
        }

        public static void* AllocateImmortal(uint size)
        {
            //return (T*)NativeMemory.AllocZeroed((uint)sizeof(T));
            var p = gcLayout.alloc_immortal(size);
            allocatedImmortals.Add((nint)p);
            return p;
        }

        public static T* AllocateImmortalRoot<T>() where T : unmanaged
        {
            //return (T*)NativeMemory.AllocZeroed((uint)sizeof(T));
            var t = (T*)gcLayout.alloc_immortal((uint)sizeof(T));
            gcLayout.add_roots(t, sizeof(T));
            return t;
        }

        public static void FreeImmortalRoot<T>(T* ptr) where T : unmanaged
        {
            gcLayout.remove_roots(ptr, sizeof(T));
            gcLayout.free((void**)&ptr);
        }

        public static T* AllocateImmortal<T>(int size) where T : unmanaged
        {
            //return (T*)NativeMemory.AllocZeroed((uint)(sizeof(T) * size));
            var p = (T*)gcLayout.alloc_immortal((uint)(sizeof(T) * size));

            allocatedImmortals.Add((nint)p);

            return p;
        }

        public static T* AllocateImmortalRoot<T>(int size) where T : unmanaged
        {
            //return (T*)NativeMemory.AllocZeroed((uint)sizeof(T));
            var t = (T*)gcLayout.alloc_immortal((uint)(sizeof(T) * size));
            gcLayout.add_roots(t, sizeof(T) * size);
            return t;
        }

        public static void FreeImmortalRoot<T>(T* ptr, int size) where T : unmanaged
        {
            gcLayout.remove_roots(ptr, sizeof(T) * size);
            gcLayout.free((void**)&ptr);
        }


        private static readonly List<nint> allocatedImmortals = new();
        private static readonly Dictionary<nint, string> disposedImmortals = new();
        public static void FreeImmortal<T>(T* t) where T : unmanaged
        {
            if (!gcLayout.isOwnerShip((void**)&t))
            {
                Debug.WriteLine($"Trying free pointer without access");
                return;
            }

            var stackTrace = Environment.StackTrace;
            if (allocatedImmortals.Remove((nint)t))
            {
                disposedImmortals[(nint)t] = stackTrace;
                gcLayout.free((void**)&t);
            }
            else if (disposedImmortals.TryGetValue((nint)t, out var result))
            {
                if (stackTrace.Equals(result))
                    return;
                throw new TryingFreeAlreadyDisposedImmortalObject(disposedImmortals[(nint)t]);
            }
            else
                throw new BadMemoryOfImmortalObject();
        }

        #endregion
    }

    public class BadMemoryOfImmortalObject : Exception;
    public class TryingFreeAlreadyDisposedImmortalObject(string stackTrace) : Exception(stackTrace);
}