namespace ishtar_test
{
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using ishtar;
    using mana.backend.ishtar.light;
    using mana.extensions;
    using mana.fs;
    using mana.ishtar.emit;
    using mana.runtime;
    using Xunit;

    public static class IshtarRuntimeModuleEx
    {
        public static RuntimeIshtarMethod GetEntryPoint(this RuntimeIshtarModule module, string name)
        {
            foreach (var method in module.class_table.SelectMany(x => x.Methods))
            {
                if (!method.IsStatic)
                    continue;
                if (method.Name == $"{name}()")
                    return (RuntimeIshtarMethod)method;
            }

            return null;
        }
    }

    public class IshtarTestContext : IDisposable
    {
        private readonly string _testCase;
        private readonly ManaModuleBuilder _module;
        private Action<ClassBuilder, dynamic> _classCtor;
        private readonly dynamic _context;
        private ClassBuilder @class;
        private static readonly object guarder = new ();
        internal string UID { get; }

        public IshtarTestContext OnClassBuild(Action<ClassBuilder, dynamic> action)
        {
            _classCtor = action;
            return this;
        }

        private void OnCodeBuild(Action<ILGenerator, dynamic> ctor)
        {
            @class = _module.DefineClass($"global::test/testClass_{_testCase}_{UID}");
            _classCtor?.Invoke(@class, _context);
            var _method = @class.DefineMethod($"master_{_testCase}_{UID}", MethodFlags.Public | MethodFlags.Static,
                ManaTypeCode.TYPE_OBJECT.AsClass());

            var gen = _method.GetGenerator();
            ctor(gen, _context);
        }


        public unsafe CallFrame Execute(Action<ILGenerator, dynamic> ctor)
        {
            if (VM.watcher is DefaultWatchDog)
                VM.watcher = new TestWatchDog();
            AppVault.CurrentVault ??= new AppVault("<test-app>");
            lock (guarder)
            {
                var resolver = AppVault.CurrentVault.GetResolver();

                OnCodeBuild(ctor);

                var runtimeModule = resolver.Resolve(new IshtarAssembly(_module));

                var entry_point = runtimeModule.GetEntryPoint($"master_{_testCase}_{UID}");
                IshtarCore.INIT_ADDITIONAL_MAPPING();
                foreach (var c in ManaCore.All.OfType<RuntimeIshtarClass>())
                    c.init_vtable();
                foreach (var c in runtimeModule.class_table.OfType<RuntimeIshtarClass>())
                    c.init_vtable();
                return RunIt(entry_point);
            }
        }

        private static unsafe CallFrame RunIt(RuntimeIshtarMethod entry)
        {
            if (VM.watcher is DefaultWatchDog)
                VM.watcher = new TestWatchDog();
            var args_ = stackalloc stackval[1];
            var frame = new CallFrame
            {
                args = args_,
                method = entry,
                level = 0
            };
            VM.exec_method(frame);
            return frame;
        }

        public IshtarTestContext(string testCase, ManaModuleBuilder module)
        {
            _testCase = testCase;
            _module = module;
            UID = Guid.NewGuid().ToString().Where(char.IsLetter).Join();
            _context = new ExpandoObject();
            VM.watcher = new TestWatchDog();
        }
        public void Dispose()
        {
            StringStorage.storage_l.Clear();
            StringStorage.storage_r.Clear();
            VM.CurrentException = null;
        }

        public void EnsureType(QualityTypeName type) => _module.InternTypeName(type);
    }

    public abstract class IshtarTestBase : IDisposable
    {
        public static class T
        {
            public static RuntimeIshtarClass VOID => ManaTypeCode.TYPE_VOID.AsRuntimeClass();
            public static RuntimeIshtarClass OBJECT => ManaTypeCode.TYPE_OBJECT.AsRuntimeClass();
        }



        private static ManaModuleBuilder _module_instance;
        private static ManaModule _corlib;
        protected static ManaModuleBuilder _module
        {
            get
            {
                if (_module_instance is null)
                    return _module_instance = new ManaModuleBuilder("tst") { Deps = new List<ManaModule> { _corlib } };
                return _module_instance;
            }
        }

        private static volatile bool isInited = false;

        protected IshtarTestBase()
        {
            if (VM.watcher is DefaultWatchDog)
                VM.watcher = new TestWatchDog();
            AppVault.CurrentVault ??= new AppVault("<test-app>");
            lock (guarder)
            {
                if (!isInited)
                {
                    ManaCore.Init();
                    IshtarGC.INIT();
                    FFI.INIT();
                    _corlib = LoadCorLib();
                    IshtarCore.INIT_ADDITIONAL_MAPPING();
                    foreach (var @class in ManaCore.All.OfType<RuntimeIshtarClass>())
                        @class.init_vtable();
                    // ReSharper disable once VirtualMemberCallInConstructor
                    StartUp();
                    isInited = true;
                }
            }
        }

        private static ManaModule LoadCorLib()
        {
            var resolver = AppVault.CurrentVault.GetResolver();
            resolver.AddSearchPath(new DirectoryInfo("./"));
            return resolver.ResolveDep("corlib", new Version(1, 0, 0), new List<ManaModule>());
        }

        private static readonly object guarder = new ();


        protected IshtarTestContext CreateContext([CallerMemberName] string caller = "<unnamed>")
            => new(caller, _module);
        void IDisposable.Dispose() => Shutdown();

        protected void Validate()
        {
        }

        protected virtual void StartUp() { }
        protected virtual void Shutdown() { }
    }
}
