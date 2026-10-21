using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Sillon.Push;

/// <summary>
/// The byte layouts of the sillon-push protocol (version 1): identifiers, the padded plaintext block, the envelope
/// (ECDH P-256, HKDF-SHA256, AES-256-GCM) and the signed send message. Pure functions, no state.
/// </summary>
public static class RelayCrypto
{
    /// <summary>Size of an X9.63 uncompressed P-256 public key.</summary>
    public const int PublicKeySize = 65;

    /// <summary>Size of the padded plaintext block.</summary>
    public const int BlockSize = 1024;

    /// <summary>Size of the AES-GCM nonce.</summary>
    public const int NonceSize = 12;

    /// <summary>Size of the AES-GCM tag.</summary>
    public const int TagSize = 16;

    /// <summary>Size of the envelope: version, ephemeral key, nonce, ciphertext, tag.</summary>
    public const int EnvelopeSize = 1 + PublicKeySize + NonceSize + BlockSize + TagSize;

    private const byte EnvelopeVersion = 1;
    private static readonly byte[] AadTag = Encoding.ASCII.GetBytes("SILLON-PUSH-AAD-V1\0");
    private static readonly byte[] SendTag = Encoding.ASCII.GetBytes("SILLON-PUSH-SEND-V1\0");
    private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("SILLON-PUSH-V1 key");

    /// <summary>Encodes bytes as base64url without padding.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The text.</returns>
    public static string B64(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url without padding.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The bytes, or <c>null</c> when the text is not canonical base64url.</returns>
    public static byte[]? FromB64(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4096 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            return null;
        }

        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            var bytes = Convert.FromBase64String(padded);
            return B64(bytes) == text ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The device id: SHA-256 of the 65-byte device public key.</summary>
    /// <param name="devicePublicKey">The key.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] DeviceId(ReadOnlySpan<byte> devicePublicKey) => SHA256.HashData(devicePublicKey);

    /// <summary>The server key id: SHA-256 of the 65-byte server public key.</summary>
    /// <param name="serverPublicKey">The key.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] Kid(ReadOnlySpan<byte> serverPublicKey) => SHA256.HashData(serverPublicKey);

    /// <summary>Tells whether the bytes are a valid uncompressed P-256 point (65 bytes, <c>0x04</c>, on the curve).</summary>
    /// <param name="key">The bytes.</param>
    /// <returns><c>true</c> when valid.</returns>
    public static bool IsValidPublicKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != PublicKeySize || key[0] != 0x04)
        {
            return false;
        }

        try
        {
            using var ecdh = ImportPublic(key);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Exports the public key of a P-256 key as 65 X9.63 bytes.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ExportPublic(ECDiffieHellman key) => ToX963(key.ExportParameters(false));

    /// <summary>Exports the public key of a P-256 signing key as 65 X9.63 bytes.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The bytes.</returns>
    public static byte[] ExportPublic(ECDsa key) => ToX963(key.ExportParameters(false));

    /// <summary>Imports a 65-byte X9.63 public key for key agreement.</summary>
    /// <param name="key">The bytes.</param>
    /// <returns>The key.</returns>
    public static ECDiffieHellman ImportPublic(ReadOnlySpan<byte> key)
        => ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Point(key) });

    /// <summary>Imports a 65-byte X9.63 public key for signature verification.</summary>
    /// <param name="key">The bytes.</param>
    /// <returns>The key.</returns>
    public static ECDsa ImportVerifyKey(ReadOnlySpan<byte> key)
        => ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = Point(key) });

    /// <summary>The plaintext block: length (u16 big-endian), the event JSON, zeros up to 1024 bytes.</summary>
    /// <param name="eventJson">The event JSON, at most 1022 bytes.</param>
    /// <returns>1024 bytes.</returns>
    public static byte[] PlaintextBlock(ReadOnlySpan<byte> eventJson)
    {
        if (eventJson.Length > BlockSize - 2)
        {
            throw new ArgumentException("The event is larger than 1022 bytes.", nameof(eventJson));
        }

        var block = new byte[BlockSize];
        BinaryPrimitives.WriteUInt16BigEndian(block, (ushort)eventJson.Length);
        eventJson.CopyTo(block.AsSpan(2));
        return block;
    }

    /// <summary>The additional authenticated data of an envelope (99 bytes).</summary>
    /// <param name="kid">The server key id.</param>
    /// <param name="deviceId">The device id.</param>
    /// <param name="ts">Unix seconds.</param>
    /// <param name="seq">The sequence number.</param>
    /// <returns>The AAD.</returns>
    public static byte[] Aad(ReadOnlySpan<byte> kid, ReadOnlySpan<byte> deviceId, long ts, long seq)
    {
        var aad = new byte[AadTag.Length + 32 + 32 + 8 + 8];
        AadTag.CopyTo(aad, 0);
        kid.CopyTo(aad.AsSpan(AadTag.Length));
        deviceId.CopyTo(aad.AsSpan(AadTag.Length + 32));
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(AadTag.Length + 64), (ulong)ts);
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(AadTag.Length + 72), (ulong)seq);
        return aad;
    }

    /// <summary>The AES-256 key of an envelope: HKDF-SHA256 over the raw ECDH x coordinate.</summary>
    /// <param name="ephemeral">The ephemeral private key.</param>
    /// <param name="devicePublicKey">The device public key.</param>
    /// <returns>32 bytes.</returns>
    public static byte[] DeriveKey(ECDiffieHellman ephemeral, ReadOnlySpan<byte> devicePublicKey)
    {
        using var device = ImportPublic(devicePublicKey);

        // the raw x coordinate, not the hashed DeriveKeyMaterial variant
        var shared = ephemeral.DeriveRawSecretAgreement(device.PublicKey);
        var salt = new byte[PublicKeySize * 2];
        ExportPublic(ephemeral).CopyTo(salt, 0);
        devicePublicKey.CopyTo(salt.AsSpan(PublicKeySize));
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, KeyInfo);
    }

    /// <summary>Builds the 1118-byte envelope.</summary>
    /// <param name="ephemeral">A fresh ephemeral P-256 key.</param>
    /// <param name="devicePublicKey">The device public key.</param>
    /// <param name="nonce">A fresh 12-byte nonce.</param>
    /// <param name="block">The 1024-byte plaintext block.</param>
    /// <param name="aad">The additional authenticated data.</param>
    /// <returns>The envelope.</returns>
    public static byte[] Seal(ECDiffieHellman ephemeral, ReadOnlySpan<byte> devicePublicKey, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> block, ReadOnlySpan<byte> aad)
    {
        if (nonce.Length != NonceSize || block.Length != BlockSize)
        {
            throw new ArgumentException("Wrong nonce or block size.");
        }

        var key = DeriveKey(ephemeral, devicePublicKey);
        var envelope = new byte[EnvelopeSize];
        envelope[0] = EnvelopeVersion;
        ExportPublic(ephemeral).CopyTo(envelope, 1);
        nonce.CopyTo(envelope.AsSpan(1 + PublicKeySize));
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(
            nonce,
            block,
            envelope.AsSpan(1 + PublicKeySize + NonceSize, BlockSize),
            envelope.AsSpan(1 + PublicKeySize + NonceSize + BlockSize, TagSize),
            aad);
        CryptographicOperations.ZeroMemory(key);
        return envelope;
    }

    /// <summary>The message a server signs for one notification (100 bytes).</summary>
    /// <param name="deviceId">The device id.</param>
    /// <param name="ts">Unix seconds.</param>
    /// <param name="seq">The sequence number.</param>
    /// <param name="envelope">The envelope.</param>
    /// <returns>The message.</returns>
    public static byte[] SendMessage(ReadOnlySpan<byte> deviceId, long ts, long seq, ReadOnlySpan<byte> envelope)
    {
        var message = new byte[SendTag.Length + 32 + 8 + 8 + 32];
        SendTag.CopyTo(message, 0);
        deviceId.CopyTo(message.AsSpan(SendTag.Length));
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(SendTag.Length + 32), (ulong)ts);
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(SendTag.Length + 40), (ulong)seq);
        SHA256.HashData(envelope, message.AsSpan(SendTag.Length + 48));
        return message;
    }

    /// <summary>Signs with ECDSA P-256 / SHA-256, raw <c>r || s</c> (IEEE P1363).</summary>
    /// <param name="key">The signing key.</param>
    /// <param name="message">The message.</param>
    /// <returns>64 bytes.</returns>
    public static byte[] Sign(ECDsa key, ReadOnlySpan<byte> message)
        => key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>Verifies an ECDSA P-256 / SHA-256 raw <c>r || s</c> signature.</summary>
    /// <param name="key">The verification key.</param>
    /// <param name="message">The message.</param>
    /// <param name="signature">The signature.</param>
    /// <returns><c>true</c> when it verifies.</returns>
    public static bool Verify(ECDsa key, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
        => key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static ECPoint Point(ReadOnlySpan<byte> key)
    {
        if (key.Length != PublicKeySize || key[0] != 0x04)
        {
            throw new CryptographicException("A public key is 65 bytes of an uncompressed point.");
        }

        return new ECPoint { X = key.Slice(1, 32).ToArray(), Y = key.Slice(33, 32).ToArray() };
    }

    private static byte[] ToX963(ECParameters parameters)
    {
        var key = new byte[PublicKeySize];
        key[0] = 0x04;
        parameters.Q.X!.CopyTo(key, 1 + (32 - parameters.Q.X.Length));
        parameters.Q.Y!.CopyTo(key, 33 + (32 - parameters.Q.Y.Length));
        return key;
    }
}
