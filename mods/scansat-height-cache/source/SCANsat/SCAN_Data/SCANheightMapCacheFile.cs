using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SCANsat.SCAN_Data
{
    // Pure file codec; no Unity/KSP calls. Binary payload preserves the existing grid exactly.
    internal static class SCANheightMapCacheFile
    {
        internal const int Width = 360, Height = 180, FormatVersion = 1;
        private const int PayloadLength = Width * Height * sizeof(float);
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCNHMAP1");

        internal static float[,] Read(string path, string body, double radius, byte[] signature)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                // Reject oversized files and malicious lengths before allocating any payload.
                if (stream.Length < PayloadLength || stream.Length > PayloadLength + 2048)
                    throw new InvalidDataException("Invalid file length");
                RequireEqual(reader.ReadBytes(Magic.Length), Magic);
                if (reader.ReadInt32() != FormatVersion || reader.ReadInt32() != Width || reader.ReadInt32() != Height)
                    throw new InvalidDataException("Incompatible format or dimensions");
                int nameLength = reader.ReadInt32();
                if (nameLength < 1 || nameLength > 1024) throw new InvalidDataException("Invalid body name length");
                if (Encoding.UTF8.GetString(reader.ReadBytes(nameLength)) != body || reader.ReadDouble() != radius)
                    throw new InvalidDataException("Body identity mismatch");
                RequireEqual(reader.ReadBytes(32), signature);
                if (reader.ReadInt32() != PayloadLength || stream.Length - stream.Position != PayloadLength + 32)
                    throw new InvalidDataException("Invalid payload length");
                byte[] bytes = reader.ReadBytes(PayloadLength);
                using (var sha = SHA256.Create()) RequireEqual(reader.ReadBytes(32), sha.ComputeHash(bytes));
                var map = new float[Width, Height];
                Buffer.BlockCopy(bytes, 0, map, 0, PayloadLength);
                return map;
            }
        }

        internal static void Write(string path, string body, double radius, byte[] signature, float[,] map)
        {
            if (map == null || map.GetLength(0) != Width || map.GetLength(1) != Height || signature.Length != 32)
                throw new ArgumentException("Invalid height map or signature");
            byte[] name = Encoding.UTF8.GetBytes(body);
            if (name.Length < 1 || name.Length > 1024) throw new ArgumentException("Invalid body name");
            byte[] bytes = new byte[PayloadLength];
            Buffer.BlockCopy(map, 0, bytes, 0, bytes.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion); writer.Write(Width); writer.Write(Height);
                    writer.Write(name.Length); writer.Write(name); writer.Write(radius);
                    writer.Write(signature); writer.Write(bytes.Length); writer.Write(bytes);
                    using (var sha = SHA256.Create()) writer.Write(sha.ComputeHash(bytes));
                    writer.Flush(); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void RequireEqual(byte[] actual, byte[] expected)
        {
            if (actual.Length != expected.Length) throw new InvalidDataException("Truncated file");
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i]) throw new InvalidDataException("Signature or checksum mismatch");
        }
    }
}
