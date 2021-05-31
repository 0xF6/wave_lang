﻿namespace ishtar
{
    using System.Linq;
    using mana.runtime;

    public unsafe class RuntimeIshtarField : ManaField
    {
        public RuntimeIshtarField(ManaClass owner, FieldName fullName, FieldFlags flags, ManaClass fieldType) : 
            base(owner, fullName, flags, fieldType)
        { }


        public int vtable_offset = 0;
        public void* default_value;


        public bool IsNeedReMapping(out int new_offset)
        {
            bool failMapping(int code)
            {
                VM.FastFail(WNE.TYPE_LOAD, 
                    $"Native aspect has incorrect mapping for '{FullName}' field. [0x{code:X}]");
                VM.ValidateLastError();
                return false;
            }

            new_offset = 0;
            var nativeAspect = Aspects.FirstOrDefault(x => x.Name == "Native");
            if (nativeAspect is null)
                return false;
            if (nativeAspect.Arguments.Count != 1)
                return failMapping(0);
            var arg = nativeAspect.Arguments.First().Value;

            if (arg is not string existName)
                return failMapping(1);

            var existField = Owner.Fields.FirstOrDefault(x => x.Name.Equals(existName));

            if (existField is null)
                return failMapping(2);

            if (existField.FieldType != FieldType)
                return failMapping(3);

            if (existField is not RuntimeIshtarField runtimeField)
                return failMapping(4);

            new_offset = runtimeField.vtable_offset;

            return true;
        }
    }
}