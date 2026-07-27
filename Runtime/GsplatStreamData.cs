// Copyright (c) 2026
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using Unity.Profiling;

namespace Gsplat
{
    public enum GsplatRuntimeStorage
    {
        EmbeddedManaged,
        StreamedPlayerData,
    }

    public enum GsplatStreamSection
    {
        PackedSplats,
        PackedSH1,
        PackedSH2,
        PackedSH3,
        Positions,
        Scales,
        Rotations,
        Colors,
        SH,
        PvgTime,
        PvgVelocity,
        Count,
    }

    public static class GsplatStreamData
    {
        const uint k_magic = 0x42505347; // GSPB
        const int k_version = 1;
        const ulong k_fnvOffset = 14695981039346656037UL;
        const ulong k_fnvPrime = 1099511628211UL;
        const string k_directoryName = "Gsplat";
        const string k_extension = ".gsplatbin";
        const int k_copyBufferSize = 1024 * 1024;
        static readonly ProfilerMarker k_missingBlobMarker = new("Gsplat.MissingStreamBlob");

        public struct Section
        {
            public long Offset;
            public long ByteLength;
            public int ElementCount;
            public int Stride;
            public ulong Hash;
        }

        public sealed class Reader : IDisposable
        {
            readonly FileStream m_stream;
            readonly Section[] m_sections;
            readonly ulong[] m_hashes;
            readonly long[] m_bytesRead;
            byte[] m_copyBuffer = Array.Empty<byte>();

            public CompressionMode Compression { get; }
            public uint SplatCount { get; }
            public byte SHBands { get; }
            public bool IsPvgDynamic { get; }

            public Reader(GsplatAsset asset)
            {
                string path = GetRuntimePath(asset.StreamDataId);
                try
                {
                    m_stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        k_copyBufferSize, FileOptions.None);
                }
                catch (FileNotFoundException)
                {
                    using (k_missingBlobMarker.Auto())
                    {
                        throw;
                    }
                }
                try
                {
                    using var reader = new BinaryReader(m_stream, System.Text.Encoding.UTF8, true);
                    if (reader.ReadUInt32() != k_magic)
                        throw new InvalidDataException($"Invalid gsplat stream magic in '{path}'.");
                    int version = reader.ReadInt32();
                    if (version != k_version)
                        throw new InvalidDataException(
                            $"Unsupported gsplat stream version {version} in '{path}' (expected {k_version}).");

                    Compression = (CompressionMode)reader.ReadInt32();
                    SplatCount = reader.ReadUInt32();
                    SHBands = reader.ReadByte();
                    IsPvgDynamic = reader.ReadBoolean();
                    reader.ReadUInt16();
                    int sectionCount = reader.ReadInt32();
                    if (sectionCount != (int)GsplatStreamSection.Count)
                        throw new InvalidDataException($"Unexpected gsplat stream section count {sectionCount}.");

                    m_sections = new Section[sectionCount];
                    m_hashes = new ulong[sectionCount];
                    m_bytesRead = new long[sectionCount];
                    for (int i = 0; i < sectionCount; ++i)
                    {
                        int sectionId = reader.ReadInt32();
                        if (sectionId < 0 || sectionId >= sectionCount)
                            throw new InvalidDataException($"Invalid gsplat stream section id {sectionId}.");
                        m_sections[sectionId] = new Section
                        {
                            Offset = reader.ReadInt64(),
                            ByteLength = reader.ReadInt64(),
                            ElementCount = reader.ReadInt32(),
                            Stride = reader.ReadInt32(),
                            Hash = reader.ReadUInt64(),
                        };
                        m_hashes[sectionId] = k_fnvOffset;
                    }

                    if (Compression != asset.Compression || SplatCount != asset.SplatCount ||
                        SHBands != asset.SHBands || IsPvgDynamic != asset.IsPvgDynamic)
                        throw new InvalidDataException(
                            $"Gsplat stream metadata does not match asset '{asset.name}'. Reimport the source PLY.");
                }
                catch
                {
                    m_stream.Dispose();
                    throw;
                }
            }

            public void Read<T>(GsplatStreamSection sectionId, T[] destination, int sourceElement,
                int elementCount) where T : struct
            {
                int id = (int)sectionId;
                var section = m_sections[id];
                int stride = Marshal.SizeOf(typeof(T));
                if (section.Stride != stride)
                    throw new InvalidDataException(
                        $"Section {sectionId} stride is {section.Stride}, expected {stride}.");
                if (sourceElement < 0 || elementCount < 0 ||
                    sourceElement + elementCount > section.ElementCount ||
                    elementCount > destination.Length)
                    throw new ArgumentOutOfRangeException(nameof(elementCount));

                int byteCount = checked(elementCount * stride);
                EnsureCopyBuffer(byteCount);
                m_stream.Position = section.Offset + (long)sourceElement * stride;
                ReadExactly(m_copyBuffer, byteCount);

                var handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
                try
                {
                    Marshal.Copy(m_copyBuffer, 0, handle.AddrOfPinnedObject(), byteCount);
                }
                finally
                {
                    handle.Free();
                }

                m_hashes[id] = UpdateHash(m_hashes[id], m_copyBuffer, byteCount);
                m_bytesRead[id] += byteCount;
            }

            public void Validate()
            {
                for (int i = 0; i < m_sections.Length; ++i)
                {
                    var section = m_sections[i];
                    if (section.ByteLength == 0)
                        continue;
                    if (m_bytesRead[i] != section.ByteLength || m_hashes[i] != section.Hash)
                        throw new InvalidDataException(
                            $"Gsplat stream section {(GsplatStreamSection)i} failed validation.");
                }
            }

            void EnsureCopyBuffer(int byteCount)
            {
                if (m_copyBuffer.Length < byteCount)
                    m_copyBuffer = new byte[byteCount];
            }

            void ReadExactly(byte[] destination, int byteCount)
            {
                int offset = 0;
                while (offset < byteCount)
                {
                    int read = m_stream.Read(destination, offset, byteCount - offset);
                    if (read == 0)
                        throw new EndOfStreamException("Unexpected end of gsplat stream data.");
                    offset += read;
                }
            }

            public void Dispose()
            {
                m_stream.Dispose();
            }
        }

        public static string GetEditorCachePath(string dataId)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "Library", "GsplatStreamData",
                dataId + k_extension);
        }

        public static string GetRuntimePath(string dataId)
        {
#if UNITY_EDITOR
            return GetEditorCachePath(dataId);
#else
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.LinuxPlayer &&
                Application.platform != RuntimePlatform.OSXPlayer)
                throw new PlatformNotSupportedException(
                    "Streamed gsplat player data currently supports desktop filesystem players only.");
            return Path.Combine(Application.streamingAssetsPath, k_directoryName, dataId + k_extension);
#endif
        }

        public static string GetBuildRelativePath(string dataId)
        {
            return k_directoryName + "/" + dataId + k_extension;
        }

        public static void Write(GsplatAsset asset, string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var sections = new Section[(int)GsplatStreamSection.Count];
            int headerSize = HeaderSize(sections.Length);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                k_copyBufferSize);
            stream.Position = headerSize;

            if (asset is GsplatAssetSpark spark)
            {
                WriteArray(stream, sections, GsplatStreamSection.PackedSplats, spark.PackedSplats);
                WriteArray(stream, sections, GsplatStreamSection.PackedSH1, spark.PackedSH1);
                WriteArray(stream, sections, GsplatStreamSection.PackedSH2, spark.PackedSH2);
                WriteArray(stream, sections, GsplatStreamSection.PackedSH3, spark.PackedSH3);
            }
            else if (asset is GsplatAssetUncompressed uncompressed)
            {
                WriteArray(stream, sections, GsplatStreamSection.Positions, uncompressed.Positions);
                WriteArray(stream, sections, GsplatStreamSection.Scales, uncompressed.Scales);
                WriteArray(stream, sections, GsplatStreamSection.Rotations, uncompressed.Rotations);
                WriteArray(stream, sections, GsplatStreamSection.Colors, uncompressed.Colors);
                WriteArray(stream, sections, GsplatStreamSection.SH, uncompressed.SHs);
            }
            else
            {
                throw new NotSupportedException($"Unsupported gsplat asset type {asset.GetType().Name}.");
            }

            WriteArray(stream, sections, GsplatStreamSection.PvgTime, asset.PvgTimeData);
            WriteArray(stream, sections, GsplatStreamSection.PvgVelocity, asset.PvgVelocities);

            stream.Position = 0;
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
            writer.Write(k_magic);
            writer.Write(k_version);
            writer.Write((int)asset.Compression);
            writer.Write(asset.SplatCount);
            writer.Write(asset.SHBands);
            writer.Write(asset.IsPvgDynamic);
            writer.Write((ushort)0);
            writer.Write(sections.Length);
            for (int i = 0; i < sections.Length; ++i)
            {
                var section = sections[i];
                writer.Write(i);
                writer.Write(section.Offset);
                writer.Write(section.ByteLength);
                writer.Write(section.ElementCount);
                writer.Write(section.Stride);
                writer.Write(section.Hash);
            }
        }

        static void WriteArray<T>(FileStream stream, Section[] sections, GsplatStreamSection sectionId,
            T[] data) where T : struct
        {
            int id = (int)sectionId;
            if (data == null || data.Length == 0)
            {
                sections[id] = default;
                return;
            }

            int stride = Marshal.SizeOf(typeof(T));
            long byteLength = checked((long)data.Length * stride);
            var section = new Section
            {
                Offset = stream.Position,
                ByteLength = byteLength,
                ElementCount = data.Length,
                Stride = stride,
                Hash = k_fnvOffset,
            };

            var copyBuffer = new byte[Math.Min(k_copyBufferSize, (int)Math.Min(byteLength, int.MaxValue))];
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                long copied = 0;
                while (copied < byteLength)
                {
                    int count = (int)Math.Min(copyBuffer.Length, byteLength - copied);
                    var source = new IntPtr(handle.AddrOfPinnedObject().ToInt64() + copied);
                    Marshal.Copy(source, copyBuffer, 0, count);
                    stream.Write(copyBuffer, 0, count);
                    section.Hash = UpdateHash(section.Hash, copyBuffer, count);
                    copied += count;
                }
            }
            finally
            {
                handle.Free();
            }

            sections[id] = section;
        }

        static int HeaderSize(int sectionCount)
        {
            const int fixedHeaderSize = sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(uint) +
                                        sizeof(byte) + sizeof(byte) + sizeof(ushort) + sizeof(int);
            const int sectionSize = sizeof(int) + sizeof(long) + sizeof(long) + sizeof(int) +
                                    sizeof(int) + sizeof(ulong);
            return fixedHeaderSize + sectionCount * sectionSize;
        }

        static ulong UpdateHash(ulong hash, byte[] bytes, int count)
        {
            for (int i = 0; i < count; ++i)
            {
                hash ^= bytes[i];
                hash *= k_fnvPrime;
            }
            return hash;
        }
    }
}
