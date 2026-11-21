using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Asn1.X9;

using System.Security.Cryptography;


namespace BTokenSigner;

public static class Crypto
{
  public const int LENGTH_HASH256 = 32;
  public const int LENGTH_HASH160 = 20;

  const string ALPHABET_BASE58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
  const int LENGTH_CHECKSUM_BASE58 = 4;

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

  public static string GenerateKeyPrivate()
  {
    Org.BouncyCastle.Security.SecureRandom random = new();

    while (true)
    {
      BigInteger d = new(DOMAIN_SECP256K1.N.BitLength, random);

      if (d.SignValue > 0 && d.CompareTo(DOMAIN_SECP256K1.N) < 0)
        return d.ToString();
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

  public static byte[] GetHash160KeyPublic(string keyPrivate)
  {
    return ComputeHash160(GetKeyPublic(keyPrivate));
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

  public static string BinaryToBase58Check(this byte[] byteArray)
  {
    List<byte> pubKey = byteArray.ToList();
    pubKey.Insert(0, 0x00);

    byte[] checksum = ComputeHash256(pubKey.ToArray());

    pubKey.AddRange(checksum.Take(LENGTH_CHECKSUM_BASE58));

    byte[] ba = pubKey.ToArray();

    BigInteger addrremain = new(1, ba);
    BigInteger big0 = new("0");
    BigInteger big58 = new("58");

    string rv = "";

    while (addrremain.CompareTo(big0) > 0)
    {
      int d = Convert.ToInt32(addrremain.Mod(big58).ToString());
      addrremain = addrremain.Divide(big58);
      rv = ALPHABET_BASE58.Substring(d, 1) + rv;
    }

    foreach (byte b in ba)
    {
      if (b != 0) break;
      rv = "1" + rv;
    }

    return rv;
  }

  public static byte[] Base58CheckToBinary(this string address)
  {
    System.Numerics.BigInteger value = 0;

    foreach (char character in address)
    {
      int digit = ALPHABET_BASE58.IndexOf(character);

      if (digit < 0)
        throw new FormatException($"Address {address} contains the character '{character}', which is not Base58.");

      value = value * 58 + digit;
    }

    byte[] bytes = [.. new byte[address.TakeWhile(c => c == ALPHABET_BASE58[0]).Count()], .. value.ToByteArray(isUnsigned: true, isBigEndian: true)];

    int lengthPayload = 1 + LENGTH_HASH160;

    if (bytes.Length != lengthPayload + LENGTH_CHECKSUM_BASE58
      || bytes[0] != 0x00
      || !ComputeHash256(bytes.AsSpan(0, lengthPayload)).AsSpan(0, LENGTH_CHECKSUM_BASE58).SequenceEqual(bytes.AsSpan(lengthPayload)))
      throw new FormatException($"Address {address} is not a valid Base58Check address.");

    return bytes[1..lengthPayload];
  }
}
