﻿//  Copyright 2018 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

// NOTE: This file is a modified version of NdrParser.cs from OleViewDotNet
// https://github.com/tyranid/oleviewdotnet. It's been relicensed from GPLv3 by
// the original author James Forshaw to be used under the Apache License for this

using NtApiDotNet.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace NtApiDotNet.Ndr
{
#pragma warning disable 1591
    [Flags]
    public enum NdrParamAttributes : ushort
    {
        MustSize = 0x0001,
        MustFree = 0x0002,
        IsPipe = 0x0004,
        IsIn = 0x0008,
        IsOut = 0x0010,
        IsReturn = 0x0020,
        IsBasetype = 0x0040,
        IsByValue = 0x0080,
        IsSimpleRef = 0x0100,
        IsDontCallFreeInst = 0x0200,
        SaveForAsyncFinish = 0x0400,
        ServerAllocSizeMask = 0xe000
    }

    [Flags]
    internal enum NdrHandleParamFlags : byte
    {
        HANDLE_PARAM_IS_VIA_PTR = 0x80,
        HANDLE_PARAM_IS_IN      = 0x40,
        HANDLE_PARAM_IS_OUT     = 0x20,
        HANDLE_PARAM_IS_RETURN  = 0x10,
                /* flags for context handles */
        NDR_STRICT_CONTEXT_HANDLE           = 0x08,
        NDR_CONTEXT_HANDLE_NOSERIALIZE      = 0x04,
        NDR_CONTEXT_HANDLE_SERIALIZE        = 0x02,
        NDR_CONTEXT_HANDLE_CANNOT_BE_NULL   = 0x01,
    }

    public class NdrProcedureParameter
    {
        public NdrParamAttributes Attributes { get; }
        public NdrBaseTypeReference Type { get; }
        public int Offset { get; }

        internal NdrProcedureParameter(NdrParamAttributes attributes, NdrBaseTypeReference type, int offset)
        {
            Attributes = attributes;
            Type = type;
            Offset = offset;
        }

        internal NdrProcedureParameter(NdrParseContext context, BinaryReader reader)
        {
            Attributes = (NdrParamAttributes)reader.ReadUInt16();
            Offset = reader.ReadUInt16();
            if ((Attributes & NdrParamAttributes.IsBasetype) == 0)
            {
                int type_ofs = reader.ReadUInt16();
                Type = NdrBaseTypeReference.Read(context, type_ofs);
            }
            else
            {
                Type = new NdrBaseTypeReference((NdrFormatCharacter)reader.ReadByte());
                // Remove padding.
                reader.ReadByte();
            }
        }

        internal string Format(NdrFormatter context)
        {
            List<string> attributes = new List<string>();
            if ((Attributes & NdrParamAttributes.IsIn) != 0)
            {
                attributes.Add("In");
            }
            if ((Attributes & NdrParamAttributes.IsOut) != 0)
            {
                attributes.Add("Out");
            }
            if ((Attributes & NdrParamAttributes.IsReturn) != 0)
            {
                attributes.Add("RetVal");
            }

            string type_format = (Attributes & NdrParamAttributes.IsSimpleRef) == 0
                ? Type.FormatType(context) : String.Format("{0}*", Type.FormatType(context));

            if (attributes.Count > 0)
            {
                return String.Format("[{0}] {1}", string.Join(", ", attributes), type_format);
            }
            else
            {
                return type_format;
            }
        }

        public override string ToString()
        {
            return String.Format("{0} - {1}", Type, Attributes);
        }
    }

    public class NdrProcedureHandleParameter : NdrProcedureParameter
    {
        NdrHandleParamFlags Flags { get; }
        public bool Explicit { get; }

        internal NdrProcedureHandleParameter(NdrParamAttributes attributes, 
            NdrBaseTypeReference type, int offset, bool explicit_handle, NdrHandleParamFlags flags) 
            : base(attributes, type, offset)
        {
            Flags = flags;
            Explicit = explicit_handle;
        }
    }

    public class NdrProcedureDefinition
    {
        public string Name { get; }
        public IList<NdrProcedureParameter> Params { get; }
        public NdrProcedureParameter ReturnValue { get; }
        public NdrProcedureHandleParameter Handle { get; }
        public uint RpcFlags { get; }
        public int ProcNum { get; }
        public int StackSize { get; }
        public bool HasAsyncHandle { get; }
        public IntPtr DispatchFunction { get; }

        internal string FormatProcedure(NdrFormatter context)
        {
            string return_value;

            if (ReturnValue == null)
            {
                return_value = "void";
            }
            else if (ReturnValue.Type.Format == NdrFormatCharacter.FC_LONG)
            {
                return_value = "HRESULT";
            }
            else
            {
                return_value = ReturnValue.Type.FormatType(context);
            }

            return String.Format("{0} {1}({2});", return_value,
                Name, string.Join(", ", Params.Select((p, i) => String.Format("/* Stack Offset: {0} */ {1} p{2}", p.Offset, p.Format(context), i))));
        }

        internal NdrProcedureDefinition(NdrTypeCache type_cache, ISymbolResolver symbol_resolver, MIDL_STUB_DESC stub_desc, IntPtr proc_desc, IntPtr type_desc, IntPtr dispatch_func)
        {
            UnmanagedMemoryStream stm = new UnmanagedMemoryStream(new SafeBufferWrapper(proc_desc), 0, int.MaxValue);
            BinaryReader reader = new BinaryReader(stm);
            NdrFormatCharacter handle_type = (NdrFormatCharacter)reader.ReadByte();
            NdrInterpreterFlags old_oi_flags = (NdrInterpreterFlags)reader.ReadByte();

            if ((old_oi_flags & NdrInterpreterFlags.HasRpcFlags) == NdrInterpreterFlags.HasRpcFlags)
            {
                RpcFlags = reader.ReadUInt32();
            }

            ProcNum = reader.ReadUInt16();
            if (symbol_resolver != null && dispatch_func != IntPtr.Zero)
            {
                Name = symbol_resolver.GetSymbolForAddress(dispatch_func, false);
            }

            Name = Name ?? $"Proc{ProcNum}";

            StackSize = reader.ReadUInt16();
            if (handle_type == 0)
            {
                // read out handle type.
                handle_type = (NdrFormatCharacter)reader.ReadByte();
                NdrHandleParamFlags flags = (NdrHandleParamFlags)reader.ReadByte();
                ushort handle_offset = reader.ReadUInt16();
                NdrBaseTypeReference base_type = new NdrBaseTypeReference(handle_type);
                if (handle_type == NdrFormatCharacter.FC_BIND_PRIMITIVE)
                {
                    flags = flags != 0 ? NdrHandleParamFlags.HANDLE_PARAM_IS_VIA_PTR : 0;
                }
                else if (handle_type == NdrFormatCharacter.FC_BIND_GENERIC)
                {
                    // Remove the size field, we might do something with this later.
                    flags = (NdrHandleParamFlags)((byte)flags & 0xF0);
                    // Read out the remaining data.
                    reader.ReadByte();
                    reader.ReadByte();
                }
                else if (handle_type == NdrFormatCharacter.FC_BIND_CONTEXT)
                {
                    // Read out the remaining data.
                    reader.ReadByte();
                    reader.ReadByte();
                }
                else
                {
                    throw new ArgumentException($"Unsupported explicit handle type {handle_type}");
                }
                Handle = new NdrProcedureHandleParameter(0, 
                        (flags & NdrHandleParamFlags.HANDLE_PARAM_IS_VIA_PTR) != 0 ? new NdrPointerTypeReference(base_type)
                            : base_type, handle_offset, true, flags);
            }
            else
            {
                Handle = new NdrProcedureHandleParameter(0, new NdrBaseTypeReference(handle_type), 0, false, 0);
            }

            ushort constant_client_buffer_size = reader.ReadUInt16();
            ushort constant_server_buffer_size = reader.ReadUInt16();
            NdrInterpreterOptFlags oi2_flags = (NdrInterpreterOptFlags)reader.ReadByte();
            int number_of_params = reader.ReadByte();
            HasAsyncHandle = (oi2_flags & NdrInterpreterOptFlags.HasAsyncHandle) != 0;

            NdrProcHeaderExts exts = new NdrProcHeaderExts();
            if ((oi2_flags & NdrInterpreterOptFlags.HasExtensions) == NdrInterpreterOptFlags.HasExtensions)
            {
                int ext_size = reader.ReadByte();
                stm.Position -= 1;
                // Read out extension bytes.
                byte[] extension = reader.ReadAll(ext_size);
                if (Marshal.SizeOf(typeof(NdrProcHeaderExts)) <= ext_size)
                {
                    using (var buffer = new SafeStructureInOutBuffer<NdrProcHeaderExts>(ext_size, false))
                    {
                        buffer.WriteArray(0, extension, 0, ext_size);
                        exts = buffer.Result;
                    }
                }
            }

            int desc_size = 4;
            if ((exts.Flags2 & NdrInterpreterOptFlags2.HasNewCorrDesc) != 0)
            {
                desc_size = 6;
                if ((exts.Flags2 & NdrInterpreterOptFlags2.ExtendedCorrDesc) != 0)
                {
                    desc_size = 16;
                }
            }
            NdrParseContext context = new NdrParseContext(type_cache, symbol_resolver, stub_desc, type_desc, desc_size);
            List<NdrProcedureParameter> ps = new List<NdrProcedureParameter>();

            bool has_return = (oi2_flags & NdrInterpreterOptFlags.HasReturn) == NdrInterpreterOptFlags.HasReturn;
            int param_count = has_return ? number_of_params - 1 : number_of_params;
            for (int param = 0; param < param_count; ++param)
            {
                ps.Add(new NdrProcedureParameter(context, reader));
            }

            if (Handle.Explicit)
            {
                // Insert handle into parameter list at the best location.
                int index = 0;
                while (index < ps.Count)
                {
                    if (ps[index].Offset > Handle.Offset)
                    {
                        ps.Insert(index, Handle);
                        break;
                    }
                    index++;
                }
            }

            Params = ps.AsReadOnly();
            if (has_return)
            {
                ReturnValue = new NdrProcedureParameter(context, reader);
            }
            DispatchFunction = dispatch_func;
        }
    }
}

#pragma warning restore 1591