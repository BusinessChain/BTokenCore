using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;


namespace BTokenLib
{
  public class Wallet
  {
    public SHA256 SHA256 = SHA256.Create();

    public string KeyPrivateDecimal;
    public byte[] KeyPublic;
    public byte[] Hash160PKeyPublic = new byte[20];
    public string AddressAccount;


    public Wallet(string privKeyDec)
    {
      KeyPrivateDecimal = privKeyDec;

      KeyPublic = Crypto.GetPubKeyFromPrivKey(KeyPrivateDecimal);

      Hash160PKeyPublic = Crypto.ComputeHash160(KeyPublic, SHA256);

      AddressAccount = Hash160PKeyPublic.BinaryToBase58Check();
    }

    public byte[] GetSignature(byte[] dataToBeSigned)
    {
      return Crypto.GetSignature(KeyPrivateDecimal, dataToBeSigned);
    }
  }
}