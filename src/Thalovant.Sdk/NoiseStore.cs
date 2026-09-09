using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Threading;

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
        private readonly Action<FileStream, byte[]> _writeAndFlush;
        public HiveMindFileNoiseStore(string? directory = null) : this(directory, WriteAndFlush) { }

        // Instance-local I/O seam for interrupted-write tests; production always
        // writes and flushes the complete value before publishing its path.
        internal HiveMindFileNoiseStore(string? directory, Action<FileStream, byte[]> writeAndFlush)
        {
            _writeAndFlush = writeAndFlush;
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
        private FileStream AcquireFileLock()
        {
            Prepare();
            var path = Path.Combine(DirectoryPath, ".noise.lock");
            var elapsed = Stopwatch.StartNew();
            while (true) {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new ThalovantConnectionException("Noise lock must not be a symbolic link.");
                try {
                    // FileShare.None is an OS-backed exclusive lock shared by all SDK
                    // processes. Readers hold it too; staging and publication also
                    // keep interrupted writes from becoming trusted key files.
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    try {
#if NET8_0_OR_GREATER
                        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
                        return stream;
                    } catch { stream.Dispose(); throw; }
                } catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(10); }
            }
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
        private static void WriteAndFlush(FileStream stream, byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        private void WriteNew(string file, byte[] key)
        {
            var temporary = Path.Combine(DirectoryPath, ".noise-" + Guid.NewGuid().ToString("N") + ".tmp");
            try {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
#if NET8_0_OR_GREATER
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
                    _writeAndFlush(stream, Encoding.ASCII.GetBytes(Noise.Hex(key)));
                }
                // Same-directory publication keeps the complete file on one volume.
                // The no-overwrite overload preserves a winner, including its pin.
                // Only a publication collision can be handled as an existing key;
                // staging write/flush errors must propagate to the caller.
                try { File.Move(temporary, file); }
                catch (IOException) when (File.Exists(file)) { }
            } finally {
                // A killed process may leave an untrusted .tmp file. It is never
                // read as key material, and subsequent attempts use a fresh name.
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        public byte[] LoadOrCreateStaticKey()
        {
            lock (StateLock) {
                using var fileLock = AcquireFileLock(); var path = Path.Combine(DirectoryPath, "noise-static.key");
                if (!File.Exists(path)) WriteNew(path, Noise.RandomKey());
                return ReadKey(path);
            }
        }
        public byte[]? LoadPin(string nodeId)
        {
            lock (StateLock) { using var fileLock = AcquireFileLock(); var path = PinFile(nodeId); return File.Exists(path) ? ReadKey(path) : null; }
        }
        public void VerifyOrPin(string nodeId, byte[] publicKey)
        {
            if (publicKey.Length != 32) throw new ArgumentException("Noise public key must be 32 bytes.", nameof(publicKey));
            lock (StateLock) {
                using var fileLock = AcquireFileLock(); var path = PinFile(nodeId);
                if (!File.Exists(path)) WriteNew(path, publicKey);
                if (!Noise.Equal(publicKey, ReadKey(path))) throw new CryptographicException("Noise server key changed; verify its rotation before replacing the saved pin.");
            }
        }
    }
}
