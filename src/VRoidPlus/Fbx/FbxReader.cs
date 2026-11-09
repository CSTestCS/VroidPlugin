using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace VRoidPlus.Fbx
{
    /// <summary>One node of an FBX file: a name, a list of property values and child nodes.</summary>
    internal class FbxNode
    {
        public string Name;
        public List<object> Props = new();
        public List<FbxNode> Children = new();

        public FbxNode Child(string name) => Children.FirstOrDefault(c => c.Name == name);
        public IEnumerable<FbxNode> All(string name) => Children.Where(c => c.Name == name);

        public long Long(int i) => Convert.ToInt64(Props[i]);
        public double Double(int i) => Convert.ToDouble(Props[i]);
        public string Str(int i) => Props[i] as string ?? "";
    }

    /// <summary>
    /// Reader for binary FBX (what Blender, Maya, Mixamo and most tools write).
    /// Format: https://code.blender.org/2013/08/fbx-binary-file-format-specification/
    /// </summary>
    internal static class FbxReader
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0");

        public static FbxNode Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 27 || !bytes.Take(Magic.Length).SequenceEqual(Magic))
            {
                var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 200));
                if (head.Contains("FBX") || head.TrimStart().StartsWith(";"))
                    throw new InvalidDataException("This is a text (ASCII) FBX. Please re-export it as binary FBX (the default in Blender, Maya and Mixamo).");
                throw new InvalidDataException("Not an FBX file.");
            }
            using var br = new BinaryReader(new MemoryStream(bytes));
            br.BaseStream.Position = 23;
            int version = br.ReadInt32();
            bool wide = version >= 7500;

            var root = new FbxNode { Name = "" };
            while (true)
            {
                var n = ReadNode(br, wide);
                if (n == null) break;
                root.Children.Add(n);
            }
            return root;
        }

        private static FbxNode ReadNode(BinaryReader br, bool wide)
        {
            if (br.BaseStream.Position >= br.BaseStream.Length - 13) return null;
            long end = wide ? (long)br.ReadUInt64() : br.ReadUInt32();
            long count = wide ? (long)br.ReadUInt64() : br.ReadUInt32();
            long listLen = wide ? (long)br.ReadUInt64() : br.ReadUInt32();
            int nameLen = br.ReadByte();
            if (end == 0) return null; // null record ends a list
            var node = new FbxNode { Name = Encoding.ASCII.GetString(br.ReadBytes(nameLen)) };

            long propsStart = br.BaseStream.Position;
            for (long i = 0; i < count; i++) node.Props.Add(ReadProp(br));
            br.BaseStream.Position = propsStart + listLen;

            while (br.BaseStream.Position < end)
            {
                var child = ReadNode(br, wide);
                if (child == null) break;
                node.Children.Add(child);
            }
            br.BaseStream.Position = end;
            return node;
        }

        private static object ReadProp(BinaryReader br)
        {
            char type = (char)br.ReadByte();
            switch (type)
            {
                case 'Y': return (long)br.ReadInt16();
                case 'C': return br.ReadByte() != 0;
                case 'I': return (long)br.ReadInt32();
                case 'F': return (double)br.ReadSingle();
                case 'D': return br.ReadDouble();
                case 'L': return br.ReadInt64();
                case 'S': return Encoding.UTF8.GetString(br.ReadBytes(br.ReadInt32()));
                case 'R': return br.ReadBytes(br.ReadInt32());
                case 'f': return ReadArray(br, 4, r => (double)r.ReadSingle());
                case 'd': return ReadArray(br, 8, r => r.ReadDouble());
                case 'l': return ReadArray(br, 8, r => (double)r.ReadInt64());
                case 'i': return ReadArray(br, 4, r => (double)r.ReadInt32());
                case 'b': return ReadArray(br, 1, r => (double)r.ReadByte());
                default: throw new InvalidDataException($"Unknown FBX property type '{type}'");
            }
        }

        /// <summary>Arrays are returned as double[] (FBX key times are int64 ticks; doubles hold them exactly enough).</summary>
        private static double[] ReadArray(BinaryReader br, int elemSize, Func<BinaryReader, double> read)
        {
            int length = br.ReadInt32();
            int encoding = br.ReadInt32();
            int compressed = br.ReadInt32();
            byte[] data = br.ReadBytes(compressed);
            if (encoding == 1)
            {
                // zlib: 2-byte header, deflate stream, 4-byte checksum.
                using var ms = new MemoryStream(data, 2, data.Length - 2);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                var outBuf = new byte[length * elemSize];
                int read0 = 0;
                while (read0 < outBuf.Length)
                {
                    int r = ds.Read(outBuf, read0, outBuf.Length - read0);
                    if (r <= 0) break;
                    read0 += r;
                }
                data = outBuf;
            }
            var result = new double[length];
            using var r2 = new BinaryReader(new MemoryStream(data));
            for (int i = 0; i < length; i++) result[i] = read(r2);
            return result;
        }
    }
}
