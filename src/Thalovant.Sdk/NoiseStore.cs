using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Thalovant
{
    /// <summary>
    /// Application-private persistent storage for the client Noise static key and
    /// authenticated hub pins. Implementations must create one durable 32-byte client
    /// key, preserve pins across connections, and reject conflicting replacements.
    /// </summary>
    public interface IHiveMindNoiseStore
    {
        byte[] LoadOrCreateStaticKey();
        byte[]? LoadPin(string nodeId);
        void VerifyOrPin(string nodeId, byte[] publicKey);
    }

    /// <summary>
    /// File-backed Noise state. On .NET 8 POSIX systems it enforces private directory
    /// and file permissions. Unity/netstandard2.1 callers must explicitly provide an
    /// existing app-private directory or implement <see cref="IHiveMindNoiseStore"/>
    /// using their platform's secure storage. Pins are never removed automatically.
    /// </summary>
    public sealed class HiveMindFileNoiseStore : IHiveMindNoiseStore
    {
        private static readonly object StateLock = new object();
        public string DirectoryPath { get; }
        private readonly bool _explicitDirectory;
        public HiveMindFileNoiseStore(string? directory = null)
        {
            _explicitDirectory = directory != null;
            DirectoryPath = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thalovant", "noise"));
        }
        private void Prepare()
        {
#if NET8_0_OR_GREATER
            if (!Directory.Exists(DirectoryPath)) {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
                else Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
#else
            if (!_explicitDirectory || !Directory.Exists(DirectoryPath)) throw new ThalovantConnectionException(
                "On netstandard2.1 supply an existing app-private Noise directory or a secure IHiveMindNoiseStore.");
#endif
            if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0) throw new ThalovantConnectionException("Noise state directory cannot be a symbolic link.");
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(DirectoryPath) & (UnixFileMode)63) != 0)
                throw new ThalovantConnectionException("Noise state directory must have private permissions (chmod 700).");
#endif
        }
        private string PinFile(string nodeId) => Path.Combine(DirectoryPath, "noise-pin-" + Noise.Hex(Noise.Hash(Encoding.UTF8.GetBytes(nodeId))) + ".key");
        private static byte[] ReadKey(string file)
        {
            if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new ThalovantConnectionException("Noise state must be a regular file.");
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & (UnixFileMode)63) != 0)
                throw new ThalovantConnectionException("Noise state file must have private permissions (chmod 600).");
#endif
            if (new FileInfo(file).Length != 64) throw new ThalovantConnectionException("Invalid stored Noise key length.");
            return Noise.Unhex(File.ReadAllText(file));
        }
        private static void WriteNew(string file, byte[] key)
        {
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
            var bytes = Encoding.ASCII.GetBytes(Noise.Hex(key)); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
        public byte[] LoadOrCreateStaticKey()
        {
            lock (StateLock) {
                Prepare(); var path = Path.Combine(DirectoryPath, "noise-static.key");
                if (!File.Exists(path)) { try { WriteNew(path, Noise.RandomKey()); } catch (IOException) when (File.Exists(path)) { } }
                return ReadKey(path);
            }
        }
        public byte[]? LoadPin(string nodeId)
        {
            lock (StateLock) { Prepare(); var path = PinFile(nodeId); return File.Exists(path) ? ReadKey(path) : null; }
        }
        public void VerifyOrPin(string nodeId, byte[] publicKey)
        {
            if (publicKey.Length != 32) throw new ArgumentException("Noise public key must be 32 bytes.", nameof(publicKey));
            lock (StateLock) {
                Prepare(); var path = PinFile(nodeId);
                try { WriteNew(path, publicKey); } catch (IOException) when (File.Exists(path)) { }
                if (!Noise.Equal(publicKey, ReadKey(path))) throw new CryptographicException("Noise server key changed; verify its rotation before replacing the saved pin.");
            }
        }
    }
}
