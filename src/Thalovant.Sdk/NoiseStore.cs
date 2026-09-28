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
    /// <remarks>
    /// A client given no store uses one of these. On .NET 8, for an identity read
    /// from a file (<see cref="ThalovantIdentity.SourcePath"/>), its folder is
    /// <c>noise</c> beside that file, so every program that reads the same file
    /// presents the same key to the hub; the first time that folder is used, the
    /// key and hub pins this identity had in the shared default folder are copied
    /// into it (never moved), when they have met this identity's hub. Any other
    /// identity, or one whose folder cannot be made, uses the shared default,
    /// <c>LocalApplicationData/Thalovant/noise</c>, as before 0.9.1.
    /// </remarks>
    public sealed class HiveMindFileNoiseStore : IHiveMindNoiseStore
    {
        private static readonly object StateLock = new object();

        /// <summary>The folder this store keeps its key and pins in.</summary>
        public string DirectoryPath
        {
            get
            {
                lock (StateLock) return _directory;
            }
        }

        private string _directory;
        private readonly bool _explicitDirectory;
        private readonly Action<FileStream, byte[]> _writeAndFlush;

#if NET8_0_OR_GREATER
        /// <summary>For a store beside an identity file: the shared default, used when that folder cannot be made.</summary>
        private string? _fallback;
#endif

        /// <summary>For a store beside an identity file: the shared default, whose key it takes on first use.</summary>
        private string? _adoptFrom;

        public HiveMindFileNoiseStore(string? directory = null) : this(directory, WriteAndFlush) { }

        // Instance-local I/O seam for interrupted-write tests; production always
        // writes and flushes the complete value before publishing its path.
        internal HiveMindFileNoiseStore(string? directory, Action<FileStream, byte[]> writeAndFlush)
        {
            _writeAndFlush = writeAndFlush;
            _explicitDirectory = directory != null;
            _directory = Path.GetFullPath(directory ?? DefaultDirectory);
        }

        /// <summary>The shared default folder: <c>LocalApplicationData/Thalovant/noise</c>.</summary>
        internal static string DefaultDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thalovant", "noise");

        /// <summary>The name of the folder beside an identity file that holds its Noise state.</summary>
        internal const string BesideIdentityName = "noise";

        /// <summary>The <c>noise</c> folder beside the file <paramref name="identity"/> was read from, or null.</summary>
        internal static string? BesideIdentity(ThalovantIdentity? identity)
        {
            if (identity?.SourcePath is not string source || source.Length == 0) return null;
            var parent = Path.GetDirectoryName(Path.GetFullPath(source));
            return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, BesideIdentityName);
        }

        /// <summary>
        /// The store a client given none uses for <paramref name="identity"/>:
        /// beside its identity file on .NET 8, otherwise the shared default.
        /// </summary>
        internal static HiveMindFileNoiseStore ForIdentity(ThalovantIdentity identity, string? legacyDirectory = null)
        {
#if NET8_0_OR_GREATER
            if (BesideIdentity(identity) is string beside)
            {
                var legacy = Path.GetFullPath(legacyDirectory ?? DefaultDirectory);
                var store = new HiveMindFileNoiseStore(beside, WriteAndFlush);
                if (!SamePath(store._directory, legacy))
                {
                    store._fallback = legacy;
                    store._adoptFrom = legacy;
                }
                return store;
            }
#endif
            return legacyDirectory is null ? new HiveMindFileNoiseStore() : new HiveMindFileNoiseStore(legacyDirectory);
        }

        internal static bool SamePath(string left, string right)
        {
            static string Normal(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Normal(left), Normal(right), comparison);
        }

        private void Prepare()
        {
#if NET8_0_OR_GREATER
            if (!Directory.Exists(_directory))
            {
                try
                {
                    CreatePrivateDirectory(_directory);
                }
                catch (Exception error) when (_fallback != null && (error is IOException || error is UnauthorizedAccessException))
                {
                    // An identity in a folder this user cannot write to (/etc,
                    // say) keeps its key in the shared default, as before.
                    _directory = _fallback;
                    _fallback = null;
                    _adoptFrom = null;
                    if (!Directory.Exists(_directory)) CreatePrivateDirectory(_directory);
                }
            }
            _fallback = null;
#else
            if (!_explicitDirectory || !Directory.Exists(_directory)) throw new ThalovantConnectionException(
                "On netstandard2.1 supply an existing app-private Noise directory or a secure IHiveMindNoiseStore.");
#endif
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) throw new ThalovantConnectionException("Noise state directory cannot be a symbolic link.");
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(_directory) & (UnixFileMode)63) != 0)
                throw new ThalovantConnectionException("Noise state directory must have private permissions (chmod 700).");
#endif
        }
#if NET8_0_OR_GREATER
        private static void CreatePrivateDirectory(string path)
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#endif

        private FileStream AcquireFileLock()
        {
            Prepare();
            var path = Path.Combine(_directory, ".noise.lock");
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
        private const string StaticKeyName = "noise-static.key";
        private static string PinName(string nodeId) => "noise-pin-" + Noise.Hex(Noise.Hash(Encoding.UTF8.GetBytes(nodeId))) + ".key";
        private string PinFile(string nodeId) => Path.Combine(_directory, PinName(nodeId));
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
            var temporary = Path.Combine(_directory, ".noise-" + Guid.NewGuid().ToString("N") + ".tmp");
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
                using var fileLock = AcquireFileLock(); var path = Path.Combine(_directory, StaticKeyName);
                if (!File.Exists(path)) WriteNew(path, Noise.RandomKey());
                return ReadKey(path);
            }
        }
        public byte[]? LoadPin(string nodeId)
        {
            lock (StateLock)
            {
                using var fileLock = AcquireFileLock();
                AdoptLegacyKey(nodeId);
                var path = PinFile(nodeId);
                return File.Exists(path) ? ReadKey(path) : null;
            }
        }

        /// <summary>
        /// Copies this identity's key from the shared default folder, once: when
        /// this folder holds no key yet and the old one holds a key that has met
        /// the hub <paramref name="nodeId"/> names (a pin for it). The hub pins the
        /// first key a connection presents, so a device that silently got a new
        /// key in a new folder would be locked out. The key and the hub pins are
        /// copied, never moved: another program may still read the old folder.
        /// Call only while holding <see cref="StateLock"/> and this folder's lock.
        /// </summary>
        private void AdoptLegacyKey(string nodeId)
        {
            var legacy = _adoptFrom;
            if (legacy is null) return;
            if (File.Exists(Path.Combine(_directory, StaticKeyName)))
            {
                _adoptFrom = null;
                return;
            }
            try
            {
                if (!Directory.Exists(legacy) || (File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0) return;
                var legacyKey = Path.Combine(legacy, StaticKeyName);
                var legacyPin = Path.Combine(legacy, PinName(nodeId));
                // The old key never met this hub: it is not the key the hub pinned
                // for this identity, so there is nothing to keep.
                if (!File.Exists(legacyKey) || !File.Exists(legacyPin)) return;
                var key = ReadKey(legacyKey);
                // This hub's pin must come across: the old key without it would
                // let the next XX handshake pin whatever answers. When it cannot
                // be read, nothing is copied and this folder starts afresh.
                var ownName = PinName(nodeId);
                var own = ReadKey(legacyPin);
                var pins = new System.Collections.Generic.List<(string Name, byte[] Key)> { (ownName, own) };
                foreach (var pin in Directory.GetFiles(legacy, "noise-pin-*.key"))
                {
                    if (string.Equals(Path.GetFileName(pin), ownName, StringComparison.Ordinal)) continue;
                    // Another hub's pin that cannot be read is not carried over,
                    // and does not cost this hub the key it already trusts.
                    try { pins.Add((Path.GetFileName(pin), ReadKey(pin))); }
                    catch (Exception error) when (error is ThalovantConnectionException || error is IOException || error is UnauthorizedAccessException) { }
                }
                foreach (var (name, value) in pins) WriteNew(Path.Combine(_directory, name), value);
                // The key last: a folder with a key is one adoption never looks at again.
                WriteNew(Path.Combine(_directory, StaticKeyName), key);
                _adoptFrom = null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ThalovantConnectionException)
            {
                // The old folder could not be read: start afresh here, as a new
                // identity would.
            }
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
