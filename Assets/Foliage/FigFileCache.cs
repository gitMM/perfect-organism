using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Foliage
{
    internal static class FigFileCache
    {
        // Bump when generation or the binary layout of any stored struct changes.
        const int Version = 1;

        internal static Mesh LoadOrGenerate(int seed, FigSettings settings, List<FigShoot> shoots)
        {
            string key = $"{seed}_{BitConverter.SingleToInt32Bits(settings.size):x8}_{BitConverter.SingleToInt32Bits(settings.crownSpread):x8}_" +
                $"{BitConverter.SingleToInt32Bits(settings.buttressReach):x8}_{BitConverter.SingleToInt32Bits(settings.fusion):x8}_" +
                $"{settings.canopyDensity}_{BitConverter.SingleToInt32Bits(settings.propFrequency):x8}_{(shoots == null ? "wood" : "crown")}";
            string path = Path.Combine(Application.persistentDataPath, "JungleFigCache", $"v{Version}_{key}.bin");
            try
            {
                if (File.Exists(path)) return Load(path, seed, shoots);
            }
            catch (Exception e) when (CacheError(e))
            {
                Debug.LogWarning($"Regenerating unreadable fig cache {path}. {e.Message}");
            }

            int first = shoots?.Count ?? 0;
            Mesh mesh = JungleFig.GenerateFresh(seed, settings, shoots);
            try
            {
                Save(path, mesh, shoots, first);
            }
            catch (Exception e) when (CacheError(e))
            {
                Debug.LogWarning($"Could not save fig cache {path}. {e.Message}");
            }
            return mesh;
        }

        static bool CacheError(Exception e) => e is IOException || e is UnauthorizedAccessException ||
            e is InvalidDataException || e is NotSupportedException;

        static void Save(string path, Mesh mesh, List<FigShoot> shoots, int first)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Open(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var w = new BinaryWriter(stream))
                {
                    w.Write(new byte[32]);
                    Write(w, mesh.vertices);
                    Write(w, mesh.normals);
                    var grain = new List<Vector3>();
                    var directions = new List<Vector4>();
                    var age = new List<Vector4>();
                    mesh.GetUVs(0, grain);
                    mesh.GetUVs(1, directions);
                    mesh.GetUVs(2, age);
                    Write(w, grain.ToArray());
                    Write(w, directions.ToArray());
                    Write(w, age.ToArray());
                    Write(w, mesh.triangles);
                    w.Write((shoots?.Count ?? 0) - first);
                    for (int i = first; i < (shoots?.Count ?? 0); i++) shoots[i].WriteCache(w);
                    stream.Position = 32;
                    byte[] digest = Checksum(stream);
                    stream.Position = 0;
                    w.Write(digest);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { File.Delete(temp); }
        }

        static Mesh Load(string path, int seed, List<FigShoot> shoots)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var r = new BinaryReader(stream);
            byte[] digest = r.ReadBytes(32);
            if (!Checksum(stream).SequenceEqual(digest)) throw new InvalidDataException("Checksum mismatch.");
            stream.Position = 32;
            var positions = Read<Vector3>(r);
            var normals = Read<Vector3>(r);
            var grain = Read<Vector3>(r);
            var directions = Read<Vector4>(r);
            var age = Read<Vector4>(r);
            var triangles = Read<int>(r);
            int count = Count(r);
            var loaded = new List<FigShoot>();
            for (int i = 0; i < count; i++) loaded.Add(new FigShoot(r));
            if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected trailing data.");
            var mesh = new Mesh { name = "Jungle fig " + seed, indexFormat = IndexFormat.UInt32 };
            mesh.vertices = positions;
            mesh.normals = normals;
            mesh.SetUVs(0, grain);
            mesh.SetUVs(1, directions);
            mesh.SetUVs(2, age);
            mesh.triangles = triangles;
            shoots?.AddRange(loaded);
            return mesh;
        }

        static byte[] Checksum(Stream stream)
        {
            var hash = new Hash128();
            var buffer = new byte[256 * 1024];
            while (stream.Position < stream.Length)
            {
                int count = (int)Math.Min(buffer.Length, stream.Length - stream.Position);
                int offset = 0;
                while (offset < count)
                {
                    int read = stream.Read(buffer, offset, count - offset);
                    if (read == 0) throw new EndOfStreamException();
                    offset += read;
                }
                hash.Append(buffer, 0, count);
            }
            return Encoding.ASCII.GetBytes(hash.ToString());
        }

        internal static int Count(BinaryReader r)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Invalid fig cache count.");
            return count;
        }

        internal static void Write<T>(BinaryWriter w, T[] items) where T : unmanaged
        {
            w.Write(items.Length);
            w.BaseStream.Write(MemoryMarshal.AsBytes(items.AsSpan()));
        }

        internal static T[] Read<T>(BinaryReader r) where T : unmanaged
        {
            int count = Count(r);
            if ((long)count * Marshal.SizeOf<T>() > r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("Incomplete fig cache array.");
            var items = new T[count];
            var bytes = MemoryMarshal.AsBytes(items.AsSpan());
            while (!bytes.IsEmpty)
            {
                int length = r.BaseStream.Read(bytes);
                if (length == 0) throw new EndOfStreamException();
                bytes = bytes.Slice(length);
            }
            return items;
        }
    }
}
