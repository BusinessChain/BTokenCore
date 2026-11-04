using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Asn1.X9;

using System.Security.Cryptography;


namespace BTokenCore;

public static class Crypto
{
  internal const int LENGTH_HASH256 = 32;
  internal const int LENGTH_HASH160 = 20;

  static readonly X9ECParameters CURVE_SECP256K1 = SecNamedCurves.GetByName("secp256k1");

  static readonly ECDomainParameters DOMAIN_SECP256K1 = new(
    CURVE_SECP256K1.Curve,
    CURVE_SECP256K1.G,
    CURVE_SECP256K1.N,
    CURVE_SECP256K1.H);

  public static bool VerifySignature(
    byte[] buffer,
    int startIndexMessage,
    int lengthMessage,
    byte[] keyPublic,
    byte[] signature)
  {
    ECPublicKeyParameters keyParameters = new(
      DOMAIN_SECP256K1.Curve.DecodePoint(keyPublic),
      DOMAIN_SECP256K1);

    ISigner signer = SignerUtilities.GetSigner("SHA-256withECDSA");

    signer.Init(false, keyParameters);
    signer.BlockUpdate(buffer, startIndexMessage, lengthMessage);

    return signer.VerifySignature(signature);
  }

  public static byte[] GetSignature(string keyPrivate, byte[] message)
  {
    ECPrivateKeyParameters keyParameters = new(new BigInteger(keyPrivate), DOMAIN_SECP256K1);

    ISigner signer = SignerUtilities.GetSigner("SHA-256withECDSA");

    while (true)
    {
      signer.Init(true, keyParameters);
      signer.BlockUpdate(message, 0, message.Length);

      byte[] signature = signer.GenerateSignature();

      if (signature[signature[3] + 5] > 32)
        continue;

      return signature;
    }
  }

  public static byte[] GetKeyPublic(string keyPrivate)
  {
    var d = new BigInteger(keyPrivate);
    var q = DOMAIN_SECP256K1.G.Multiply(d);

    var keyParameters = new ECPublicKeyParameters(q, DOMAIN_SECP256K1);

    List<byte> keyPublic = keyParameters.Q.XCoord.GetEncoded().ToList();

    byte lastByteY = keyParameters.Q.YCoord.GetEncoded().Last();

    if ((lastByteY & 0x01) == 0x00)
      keyPublic.Insert(0, 0x02);
    else
      keyPublic.Insert(0, 0x03);

    return keyPublic.ToArray();
  }

  public static byte[] ComputeHash160(byte[] data)
  {
    byte[] hash160 = new byte[LENGTH_HASH160];

    var hashSHA256 = SHA256.HashData(data);

    RipeMD160Digest RIPEMD160 = new();
    RIPEMD160.BlockUpdate(hashSHA256, 0, hashSHA256.Length);
    RIPEMD160.DoFinal(hash160, 0);

    return hash160;
  }

  public static byte[] ComputeHash256(ReadOnlySpan<byte> data)
  {
    return SHA256.HashData(SHA256.HashData(data));
  }
}
