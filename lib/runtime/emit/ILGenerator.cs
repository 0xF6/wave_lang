﻿namespace wave.emit
{
    using System;
    using System.Buffers.Binary;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Text;
    using extensions;
    using runtime.emit;
    using static OpCodeValue;

    public class ILGenerator
    {
        private byte[] _ilBody;
        private int _position;
        internal readonly MethodBuilder _methodBuilder;
        private readonly StringBuilder _debugBuilder = new ();
        
        internal int LocalsSize { get; set; }
        
        public virtual int ILOffset => _position;

        public ILGenerator(MethodBuilder method) : this(method, 16) { }
        public ILGenerator(MethodBuilder method, int size)
        {
            _methodBuilder = method;
            _ilBody = new byte[Math.Max(size, 16)];
        }

        public MethodBuilder GetMethodBuilder() => _methodBuilder;

        public virtual void Emit(OpCode opcode)
        {
            _debugBuilder.AppendLine($".{opcode.Name}");
            EnsureCapacity<OpCode>();
            InternalEmit(opcode);
        }
        public virtual void Emit(OpCode opcode, byte arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} 0x{arg:X8}.byte");
            EnsureCapacity<OpCode>(sizeof(byte));
            InternalEmit(opcode);
            _ilBody[_position++] = arg;
        }
        public void Emit(OpCode opcode, sbyte arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} 0x{arg:X8}.sbyte");
            EnsureCapacity<OpCode>(sizeof(sbyte));
            InternalEmit(opcode);
            _ilBody[_position++] = (byte) arg;
        }
        public virtual void Emit(OpCode opcode, short arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} 0x{arg:X8}.short");
            EnsureCapacity<OpCode>(sizeof(short));
            InternalEmit(opcode);
            BinaryPrimitives.WriteInt16LittleEndian(_ilBody.AsSpan(_position), arg);
            _position += sizeof(short);
        }
        
        public virtual void Emit(OpCode opcode, int arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} 0x{arg:X8}.int");
            EnsureCapacity<OpCode>(sizeof(int));
            InternalEmit(opcode);
            BinaryPrimitives.WriteInt32LittleEndian(_ilBody.AsSpan(_position), arg);
            _position += sizeof(int);
        }
        public virtual void Emit(OpCode opcode, long arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} 0x{arg:X8}.long");
            EnsureCapacity<OpCode>(sizeof(long));
            InternalEmit(opcode);
            BinaryPrimitives.WriteInt64LittleEndian(_ilBody.AsSpan(_position), arg);
            _position += sizeof(long);
        }
        
        public virtual void Emit(OpCode opcode, ulong arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} 0x{arg:X8}.ulong");
            EnsureCapacity<OpCode>(sizeof(ulong));
            InternalEmit(opcode);
            BinaryPrimitives.WriteUInt64LittleEndian(_ilBody.AsSpan(_position), arg);
            _position += sizeof(ulong);
        }

        public virtual void Emit(OpCode opcode, float arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} {arg}.float");
            EnsureCapacity<OpCode>(sizeof(float));
            InternalEmit(opcode);
            BinaryPrimitives.WriteInt32LittleEndian(_ilBody.AsSpan(_position), BitConverter.SingleToInt32Bits(arg));
            _position += sizeof(float);
        }

        public virtual void Emit(OpCode opcode, double arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} {arg}.double");
            EnsureCapacity<OpCode>(sizeof(double));
            InternalEmit(opcode);
            BinaryPrimitives.WriteInt64LittleEndian(_ilBody.AsSpan(_position), BitConverter.DoubleToInt64Bits(arg));
            _position += sizeof(double);
        }
        
        public virtual void Emit(OpCode opcode, decimal arg)
        {
            _debugBuilder.AppendLine($".{opcode.Name} {arg}.decimal");
            EnsureCapacity<OpCode>(sizeof(decimal));
            InternalEmit(opcode);
            foreach (var i in decimal.GetBits(arg))
                BinaryPrimitives.WriteInt32LittleEndian(_ilBody.AsSpan(_position), i);
            _position += sizeof(decimal);
        }
        
        public virtual void Emit(OpCode opcode, string str)
        {
            var token = _methodBuilder
                .classBuilder
                .moduleBuilder
                .GetStringConstant(str);
            this.EnsureCapacity<OpCode>(sizeof(int));
            InternalEmit(opcode);
            PutInteger4(token);
            _debugBuilder.AppendLine($".{opcode.Name} '{str}'.0x{token:X8}");
        }

        public virtual void Emit(OpCode opcode, FieldName field)
        {
            if (opcode.Value != (int) LDF)
                throw new Exception("invalid opcode");
            
            var (token, direction) = this.FindFieldToken(field);

            opcode = direction switch
            {
                FieldDirection.Local => this._methodBuilder.GetLocalIndex(field) switch
                {
                    0 => OpCodes.LDLOC_0,
                    1 => OpCodes.LDLOC_1,
                    2 => OpCodes.LDLOC_2,
                    3 => OpCodes.LDLOC_3,
                    4 => OpCodes.LDLOC_4,
                    _ => throw new Exception(/* todo */)
                },
                FieldDirection.Arg => this._methodBuilder.GetArgumentIndex(field) switch
                {
                    0 => OpCodes.LDARG_0,
                    1 => OpCodes.LDARG_1,
                    2 => OpCodes.LDARG_2,
                    3 => OpCodes.LDARG_3,
                    4 => OpCodes.LDARG_4,
                    _ => throw new Exception(/* todo */)
                },
                FieldDirection.Member => throw new Exception(/* todo */),
                _ => throw new Exception(/* todo */)
            };
            this.EnsureCapacity<OpCode>();
            this.InternalEmit(opcode);
            _debugBuilder.AppendLine($".{opcode.Name} {field.Name}.{token:X8}");
        }
        
        public virtual void Emit(OpCode opcode, Label label)
        {
            this.EnsureCapacity<OpCode>(sizeof(int));
            this.InternalEmit(opcode);
            this.PutInteger4(label.Value);
            _debugBuilder.AppendLine($".{opcode.Name} :{label.Value:X8}");
        }
        
        
        public virtual void Emit(OpCode opcode, QualityTypeName type)
        {
            this.EnsureCapacity<OpCode>(sizeof(int)*3);
            this.InternalEmit(opcode);
            this.PutTypeName(type);
            _debugBuilder.AppendLine($".{opcode.Name} [{type}]");
        }
        
        public virtual void Emit(OpCode opcode, LocalsBuilder locals)
        {
            if (opcode.Value != (int) LOC_INIT)
                throw new Exception("invalid opcode");
            var size = locals.Count();
            
            this.EnsureCapacity<OpCode>(sizeof(int) + (((sizeof(int) * 3)+sizeof(ushort)) * size));
            this.InternalEmit(opcode);
            this.PutInteger4(size);
            foreach(var t in locals)
            {
                this.InternalEmit(OpCodes.LOC_INIT_X);
                this.PutTypeName(t);
                this.LocalsSize++;
            }
            var str = new StringBuilder();
            str.AppendLine(".locals { ");
            foreach(var (t, i) in locals.Select((x, y) => (x, y)))
                str.AppendLine($"\t[{i}]: {t.Name}");
            str.Append("};");
            _debugBuilder.AppendLine($"{str}");
        }

        public virtual void EmitCall(OpCode opcode, WaveMethod method)
        {
            if (method is null)
                throw new ArgumentNullException(nameof (method));
            var (tokenIdx, ownerIdx) = this._methodBuilder.classBuilder.moduleBuilder.GetMethodToken(method);
            this.EnsureCapacity<OpCode>(sizeof(byte) + sizeof(int) + (sizeof(int) * 3));
            this.InternalEmit(opcode);
            
            if (method.Owner.FullName == this._methodBuilder.Owner.FullName)
                this.PutByte((byte)CallContext.SELF_CALL);
            else if (method.IsExtern)
                this.PutByte((byte)CallContext.INTERNAL_CALL);
            else
                this.PutByte((byte)CallContext.OUTER_CALL);
            
            this.PutInteger4(tokenIdx);
            this.PutTypeName(ownerIdx);
            _debugBuilder.AppendLine($".{opcode.Name} {method}");
        }
        
        public enum FieldDirection
        {
            Arg,
            Local,
            Member
        }

        private int[] _labels;
        private int _labels_count;
        
        public virtual Label DefineLabel()
        {
            _labels ??= new int[4];
            if (_labels_count >= _labels.Length)
                RepackArray(_labels);
            _labels[_labels_count] = -1;
            return new Label(_labels_count++);
        }
        
        public virtual Label[] DefineLabel(int size)
            => Enumerable.Range(0, size).Select(_ => DefineLabel()).ToArray();
        
        public virtual void UseLabel(Label loc)
        {
            if (_labels is null || loc.Value < 0 || loc.Value >= _labels.Length)
                throw new InvalidLabelException();
            if (_labels[loc.Value] != -1)
                throw new UndefinedLabelException();
            _labels[loc.Value] = _position;
            _debugBuilder.AppendLine($"::label {loc.Value:X8}@{_position:X8}");
        }

        public int[] GetLabels() => _labels;


        private (ulong, FieldDirection) FindFieldToken(FieldName field)
        {
            var token = (ulong?)0;
            if ((token = this._methodBuilder.FindArgumentField(field)) != null)
                return (token.Value, FieldDirection.Arg);
            if ((token = this._methodBuilder.FindLocalField(field)) != null)
                return (token.Value, FieldDirection.Local);
            if ((token = this._methodBuilder.classBuilder.FindMemberField(field)) != null)
                return (token.Value, FieldDirection.Member);
            throw new FieldIsNotDeclaredException(field);
        }

        internal byte[] BakeByteArray()
        {
            if (_position == 0)
                return new byte[0];
            using var mem = new MemoryStream();
            using var bin = new BinaryWriter(mem);
            
            bin.Write(_ilBody);
            bin.Write((ushort)0xFFFF); // end frame
            bin.Write(_labels_count);
            if (_labels_count == 0) 
                return mem.ToArray();
            foreach (var i in _labels) 
                bin.Write(i);
            return mem.ToArray();
        }

        internal string BakeDebugString()
            => _position == 0 ? "" : _debugBuilder.ToString();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PutInteger4(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_ilBody.AsSpan(_position), value);
            _position += sizeof(int);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PutByte(byte value)
        {
            _ilBody[_position] = value;
            _position += sizeof(byte);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PutUInteger8(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_ilBody.AsSpan(_position), value);
            _position += sizeof(ulong);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PutInteger8(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_ilBody.AsSpan(_position), value);
            _position += sizeof(long);
        }
        
        internal void InternalEmit(OpCode opcode)
        {
            var num = opcode.Value;
            BinaryPrimitives.WriteUInt16LittleEndian(_ilBody.AsSpan(_position), num);
            _position += sizeof(ushort);
            //this.UpdateStackSize(opcode, opcode.StackChange());
        }
        internal void EnsureCapacity<_>(params int[] sizes) where _ : struct
        {
            var sum = sizes.Sum() + sizeof(ushort);
            EnsureCapacity(sum);
        }
        internal void EnsureCapacity(int size)
        {
            if (_position + size < _ilBody.Length)
                return;
            IncreaseCapacity(size);
        }
        
        private void IncreaseCapacity(int size)
        {
            var newsize = Math.Max(_ilBody.Length * 2, _position + size + 2);
            if (newsize % 2 != 0)
                newsize++;
            var numArray = new byte[newsize];
            Array.Copy(_ilBody, numArray, _ilBody.Length);
            _ilBody = numArray;
        }
        
        
        
        internal static T[] RepackArray<T>(T[] arr) => RepackArray<T>(arr, arr.Length * 2);

        internal static T[] RepackArray<T>(T[] arr, int newSize)
        {
            var objArray = new T[newSize];
            Array.Copy(arr, objArray, arr.Length);
            return objArray;
        }
    }
}