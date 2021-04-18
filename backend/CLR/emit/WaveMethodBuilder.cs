﻿namespace wave.clr.emit
{
    using System.Reflection;
    using System.Reflection.Emit;
    using runtime;

    public class WaveMethodBuilder : WaveMethod, IBaker
    {
        internal readonly WaveClassBuilder classBuilder;
        internal WaveModuleBuilder moduleBuilder 
            => classBuilder?.moduleBuilder;

        internal MethodBuilder clr_method_builder;


        public WaveMethodBuilder(WaveClassBuilder clazz, string name, WaveType returnType, MethodFlags flags, params WaveArgumentRef[] args) 
            : base(name, flags, returnType, clazz, args)
        {
            classBuilder = clazz;
            clr_method_builder = clazz.classBuilder.DefineMethod(name, flags.AsCLR(), 
                CallingConventions.Standard,
                returnType.AsCLR(), args.AsCLR());
        }

        public ILGenerator GetGenerator() 
            => clr_method_builder.GetILGenerator();

        #region Implementation of IBaker

        public byte[] BakeByteArray() 
            => clr_method_builder.GetMethodBody()?.GetILAsByteArray();

        public string BakeDebugString() => clr_method_builder.ToString();

        #endregion
    }
}