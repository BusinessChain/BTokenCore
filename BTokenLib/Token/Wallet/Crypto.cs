using System;
using System.Collections.Generic;

using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;

using System.Security.Cryptography;
using Org.BouncyCastle.Asn1.X9;
using System.Linq;

namespace BTokenLib
{
  public static class Crypto
  {       
    public static bool VerifySignature(
      byte[] buffer,
      int startIndexMessage,
      int lengthMessage,
      byte[] pubKeyX,
      byte[] signature)
    {
      X9ECParameters curve = SecNamedCurves.GetByName("secp256k1");

      ECPublicKeyParameters keyParameters = new(
        curve.Curve.DecodePoint(pubKeyX),
        new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H));

      ISigner signer = SignerUtilities.GetSigner("SHA-256withECDSA");

      signer.Init(false, keyParameters);
      signer.BlockUpdate(buffer, startIndexMessage, lengthMessage);

      return signer.VerifySignature(signature);
    }

    public static byte[] GetSignature(string privateKey, byte[] message)
    {
      var curve = SecNamedCurves.GetByName("secp256k1");

      ECPrivateKeyParameters keyParameters = new(
        new BigInteger(privateKey),
        new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H));

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

    public static byte[] GetPubKeyFromPrivKey(string privKey)
    {
      var curve = SecNamedCurves.GetByName("secp256k1");

      var domain = new ECDomainParameters(
        curve.Curve, 
        curve.G, 
        curve.N, 
        curve.H);

      var d = new BigInteger(privKey);
      var q = domain.G.Multiply(d);

      var publicKey = new ECPublicKeyParameters(q, domain);

      List<byte> pubKeyX = publicKey.Q.XCoord.GetEncoded().ToList();

      byte lasbByteY = publicKey.Q.YCoord.GetEncoded().Last();

      if ((lasbByteY & 0x01) == 0x00)
        pubKeyX.Insert(0, 0x02);
      else
        pubKeyX.Insert(0, 0x03);

      return pubKeyX.ToArray();
    }

    public static byte[] ComputeHash160(byte[] data, SHA256 sHA256)
    {
      byte[] publicKeyHash160 = new byte[20];

      var hashPublicKey = sHA256.ComputeHash(data);

      RipeMD160Digest RIPEMD160 = new();
      RIPEMD160.BlockUpdate(hashPublicKey, 0, hashPublicKey.Length);
      RIPEMD160.DoFinal(publicKeyHash160, 0);

      return publicKeyHash160;
    }
  }
}
