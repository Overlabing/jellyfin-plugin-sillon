using System.Security.Cryptography;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>
/// The server's signing key pair (ECDSA P-256), generated once and kept as PKCS#8 PEM in a file of the plugin's data folder,
/// never in the XML configuration. Only the public half ever leaves this class. Rotation is deleting the file.
/// </summary>
public sealed class ServerKeyStore : IDisposable
{
    /// <summary>The file name inside the data folder.</summary>
    public const string FileName = "relay-server-key.pem";

    private readonly object _lock = new();
    private readonly string _path;
    private ECDsa? _key;
    private byte[]? _publicKey;

    /// <summary>Initializes a new instance of the <see cref="ServerKeyStore"/> class.</summary>
    /// <param name="directory">The plugin's data folder.</param>
    public ServerKeyStore(string directory) => _path = Path.Combine(directory, FileName);

    /// <summary>Gets the server public key, 65 bytes (X9.63).</summary>
    public byte[] PublicKey
    {
        get
        {
            Load();
            return (byte[])_publicKey!.Clone();
        }
    }

    /// <summary>Gets the public key as base64url.</summary>
    public string PublicKeyText => RelayCrypto.B64(PublicKey);

    /// <summary>Gets the key id: SHA-256 of the public key.</summary>
    public byte[] Kid => RelayCrypto.Kid(PublicKey);

    /// <summary>Gets the key id as base64url.</summary>
    public string KidText => RelayCrypto.B64(Kid);

    /// <summary>Signs a message (ECDSA P-256 / SHA-256, raw r || s).</summary>
    /// <param name="message">The message.</param>
    /// <returns>64 bytes.</returns>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        Load();
        lock (_lock)
        {
            return RelayCrypto.Sign(_key!, message);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _key?.Dispose();

    private void Load()
    {
        lock (_lock)
        {
            if (_key is not null)
            {
                return;
            }

            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(_path))
            {
                key.ImportFromPem(File.ReadAllText(_path));
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var pem = key.ExportPkcs8PrivateKeyPem();
                var temp = _path + ".tmp";
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                using (var stream = new FileStream(temp, options))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(pem);
                }

                File.Move(temp, _path, overwrite: true);
            }

            _publicKey = RelayCrypto.ExportPublic(key);
            _key = key;
        }
    }
}
