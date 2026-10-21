using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Sillon.Push;
using Xunit;

namespace Jellyfin.Plugin.Sillon.Tests;

/// <summary>The byte layouts of the sillon-push protocol against the test vectors of section 14 (produced by the Go reference).</summary>
public class RelayCryptoTests
{
    internal const string DevicePrivate = "1111111111111111111111111111111111111111111111111111111111111111";
    internal const string DevicePublic = "040217e617f0b6443928278f96999e69a23a4f2c152bdf6d6cdf66e5b80282d4ed194a7debcb97712d2dda3ca85aa8765a56f45fc758599652f2897c65306e5794";
    internal const string DeviceIdHex = "2bad0fd610d99eae443e932a26142bca1e5fa995b4518452827e78ef1f317ff0";
    internal const string EphemeralPrivate = "2222222222222222222222222222222222222222222222222222222222222222";
    internal const string ServerPrivate = "3333333333333333333333333333333333333333333333333333333333333333";
    internal const string ServerPublic = "0451a7580833898ea1b183cbd7350a4099078c6ef1c1e18e970cd7683035f25e7d0110522712b0b5a7cff081685486984a94e6831edac46e7360fa9d834a7a81a1";
    internal const string KidHex = "727235892575dc68a1db851287f453e58b458acd43bfae6dd57aaf295e9fb644";
    internal const long Ts = 1790000000;
    internal const long Seq = 1790000000123;
    internal const string Nonce = "444444444444444444444444";
    internal const string EventJson = "{\"v\":1,\"type\":\"request.available\",\"request_id\":\"r-1\",\"album_id\":\"a-1\",\"title\":\"Kind of Blue\",\"artist\":\"Miles Davis\",\"tracks_imported\":5,\"tracks_total\":5}";

    private const string Aad = "53494c4c4f4e2d505553482d4141442d563100727235892575dc68a1db851287f453e58b458acd43bfae6dd57aaf295e9fb6442bad0fd610d99eae443e932a26142bca1e5fa995b4518452827e78ef1f317ff0000000006ab13b80000001a0c4506c7b";
    private const string SharedX = "ccfc261f58193c98ca4ad4a53bbac6f0ee29bc4d48438090446908622ca79af6";
    private const string AesKey = "8ea926819524de43b8252482aba573a3259cfff0f5b9432b5cd2d0899fa3bc60";

    private static readonly Dictionary<string, string> Vectors = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "relay-vectors.txt"))
        .Select(l => l.Split('=', 2))
        .ToDictionary(p => p[0], p => p[1]);

    internal static byte[] Hex(string hex) => Convert.FromHexString(hex);

    internal static string HexOf(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    // a private key from its scalar and its public point, the form .NET imports
    internal static ECDiffieHellman AgreementKey(string privateHex, string publicHex)
    {
        var q = Hex(publicHex);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Hex(privateHex),
            Q = new ECPoint { X = q[1..33], Y = q[33..65] }
        });
    }

    internal static ECDsa SigningKey(string privateHex, string publicHex)
    {
        var q = Hex(publicHex);
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Hex(privateHex),
            Q = new ECPoint { X = q[1..33], Y = q[33..65] }
        });
    }

    // the ephemeral key of the vectors: its public half is the one inside the envelope
    internal static ECDiffieHellman VectorEphemeral() => AgreementKey(EphemeralPrivate, HexOf(Hex(Vectors["envelope"])[1..66]));

    [Fact]
    public void TheVectorKeysAreWhatTheDocumentSays()
    {
        using var device = AgreementKey(DevicePrivate, DevicePublic);
        using var server = SigningKey(ServerPrivate, ServerPublic);

        Assert.Equal(DevicePublic, HexOf(RelayCrypto.ExportPublic(device)));
        Assert.Equal(ServerPublic, HexOf(RelayCrypto.ExportPublic(server)));
        Assert.Equal(HexOf(Hex(Vectors["envelope"])[1..66]), HexOf(RelayCrypto.ExportPublic(VectorEphemeral())));

        // the scalar alone gives the same public point: the key pairs of the document are consistent
        using var fromScalar = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = Hex(EphemeralPrivate), Q = default });
        Assert.Equal(HexOf(Hex(Vectors["envelope"])[1..66]), HexOf(RelayCrypto.ExportPublic(fromScalar)));
    }

    [Fact]
    public void DeviceIdAndKid_MatchTheVectors()
    {
        Assert.Equal(DeviceIdHex, HexOf(RelayCrypto.DeviceId(Hex(DevicePublic))));
        Assert.Equal("K60P1hDZnq5EPpMqJhQryh5fqZW0UYRSgn547x8xf_A", RelayCrypto.B64(RelayCrypto.DeviceId(Hex(DevicePublic))));
        Assert.Equal(KidHex, HexOf(RelayCrypto.Kid(Hex(ServerPublic))));
        Assert.Equal("cnI1iSV13Gih24USh_RT5YtFis1Dv65t1XqvKV6ftkQ", RelayCrypto.B64(RelayCrypto.Kid(Hex(ServerPublic))));
        Assert.Equal("BAIX5hfwtkQ5KCePlpmeaaI6TywVK99tbN9m5bgCgtTtGUp968uXcS0t2jyoWqh2Wlb0X8dYWZZS8ol8ZTBuV5Q", RelayCrypto.B64(Hex(DevicePublic)));
    }

    [Fact]
    public void PlaintextBlock_IsLengthJsonAndZeros()
    {
        var json = Encoding.UTF8.GetBytes(EventJson);

        var block = RelayCrypto.PlaintextBlock(json);

        Assert.Equal(153, json.Length);
        Assert.Equal(1024, block.Length);
        Assert.Equal("0099", HexOf(block[..2]));
        Assert.Equal(json, block[2..155]);
        Assert.All(block[155..], b => Assert.Equal(0, b));
        Assert.Equal(869, block.Length - 155);
    }

    [Fact]
    public void PlaintextBlock_RefusesAnEventLargerThan1022Bytes()
    {
        Assert.Equal(1024, RelayCrypto.PlaintextBlock(new byte[1022]).Length);
        Assert.Throws<ArgumentException>(() => RelayCrypto.PlaintextBlock(new byte[1023]));
    }

    [Fact]
    public void Aad_MatchesTheVector()
    {
        var aad = RelayCrypto.Aad(Hex(KidHex), Hex(DeviceIdHex), Ts, Seq);

        Assert.Equal(99, aad.Length);
        Assert.Equal(Aad, HexOf(aad));
    }

    [Fact]
    public void RawEcdhSecret_IsTheXCoordinateOfTheVector()
    {
        using var ephemeral = VectorEphemeral();
        using var device = RelayCrypto.ImportPublic(Hex(DevicePublic));

        // DeriveRawSecretAgreement, not the hashed DeriveKeyMaterial
        Assert.Equal(SharedX, HexOf(ephemeral.DeriveRawSecretAgreement(device.PublicKey)));
    }

    [Fact]
    public void HkdfKey_MatchesTheVector()
    {
        using var ephemeral = VectorEphemeral();

        Assert.Equal(AesKey, HexOf(RelayCrypto.DeriveKey(ephemeral, Hex(DevicePublic))));
    }

    [Fact]
    public void Envelope_MatchesTheVectorByteForByte()
    {
        using var ephemeral = VectorEphemeral();
        var block = RelayCrypto.PlaintextBlock(Encoding.UTF8.GetBytes(EventJson));
        var aad = RelayCrypto.Aad(Hex(KidHex), Hex(DeviceIdHex), Ts, Seq);

        var envelope = RelayCrypto.Seal(ephemeral, Hex(DevicePublic), Hex(Nonce), block, aad);

        Assert.Equal(1118, envelope.Length);
        Assert.Equal(RelayCrypto.EnvelopeSize, envelope.Length);
        Assert.Equal(Vectors["envelope"], HexOf(envelope));
        Assert.Equal(Vectors["envelope_b64u"], RelayCrypto.B64(envelope));
    }

    [Fact]
    public void Envelope_OpensWithTheDeviceKey_AndOnlyWithTheRightAad()
    {
        var envelope = Hex(Vectors["envelope"]);
        using var device = AgreementKey(DevicePrivate, DevicePublic);

        var plain = Open(device, envelope, RelayCrypto.Aad(Hex(KidHex), Hex(DeviceIdHex), Ts, Seq));

        Assert.NotNull(plain);
        Assert.Equal(EventJson, Encoding.UTF8.GetString(plain![2..(2 + 153)]));
        Assert.Null(Open(device, envelope, RelayCrypto.Aad(Hex(KidHex), Hex(DeviceIdHex), Ts, Seq + 1)));
        Assert.Null(Open(device, envelope, RelayCrypto.Aad(Hex(KidHex), Hex(DeviceIdHex), Ts + 1, Seq)));
    }

    [Fact]
    public void SendMessage_MatchesTheVector()
    {
        var message = RelayCrypto.SendMessage(Hex(DeviceIdHex), Ts, Seq, Hex(Vectors["envelope"]));

        Assert.Equal(100, message.Length);
        Assert.Equal(Vectors["send_message"], HexOf(message));
    }

    [Fact]
    public void TheVectorSignature_VerifiesWithTheServerKey_AndOnlyForTheSameMessage()
    {
        using var verifier = RelayCrypto.ImportVerifyKey(Hex(ServerPublic));
        var signature = Hex(Vectors["signature"]);
        var envelope = Hex(Vectors["envelope"]);

        Assert.Equal(64, signature.Length);
        Assert.True(RelayCrypto.Verify(verifier, RelayCrypto.SendMessage(Hex(DeviceIdHex), Ts, Seq, envelope), signature));

        // any of device id, ts, seq, or one byte of the envelope breaks it
        var otherDevice = Hex(DeviceIdHex);
        otherDevice[0] ^= 1;
        var otherEnvelope = (byte[])envelope.Clone();
        otherEnvelope[500] ^= 1;
        Assert.False(RelayCrypto.Verify(verifier, RelayCrypto.SendMessage(otherDevice, Ts, Seq, envelope), signature));
        Assert.False(RelayCrypto.Verify(verifier, RelayCrypto.SendMessage(Hex(DeviceIdHex), Ts + 1, Seq, envelope), signature));
        Assert.False(RelayCrypto.Verify(verifier, RelayCrypto.SendMessage(Hex(DeviceIdHex), Ts, Seq + 1, envelope), signature));
        Assert.False(RelayCrypto.Verify(verifier, RelayCrypto.SendMessage(Hex(DeviceIdHex), Ts, Seq, otherEnvelope), signature));
    }

    [Fact]
    public void ASignatureWeMakeWithTheVectorServerKey_VerifiesAndIsRawRS()
    {
        using var server = SigningKey(ServerPrivate, ServerPublic);
        using var verifier = RelayCrypto.ImportVerifyKey(Hex(ServerPublic));
        var message = RelayCrypto.SendMessage(Hex(DeviceIdHex), Ts, Seq, Hex(Vectors["envelope"]));

        var signature = RelayCrypto.Sign(server, message);

        Assert.Equal(64, signature.Length);
        Assert.True(RelayCrypto.Verify(verifier, message, signature));
    }

    [Fact]
    public void Base64Url_IsUnpaddedAndCanonical()
    {
        Assert.Equal("AQI", RelayCrypto.B64(new byte[] { 1, 2 }));
        Assert.Equal(new byte[] { 1, 2 }, RelayCrypto.FromB64("AQI"));
        Assert.Null(RelayCrypto.FromB64("AQI="));
        Assert.Null(RelayCrypto.FromB64("AQ+/"));
        Assert.Null(RelayCrypto.FromB64("AQJ"));
        Assert.Null(RelayCrypto.FromB64(string.Empty));
        Assert.Null(RelayCrypto.FromB64(null));
    }

    [Fact]
    public void PublicKeys_AreOnlyUncompressedPointsOnTheCurve()
    {
        var good = Hex(DevicePublic);
        var offCurve = (byte[])good.Clone();
        offCurve[64] ^= 1;
        var compressed = new byte[33];
        compressed[0] = 0x02;

        Assert.True(RelayCrypto.IsValidPublicKey(good));
        Assert.False(RelayCrypto.IsValidPublicKey(offCurve));
        Assert.False(RelayCrypto.IsValidPublicKey(compressed));
        Assert.False(RelayCrypto.IsValidPublicKey(good[..64]));
        Assert.False(RelayCrypto.IsValidPublicKey(new byte[65]));
    }

    // opens an envelope the way the app does; null when it does not authenticate
    internal static byte[]? Open(ECDiffieHellman device, byte[] envelope, byte[] aad)
    {
        using var ephemeral = RelayCrypto.ImportPublic(envelope[1..66]);
        var shared = device.DeriveRawSecretAgreement(ephemeral.PublicKey);
        var salt = envelope[1..66].Concat(RelayCrypto.ExportPublic(device)).ToArray();
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.ASCII.GetBytes("SILLON-PUSH-V1 key"));
        var plain = new byte[1024];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.AsSpan(66, 12), envelope.AsSpan(78, 1024), envelope.AsSpan(1102, 16), plain, aad);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
