// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace NetCoreDbg
{
    public class SymbolReader
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DebugInfo
        {
            public int lineNumber;
            public int ilOffset;
            public string fileName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct LocalVarInfo
        {
            public int startOffset;
            public int endOffset;
            public string name;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MethodDebugInfo
        {
            public IntPtr points;
            public int size;
            public IntPtr locals;
            public int localsSize;

        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DbgSequencePoint
        {
            public int startLine;
            public int startColumn;
            public int endLine;
            public int endColumn;
            public int offset;
            public IntPtr document;
        }

        /// <summary>
        /// Read memory callback
        /// </summary>
        /// <returns>number of bytes read or 0 for error</returns>
        internal unsafe delegate int ReadMemoryDelegate(ulong address, byte* buffer, int count);

        private sealed class OpenedReader : IDisposable
        {
            public readonly MetadataReaderProvider Provider;
            public readonly MetadataReader Reader;

            public OpenedReader(MetadataReaderProvider provider, MetadataReader reader)
            {
                Debug.Assert(provider != null);
                Debug.Assert(reader != null);

                Provider = provider;
                Reader = reader;
            }

            public void Dispose() => Provider.Dispose();
        }

        /// <summary>
        /// Stream implementation to read debugger target memory for in-memory PDBs
        /// </summary>
        private class TargetStream : Stream
        {
            readonly ulong _address;
            readonly ReadMemoryDelegate _readMemory;

            public override long Position { get; set; }
            public override long Length { get; }
            public override bool CanSeek { get { return true; } }
            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return false; } }

            public TargetStream(ulong address, int size, ReadMemoryDelegate readMemory)
                : base()
            {
                _address = address;
                _readMemory = readMemory;
                Length = size;
                Position = 0;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position + count > Length)
                {
                    throw new ArgumentOutOfRangeException();
                }
                unsafe
                {
                    fixed (byte* p = &buffer[offset])
                    {
                        int read  = _readMemory(_address + (ulong)Position, p, count);
                        Position += read;
                        return read;
                    }
                }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                switch (origin)
                {
                    case SeekOrigin.Begin:
                        Position = offset;
                        break;
                    case SeekOrigin.End:
                        Position = Length + offset;
                        break;
                    case SeekOrigin.Current:
                        Position += offset;
                        break;
                }
                return Position;
            }

            public override void Flush()
            {
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Quick fix for Path.GetFileName which incorrectly handles Windows-style paths on Linux
        /// </summary>
        /// <param name="pathName"> File path to be processed </param>
        /// <returns>Last component of path</returns>
        private static string GetFileName(string pathName)
        {
            int pos = pathName.LastIndexOfAny(new char[] { '/', '\\'});
            if (pos < 0)
                return pathName;
            return pathName.Substring(pos + 1);
        }

        /// <summary>
        /// Checks availability of debugging information for given assembly.
        /// </summary>
        /// <param name="assemblyPath">
        /// File path of the assembly or null if the module is in-memory or dynamic (generated by Reflection.Emit)
        /// </param>
        /// <param name="isFileLayout">type of in-memory PE layout, if true, file based layout otherwise, loaded layout</param>
        /// <param name="loadedPeAddress">
        /// Loaded PE image address or zero if the module is dynamic (generated by Reflection.Emit). 
        /// Dynamic modules have their PDBs (if any) generated to an in-memory stream 
        /// (pointed to by <paramref name="inMemoryPdbAddress"/> and <paramref name="inMemoryPdbSize"/>).
        /// </param>
        /// <param name="loadedPeSize">loaded PE image size</param>
        /// <param name="inMemoryPdbAddress">in memory PDB address or zero</param>
        /// <param name="inMemoryPdbSize">in memory PDB size</param>
        /// <param name="readMemory">read memory callback</param>
        /// <returns>Symbol reader handle or zero if error</returns>
        internal static IntPtr LoadSymbolsForModule([MarshalAs(UnmanagedType.LPWStr)] string assemblyPath, bool isFileLayout, ulong loadedPeAddress, int loadedPeSize, 
            ulong inMemoryPdbAddress, int inMemoryPdbSize, ReadMemoryDelegate readMemory)
        {
            try
            {
                TargetStream peStream = null;
                if (assemblyPath == null && loadedPeAddress != 0)
                {
                    peStream = new TargetStream(loadedPeAddress, loadedPeSize, readMemory);
                }
                TargetStream pdbStream = null;
                if (inMemoryPdbAddress != 0)
                {
                    pdbStream = new TargetStream(inMemoryPdbAddress, inMemoryPdbSize, readMemory);
                }
                OpenedReader openedReader = GetReader(assemblyPath, isFileLayout, peStream, pdbStream);
                if (openedReader != null)
                {
                    GCHandle gch = GCHandle.Alloc(openedReader);
                    return GCHandle.ToIntPtr(gch);
                }
            }
            catch
            {
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Cleanup and dispose of symbol reader handle
        /// </summary>
        /// <param name="symbolReaderHandle">symbol reader handle returned by LoadSymbolsForModule</param>
        internal static void Dispose(IntPtr symbolReaderHandle)
        {
            Debug.Assert(symbolReaderHandle != IntPtr.Zero);
            try
            {
                GCHandle gch = GCHandle.FromIntPtr(symbolReaderHandle);
                ((OpenedReader)gch.Target).Dispose();
                gch.Free();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Returns method token and IL offset for given source line number.
        /// </summary>
        /// <param name="symbolReaderHandle">symbol reader handle returned by LoadSymbolsForModule</param>
        /// <param name="filePath">source file name and path</param>
        /// <param name="lineNumber">source line number</param>
        /// <param name="methodToken">method token return</param>
        /// <param name="ilOffset">IL offset return</param>
        /// <returns> true if information is available</returns>
        internal static bool ResolveSequencePoint(IntPtr symbolReaderHandle, [MarshalAs(UnmanagedType.LPWStr)] string filePath, int lineNumber, out int methodToken, out int ilOffset)
        {
            Debug.Assert(symbolReaderHandle != IntPtr.Zero);
            methodToken = 0;
            ilOffset = 0;

            GCHandle gch = GCHandle.FromIntPtr(symbolReaderHandle);
            MetadataReader reader = ((OpenedReader)gch.Target).Reader;

            try
            {
                Func<string, bool> FileNameMatches;
                bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                FileNameMatches = s => s.Equals(filePath, isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

                foreach (MethodDebugInformationHandle methodDebugInformationHandle in reader.MethodDebugInformation)
                {
                    MethodDebugInformation methodDebugInfo = reader.GetMethodDebugInformation(methodDebugInformationHandle);
                    SequencePointCollection sequencePoints = methodDebugInfo.GetSequencePoints();
                    foreach (SequencePoint point in sequencePoints)
                    {
                        string sourceName = reader.GetString(reader.GetDocument(point.Document).Name);
                        if (point.StartLine == lineNumber && FileNameMatches(sourceName))
                        {
                            methodToken = MetadataTokens.GetToken(methodDebugInformationHandle.ToDefinitionHandle());
                            ilOffset = point.Offset;
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// Returns source line number and source file name for given IL offset and method token.
        /// </summary>
        /// <param name="symbolReaderHandle">symbol reader handle returned by LoadSymbolsForModule</param>
        /// <param name="methodToken">method token</param>
        /// <param name="ilOffset">IL offset</param>
        /// <param name="sequencePoint">sequence point return</param>
        /// <returns> true if information is available</returns>
        private static bool GetSequencePointByILOffset(IntPtr symbolReaderHandle, int methodToken, long ilOffset, out DbgSequencePoint sequencePoint)
        {
            Debug.Assert(symbolReaderHandle != IntPtr.Zero);
            sequencePoint.document = IntPtr.Zero;
            sequencePoint.startLine = 0;
            sequencePoint.startColumn = 0;
            sequencePoint.endLine = 0;
            sequencePoint.endColumn = 0;
            sequencePoint.offset = 0;

            try
            {
                GCHandle gch = GCHandle.FromIntPtr(symbolReaderHandle);
                MetadataReader reader = ((OpenedReader)gch.Target).Reader;

                Handle handle = MetadataTokens.Handle(methodToken);
                if (handle.Kind != HandleKind.MethodDefinition)
                    return false;

                MethodDebugInformationHandle methodDebugHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();
                if (methodDebugHandle.IsNil)
                    return false;

                MethodDebugInformation methodDebugInfo = reader.GetMethodDebugInformation(methodDebugHandle);
                SequencePointCollection sequencePoints = methodDebugInfo.GetSequencePoints();

                SequencePoint nearestPoint = sequencePoints.GetEnumerator().Current;
                bool found = false;

                foreach (SequencePoint point in sequencePoints)
                {
                    if (found && point.Offset > ilOffset)
                        break;

                    if (!point.IsHidden)
                    {
                        nearestPoint = point;
                        found = true;
                    }
                }

                if (!found || nearestPoint.StartLine == 0) {
                    return false;
                }


                var fileName = reader.GetString(reader.GetDocument(nearestPoint.Document).Name);
                sequencePoint.document = Marshal.StringToBSTR(fileName);
                sequencePoint.startLine = nearestPoint.StartLine;
                sequencePoint.startColumn = nearestPoint.StartColumn;
                sequencePoint.endLine = nearestPoint.EndLine;
                sequencePoint.endColumn = nearestPoint.EndColumn;
                sequencePoint.offset = nearestPoint.Offset;
                fileName = null;

                return true;
            }
            catch
            {
            }
            return false;
        }

        internal static bool GetSequencePoints(IntPtr symbolReaderHandle, int methodToken, out IntPtr points, out int pointsCount)
        {
            Debug.Assert(symbolReaderHandle != IntPtr.Zero);
            var list = new List<DbgSequencePoint>();
            pointsCount = 0;
            points = IntPtr.Zero;

            GCHandle gch = GCHandle.FromIntPtr(symbolReaderHandle);
            MetadataReader reader = ((OpenedReader)gch.Target).Reader;

            try
            {
                Handle handle = MetadataTokens.Handle(methodToken);
                if (handle.Kind != HandleKind.MethodDefinition)
                    return false;

                MethodDebugInformationHandle methodDebugHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();
                if (methodDebugHandle.IsNil)
                    return false;

                MethodDebugInformation methodDebugInfo = reader.GetMethodDebugInformation(methodDebugHandle);
                SequencePointCollection sequencePoints = methodDebugInfo.GetSequencePoints();

                foreach (SequencePoint p in sequencePoints)
                {
                    string fileName = reader.GetString(reader.GetDocument(p.Document).Name);
                    list.Add(new DbgSequencePoint() {
                        document =  Marshal.StringToBSTR(fileName),
                        startLine = p.StartLine,
                        endLine = p.EndLine,
                        startColumn = p.StartColumn,
                        endColumn = p.EndColumn,
                        offset = p.Offset
                    });
                }

                if (list.Count == 0)
                    return true;

                var structSize = Marshal.SizeOf<DbgSequencePoint>();
                IntPtr allPoints = Marshal.AllocCoTaskMem(list.Count * structSize);
                var currentPtr = allPoints;

                foreach (var p in list)
                {
                    Marshal.StructureToPtr(p, currentPtr, false);
                    currentPtr = (IntPtr)(currentPtr.ToInt64() + structSize);
                }

                points = allPoints;
                pointsCount = list.Count;
                return true;
            }
            catch
            {
                foreach (var p in list) {
                    Marshal.FreeBSTR(p.document);
                }
            }
            return false;
        }

        internal static bool GetStepRangesFromIP(IntPtr symbolReaderHandle, int ip, int methodToken, out uint ilStartOffset, out uint ilEndOffset)
        {
            Debug.Assert(symbolReaderHandle != IntPtr.Zero);
            ilStartOffset = 0;
            ilEndOffset = 0;

            Debug.Assert(symbolReaderHandle != IntPtr.Zero);

            GCHandle gch = GCHandle.FromIntPtr(symbolReaderHandle);
            MetadataReader reader = ((OpenedReader)gch.Target).Reader;

            try
            {
                Handle handle = MetadataTokens.Handle(methodToken);
                if (handle.Kind != HandleKind.MethodDefinition)
                    return false;

                MethodDebugInformationHandle methodDebugHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();
                if (methodDebugHandle.IsNil)
                    return false;

                MethodDebugInformation methodDebugInfo = reader.GetMethodDebugInformation(methodDebugHandle);
                SequencePointCollection sequencePoints = methodDebugInfo.GetSequencePoints();

                var list = new List<SequencePoint>();
                foreach (SequencePoint p in sequencePoints)
                    list.Add(p);

                var pointsArray = list.ToArray();

                for (int i = 1; i < pointsArray.Length; i++)
                {
                    SequencePoint p = pointsArray[i];

                    if (p.Offset > ip && p.StartLine != 0 && p.StartLine != SequencePoint.HiddenLine)
                    {
                        ilStartOffset = (uint)pointsArray[0].Offset;
                        for (int j = i - 1; j > 0; j--)
                        {
                            if (pointsArray[j].Offset <= ip)
                            {
                                ilStartOffset = (uint)pointsArray[j].Offset;
                                break;
                            }
                        }
                        ilEndOffset = (uint)p.Offset;
                        return true;
                    }
                }

                // let's handle correctly last step range from last sequence point till
                // end of the method.
                if (pointsArray.Length > 0)
                {
                    ilStartOffset = (uint)pointsArray[0].Offset;
                    for (int j = pointsArray.Length - 1; j > 0; j--)
                    {
                        if (pointsArray[j].Offset <= ip)
                        {
                            ilStartOffset = (uint)pointsArray[j].Offset;
                            break;
                        }
                    }
                    ilEndOffset = ilStartOffset; // Should set this to IL code size in calling code
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        internal static bool GetLocalVariableNameAndScope(IntPtr symbolReaderHandle, int methodToken, int localIndex, out IntPtr localVarName, out int ilStartOffset, out int ilEndOffset)
        {
            localVarName = IntPtr.Zero;
            ilStartOffset = 0;
            ilEndOffset = 0;

            string localVar = null;
            if (!GetLocalVariableAndScopeByIndex(symbolReaderHandle, methodToken, localIndex, out localVar, out ilStartOffset, out ilEndOffset))
                return false;

            localVarName = Marshal.StringToBSTR(localVar);
            localVar = null;
            return true;
        }

        internal static bool GetLocalVariableAndScopeByIndex(IntPtr symbolReaderHandle, int methodToken, int localIndex, out string localVarName, out int ilStartOffset, out int ilEndOffset)
        {
            Debug.Assert(symbolReaderHandle != IntPtr.Zero);
            localVarName = null;
            ilStartOffset = 0;
            ilEndOffset = 0;

            GCHandle gch = GCHandle.FromIntPtr(symbolReaderHandle);
            MetadataReader reader = ((OpenedReader)gch.Target).Reader;

            try
            {
                Handle handle = MetadataTokens.Handle(methodToken);
                if (handle.Kind != HandleKind.MethodDefinition)
                    return false;

                MethodDebugInformationHandle methodDebugHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();
                LocalScopeHandleCollection localScopes = reader.GetLocalScopes(methodDebugHandle);
                foreach (LocalScopeHandle scopeHandle in localScopes)
                {
                    LocalScope scope = reader.GetLocalScope(scopeHandle);
                    LocalVariableHandleCollection localVars = scope.GetLocalVariables();
                    foreach (LocalVariableHandle varHandle in localVars)
                    {
                        LocalVariable localVar = reader.GetLocalVariable(varHandle);
                        if (localVar.Index == localIndex)
                        {
                            if (localVar.Attributes == LocalVariableAttributes.DebuggerHidden)
                                return false;

                            localVarName = reader.GetString(localVar.Name);
                            ilStartOffset = scope.StartOffset;
                            ilEndOffset = scope.EndOffset;
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// Returns local variable name for given local index and IL offset.
        /// </summary>
        /// <param name="symbolReaderHandle">symbol reader handle returned by LoadSymbolsForModule</param>
        /// <param name="methodToken">method token</param>
        /// <param name="localIndex">local variable index</param>
        /// <param name="localVarName">local variable name return</param>
        /// <returns>true if name has been found</returns>
        internal static bool GetLocalVariableName(IntPtr symbolReaderHandle, int methodToken, int localIndex, out IntPtr localVarName)
        {
            localVarName = IntPtr.Zero;

            string localVar = null;
            if (!GetLocalVariableByIndex(symbolReaderHandle, methodToken, localIndex, out localVar))
                return false;

            localVarName = Marshal.StringToBSTR(localVar);
            localVar = null;
            return true;
        }

        /// <summary>
        /// Helper method to return local variable name for given local index and IL offset.
        /// </summary>
        /// <param name="symbolReaderHandle">symbol reader handle returned by LoadSymbolsForModule</param>
        /// <param name="methodToken">method token</param>
        /// <param name="localIndex">local variable index</param>
        /// <param name="localVarName">local variable name return</param>
        /// <returns>true if name has been found</returns>
        internal static bool GetLocalVariableByIndex(IntPtr symbolReaderHandle, int methodToken, int localIndex, out string localVarName)
        {
            Debug.Assert(symbolReaderHandle != IntPtr.Zero);
            localVarName = null;

            GCHandle gch = GCHandle.FromIntPtr(symbolReaderHandle);
            MetadataReader reader = ((OpenedReader)gch.Target).Reader;

            try
            {
                Handle handle = MetadataTokens.Handle(methodToken);
                if (handle.Kind != HandleKind.MethodDefinition)
                    return false;

                MethodDebugInformationHandle methodDebugHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();
                LocalScopeHandleCollection localScopes = reader.GetLocalScopes(methodDebugHandle);
                foreach (LocalScopeHandle scopeHandle in localScopes)
                {
                    LocalScope scope = reader.GetLocalScope(scopeHandle);
                    LocalVariableHandleCollection localVars = scope.GetLocalVariables();
                    foreach (LocalVariableHandle varHandle in localVars)
                    {
                        LocalVariable localVar = reader.GetLocalVariable(varHandle);
                        if (localVar.Index == localIndex)
                        {
                            if (localVar.Attributes == LocalVariableAttributes.DebuggerHidden)
                                return false;

                            localVarName = reader.GetString(localVar.Name);
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// Returns the portable PDB reader for the assembly path
        /// </summary>
        /// <param name="assemblyPath">file path of the assembly or null if the module is in-memory or dynamic</param>
        /// <param name="isFileLayout">type of in-memory PE layout, if true, file based layout otherwise, loaded layout</param>
        /// <param name="peStream">optional in-memory PE stream</param>
        /// <param name="pdbStream">optional in-memory PDB stream</param>
        /// <returns>reader/provider wrapper instance</returns>
        /// <remarks>
        /// Assumes that neither PE image nor PDB loaded into memory can be unloaded or moved around.
        /// </remarks>
        private static OpenedReader GetReader(string assemblyPath, bool isFileLayout, Stream peStream, Stream pdbStream)
        {
            return (pdbStream != null) ? TryOpenReaderForInMemoryPdb(pdbStream) : TryOpenReaderFromAssembly(assemblyPath, isFileLayout, peStream);
        }

        private static OpenedReader TryOpenReaderForInMemoryPdb(Stream pdbStream)
        {
            Debug.Assert(pdbStream != null);

            byte[] buffer = new byte[sizeof(uint)];
            if (pdbStream.Read(buffer, 0, sizeof(uint)) != sizeof(uint))
            {
                return null;
            }
            uint signature = BitConverter.ToUInt32(buffer, 0);

            // quick check to avoid throwing exceptions below in common cases:
            const uint ManagedMetadataSignature = 0x424A5342;
            if (signature != ManagedMetadataSignature)
            {
                // not a Portable PDB
                return null;
            }

            OpenedReader result = null;
            MetadataReaderProvider provider = null;
            try
            {
                pdbStream.Position = 0;
                provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                result = new OpenedReader(provider, provider.GetMetadataReader());
            }
            catch (Exception e) when (e is BadImageFormatException || e is IOException)
            {
                return null;
            }
            finally
            {
                if (result == null)
                {
                    provider?.Dispose();
                }
            }

            return result;
        }

        private static OpenedReader TryOpenReaderFromAssembly(string assemblyPath, bool isFileLayout, Stream peStream)
        {
            if (assemblyPath == null && peStream == null)
                return null;

            PEStreamOptions options = isFileLayout ? PEStreamOptions.Default : PEStreamOptions.IsLoadedImage;
            if (peStream == null)
            {
                peStream = TryOpenFile(assemblyPath);
                if (peStream == null)
                    return null;
                
                options = PEStreamOptions.Default;
            }

            try
            {
                using (var peReader = new PEReader(peStream, options))
                {
                    DebugDirectoryEntry codeViewEntry, embeddedPdbEntry;
                    ReadPortableDebugTableEntries(peReader, out codeViewEntry, out embeddedPdbEntry);

                    // First try .pdb file specified in CodeView data (we prefer .pdb file on disk over embedded PDB
                    // since embedded PDB needs decompression which is less efficient than memory-mapping the file).
                    if (codeViewEntry.DataSize != 0)
                    {
                        var result = TryOpenReaderFromCodeView(peReader, codeViewEntry, assemblyPath);
                        if (result != null)
                        {
                            return result;
                        }
                    }

                    // if it failed try Embedded Portable PDB (if available):
                    if (embeddedPdbEntry.DataSize != 0)
                    {
                        return TryOpenReaderFromEmbeddedPdb(peReader, embeddedPdbEntry);
                    }
                }
            }
            catch (Exception e) when (e is BadImageFormatException || e is IOException)
            {
                // nop
            }

            return null;
        }

        private static void ReadPortableDebugTableEntries(PEReader peReader, out DebugDirectoryEntry codeViewEntry, out DebugDirectoryEntry embeddedPdbEntry)
        {
            // See spec: https://github.com/dotnet/corefx/blob/master/src/System.Reflection.Metadata/specs/PE-COFF.md

            codeViewEntry = default(DebugDirectoryEntry);
            embeddedPdbEntry = default(DebugDirectoryEntry);

            foreach (DebugDirectoryEntry entry in peReader.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.CodeView)
                {
                    const ushort PortableCodeViewVersionMagic = 0x504d;
                    if (entry.MinorVersion != PortableCodeViewVersionMagic)
                    {
                        continue;
                    }

                    codeViewEntry = entry;
                }
                else if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    embeddedPdbEntry = entry;
                }
            }
        }

        private static OpenedReader TryOpenReaderFromCodeView(PEReader peReader, DebugDirectoryEntry codeViewEntry, string assemblyPath)
        {
            OpenedReader result = null;
            MetadataReaderProvider provider = null;
            try
            {
                var data = peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);

                string pdbPath = data.Path;
                if (assemblyPath != null)
                {
                    try
                    {
                        pdbPath = Path.Combine(Path.GetDirectoryName(assemblyPath), GetFileName(pdbPath));
                    }
                    catch
                    {
                        // invalid characters in CodeView path
                        return null;
                    }
                }

                var pdbStream = TryOpenFile(pdbPath);
                if (pdbStream == null && assemblyPath != null)
                {
                    // workaround, since NI file could be generated in `.native_image` subdirectory
                    // NOTE this is temporary solution until we add option for specifying pdb path
                    try
                    {
                        int tmpLastIndex = assemblyPath.LastIndexOf(".native_image");
                        if (tmpLastIndex == -1)
                        {
                            return null;
                        }
                        string tmpPath = assemblyPath.Substring(0, tmpLastIndex);
                        pdbPath = Path.Combine(Path.GetDirectoryName(tmpPath), GetFileName(pdbPath));
                    }
                    catch
                    {
                        // invalid characters in CodeView path
                        return null;
                    }

                    pdbStream = TryOpenFile(pdbPath);
                }
                if (pdbStream == null)
                {
                    return null;
                }

                provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                var reader = provider.GetMetadataReader();

                // Validate that the PDB matches the assembly version
                if (data.Age == 1 && new BlobContentId(reader.DebugMetadataHeader.Id) == new BlobContentId(data.Guid, codeViewEntry.Stamp))
                {
                    result = new OpenedReader(provider, reader);
                }
            }
            catch (Exception e) when (e is BadImageFormatException || e is IOException)
            {
                return null;
            }
            finally
            {
                if (result == null)
                {
                    provider?.Dispose();
                }
            }

            return result;
        }

        private static OpenedReader TryOpenReaderFromEmbeddedPdb(PEReader peReader, DebugDirectoryEntry embeddedPdbEntry)
        {
            OpenedReader result = null;
            MetadataReaderProvider provider = null;

            try
            {
                // TODO: We might want to cache this provider globally (across stack traces), 
                // since decompressing embedded PDB takes some time.
                provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdbEntry);
                result = new OpenedReader(provider, provider.GetMetadataReader());
            }
            catch (Exception e) when (e is BadImageFormatException || e is IOException)
            {
                return null;
            }
            finally
            {
                if (result == null)
                {
                    provider?.Dispose();
                }
            }

            return result;
        }

        private static Stream TryOpenFile(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                return File.OpenRead(path);
            }
            catch
            {
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct AsyncAwaitInfoBlock
        {
            public uint catch_handler_offset;
            public uint yield_offset;
            public uint resume_offset;
            public uint token;
        }

        /// <summary>
        /// Helper method to return all async methods stepping information.
        /// </summary>
        /// <param name="symbolReaderHandle">symbol reader handle returned by LoadSymbolsForModule</param>
        /// <param name="asyncInfo">array with all async methods stepping information</param>
        /// <param name="asyncInfoCount">entry's count in asyncInfo</param>
        internal static void GetAsyncMethodsSteppingInfo(IntPtr symbolReaderHandle, out IntPtr asyncInfo, out int asyncInfoCount)
        {
            Debug.Assert(symbolReaderHandle != IntPtr.Zero);

            asyncInfo = IntPtr.Zero;
            asyncInfoCount = 0;
            var list = new List<AsyncAwaitInfoBlock>();

            GCHandle gch = GCHandle.FromIntPtr(symbolReaderHandle);
            MetadataReader reader = ((OpenedReader)gch.Target).Reader;

            // Guid is taken from Roslyn source code:
            // https://github.com/dotnet/roslyn/blob/afd10305a37c0ffb2cfb2c2d8446154c68cfa87a/src/Dependencies/CodeAnalysis.Debugging/PortableCustomDebugInfoKinds.cs#L13
            Guid asyncMethodSteppingInformationBlob = new Guid("54FD2AC5-E925-401A-9C2A-F94F171072F8");

            foreach (MethodDebugInformationHandle methodDebugInformationHandle in reader.MethodDebugInformation)
            {
                var entityHandle = MetadataTokens.EntityHandle(MetadataTokens.GetToken(methodDebugInformationHandle.ToDefinitionHandle()));

                foreach (var cdiHandle in reader.GetCustomDebugInformation(entityHandle))
                {
                    var cdi = reader.GetCustomDebugInformation(cdiHandle);

                    if (reader.GetGuid(cdi.Kind) == asyncMethodSteppingInformationBlob)
                    {
                        // Format of this blob is taken from Roslyn source code:
                        // https://github.com/dotnet/roslyn/blob/afd10305a37c0ffb2cfb2c2d8446154c68cfa87a/src/Compilers/Core/Portable/PEWriter/MetadataWriter.PortablePdb.cs#L575

                        var blobReader = reader.GetBlobReader(cdi.Value);
                        var catchHandlerOffset = blobReader.ReadUInt32();

                        while (blobReader.Offset < blobReader.Length)
                        {
                            list.Add(new AsyncAwaitInfoBlock() {
                                catch_handler_offset = catchHandlerOffset,
                                yield_offset = blobReader.ReadUInt32(),
                                resume_offset = blobReader.ReadUInt32(),
                                // explicit conversion from int into uint here, see:
                                // https://docs.microsoft.com/en-us/dotnet/api/system.reflection.metadata.blobreader.readcompressedinteger
                                token = (uint)blobReader.ReadCompressedInteger()
                            });
                        }
                    }
                }
            }

            if (list.Count == 0)
                return;

            var structSize = Marshal.SizeOf<AsyncAwaitInfoBlock>();
            IntPtr allInfo = Marshal.AllocCoTaskMem(list.Count * structSize);
            var currentPtr = allInfo;

            foreach (var p in list)
            {
                Marshal.StructureToPtr(p, currentPtr, false);
                currentPtr = (IntPtr)(currentPtr.ToInt64() + structSize);
            }

            asyncInfo = allInfo;
            asyncInfoCount = list.Count;
        }
    }
}
